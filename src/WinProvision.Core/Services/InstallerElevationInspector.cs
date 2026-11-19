using System.Buffers.Binary;
using System.Xml;
using System.Xml.Linq;

namespace WinProvision.Core.Services;

public sealed record InstallerInspectionSignal(string Name, string Value);

public sealed record InstallerElevationInspection(string Decision, string Reason, InstallerInspectionSignal[] Signals,
    bool Complete = true, bool FromStorage = false)
{
    public static InstallerElevationInspection Unknown(string reason, bool complete = true) =>
        new("unknown", reason, [], complete);
}

public interface IInstallerElevationInspector
{
    string Version { get; }
    Task<InstallerElevationInspection> InspectAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Reads PE bytes or a read-only MSI database, selected by content. Never creates an installation session.
/// Format references and limits: docs/Elevation.md, Etapa 4b.
/// </summary>
public sealed class InstallerElevationInspector : IInstallerElevationInspector
{
    public string Version => "pe-msi-2";
    public const long MaximumFileBytes = 4L * 1024 * 1024 * 1024;
    public const int MaximumReadBytes = 1024 * 1024;
    public const int MaximumManifestBytes = 256 * 1024;
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim Readers = new(2, 2);
    private readonly Func<string, Stream> _open;
    private readonly TimeSpan _duration;
    private readonly MsiInstallerElevationReader _msiReader;

    // Evidence is cached independently of catalog type. Interpret the same PE again for each selected installer.
    public static InstallerElevationInspection ForInstallerType(InstallerElevationInspection inspection, string? installerType)
    {
        if (installerType?.Trim().ToLowerInvariant() is "inno" or "burn"
            && inspection.Decision == "not-required" && inspection.Reason == "pe-requested-execution-level"
            && inspection.Signals.Any(s => s.Name.StartsWith("pe.manifest.", StringComparison.Ordinal) && s.Value == "asInvoker"))
            return inspection with { Decision = "unknown", Reason = "asInvoker-does-not-confirm-installation-privileges" };
        return inspection;
    }

    public InstallerElevationInspector() : this(path => new FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.RandomAccess), MaximumDuration) { }

    internal InstallerElevationInspector(Func<string, Stream> open, TimeSpan duration, MsiInstallerElevationReader? msiReader = null)
    {
        _open = open;
        _duration = duration;
        _msiReader = msiReader ?? new MsiInstallerElevationReader();
    }

    public async Task<InstallerElevationInspection> InspectAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_duration);
        CancellationToken inspectionToken = budget.Token;
        // Even a filesystem open that ignores cancellation cannot hold up installation.
        // At most two workers may remain in OS I/O; late tasks are always observed.
        Task<InstallerElevationInspection> pending = Task.Run(async () =>
        {
            await Readers.WaitAsync(inspectionToken).ConfigureAwait(false);
            try
            {
                inspectionToken.ThrowIfCancellationRequested();
                using Stream stream = _open(path);
                var reader = new ByteReader(stream, inspectionToken);
                if (reader.Length > MaximumFileBytes) return InstallerElevationInspection.Unknown("file-size-limit", false);
                if (reader.Length >= 8 && (await reader.ReadAsync(0, 8).ConfigureAwait(false)).AsSpan()
                    .SequenceEqual(new byte[] { 0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1 }))
                    return await _msiReader.InspectAsync(path, inspectionToken).ConfigureAwait(false);
                return await ReadPeAsync(reader).ConfigureAwait(false);
            }
            finally { Readers.Release(); }
        }, CancellationToken.None);
        try
        {
            InstallerElevationInspection result = await pending.WaitAsync(inspectionToken).ConfigureAwait(false);
            inspectionToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Observe(pending);
            return InstallerElevationInspection.Unknown("time-limit", false);
        }
        catch (OperationCanceledException) { Observe(pending); throw; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or XmlException or OverflowException)
        {
            return InstallerElevationInspection.Unknown("unreadable-or-malformed", false);
        }
    }

    private static void Observe(Task task) => _ = task.ContinueWith(t => _ = t.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

    private static async Task<InstallerElevationInspection> ReadPeAsync(ByteReader reader)
    {
        if (reader.Length > MaximumFileBytes) return InstallerElevationInspection.Unknown("file-size-limit", false);
        if (reader.Length < 64) return InstallerElevationInspection.Unknown("not-pe");
        byte[] dos = await reader.ReadAsync(0, 64).ConfigureAwait(false);
        if (U16(dos, 0) != 0x5a4d) return InstallerElevationInspection.Unknown("not-pe");
        uint peOffset = U32(dos, 0x3c);
        if (peOffset < 64) throw new InvalidDataException();
        byte[] coff = await reader.ReadAsync(peOffset, 24).ConfigureAwait(false);
        if (U32(coff, 0) != 0x00004550) return InstallerElevationInspection.Unknown("not-pe");
        int sectionCount = U16(coff, 6), optionalSize = U16(coff, 20);
        if (sectionCount is < 1 or > 96 || optionalSize is < 2 or > 4096)
            throw new InvalidDataException();
        byte[] optional = await reader.ReadAsync((long)peOffset + 24, optionalSize).ConfigureAwait(false);
        // Microsoft PE format: PE32/PE32+, NumberOfRvaAndSizes and third DataDirectory entry.
        int directories = U16(optional, 0) switch { 0x10b => 96, 0x20b => 112, _ => 0 };
        if (directories == 0 || optional.Length < directories + 24) throw new InvalidDataException();
        if (U32(optional, directories - 4) < 3) return InstallerElevationInspection.Unknown("no-resource-directory");
        uint resourceRva = U32(optional, directories + 16), resourceSize = U32(optional, directories + 20);
        if (resourceRva == 0 || resourceSize == 0) return InstallerElevationInspection.Unknown("no-resource-directory");
        byte[] sections = await reader.ReadAsync((long)peOffset + 24 + optionalSize, sectionCount * 40).ConfigureAwait(false);
        var map = new SectionMap(sections);
        long resourceStart = map.Offset(resourceRva, resourceSize);
        var tree = new ResourceTree(reader, resourceStart, resourceSize);
        uint? type = await tree.FindDirectoryAsync(0, 24).ConfigureAwait(false); // RT_MANIFEST
        if (type is null) return InstallerElevationInspection.Unknown("no-manifest");
        uint? name = await tree.FindDirectoryAsync(type.Value, 1).ConfigureAwait(false); // process default
        if (name is null) return InstallerElevationInspection.Unknown("no-process-manifest");
        var languages = await tree.EntriesAsync(name.Value).ConfigureAwait(false);
        if (languages.Length == 0) throw new InvalidDataException();
        var signals = new List<InstallerInspectionSignal>();
        string? common = null;
        bool ambiguous = false;
        foreach (var (id, target) in languages)
        {
            if ((id & 0x80000000) != 0 || (target & 0x80000000) != 0) throw new InvalidDataException();
            byte[] data = await tree.ReadAsync(target, 16).ConfigureAwait(false);
            uint size = U32(data, 4);
            if (size is 0 or > MaximumManifestBytes) throw new InvalidDataException();
            byte[] xml = await reader.ReadAsync(map.Offset(U32(data, 0), size), checked((int)size)).ConfigureAwait(false);
            string? level = ReadLevel(xml);
            signals.Add(new($"pe.manifest.1.{id}.requestedExecutionLevel", level ?? "absent-or-unsupported"));
            if (level is null || common is not null && common != level) ambiguous = true;
            common ??= level;
        }
        string decision = ambiguous ? "unknown" : common switch
        {
            "requireAdministrator" => "required",
            "asInvoker" => "not-required",
            _ => "unknown" // highestAvailable is conditional; it is not an unconditional requirement.
        };
        return new(decision, decision == "unknown" ? "manifest-inconclusive" : "pe-requested-execution-level", signals.ToArray());
    }

    private static string? ReadLevel(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var xml = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaximumManifestBytes, IgnoreComments = true
        });
        XElement root = XElement.Load(xml);
        XNamespace v1 = "urn:schemas-microsoft-com:asm.v1", v2 = "urn:schemas-microsoft-com:asm.v2", v3 = "urn:schemas-microsoft-com:asm.v3";
        if (root.Name != v1 + "assembly" || (string?)root.Attribute("manifestVersion") != "1.0") return null;
        XElement[] levels = root.Elements().Where(e => e.Name == v2 + "trustInfo" || e.Name == v3 + "trustInfo")
            .SelectMany(t => t.Elements(t.Name.Namespace + "security"))
            .SelectMany(s => s.Elements().Where(p => p.Name.LocalName == "requestedPrivileges"
                && (p.Name.Namespace == v3 || p.Name.Namespace == s.Name.Namespace)))
            .SelectMany(p => p.Elements(v3 + "requestedExecutionLevel")).ToArray();
        if (levels.Length != 1) return null;
        string? uiAccess = (string?)levels[0].Attribute("uiAccess");
        if (uiAccess is not (null or "false" or "0")) return null; // requires a separate security policy
        return (string?)levels[0].Attribute("level") is "requireAdministrator" or "asInvoker" or "highestAvailable"
            ? (string?)levels[0].Attribute("level") : null;
    }

    private static ushort U16(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private sealed class ByteReader(Stream stream, CancellationToken token)
    {
        private int _read;
        public long Length { get; } = stream.Length;
        public async Task<byte[]> ReadAsync(long offset, int count)
        {
            token.ThrowIfCancellationRequested();
            if (offset < 0 || count < 0 || offset > Length - count || count > MaximumReadBytes - _read)
                throw new InvalidDataException();
            _read += count;
            stream.Seek(offset, SeekOrigin.Begin);
            byte[] bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return bytes;
        }
    }

    private sealed class SectionMap(byte[] sections)
    {
        public long Offset(uint rva, uint size)
        {
            long? found = null;
            for (int i = 0; i < sections.Length; i += 40)
            {
                uint start = U32(sections, i + 12), rawSize = U32(sections, i + 16), rawOffset = U32(sections, i + 20);
                if (rva < start || (ulong)rva - start + size > rawSize) continue;
                if (found is not null) throw new InvalidDataException(); // overlapping mappings are ambiguous
                found = (long)rawOffset + rva - start;
            }
            return found ?? throw new InvalidDataException();
        }
    }

    private sealed class ResourceTree(ByteReader reader, long start, uint size)
    {
        public Task<byte[]> ReadAsync(uint offset, int count)
        {
            if ((ulong)offset + (uint)count > size) throw new InvalidDataException();
            return reader.ReadAsync(start + offset, count);
        }
        public async Task<(uint Id, uint Target)[]> EntriesAsync(uint offset)
        {
            byte[] header = await ReadAsync(offset, 16).ConfigureAwait(false);
            int count = U16(header, 12) + U16(header, 14);
            if (count > 256) throw new InvalidDataException();
            byte[] entries = await ReadAsync(checked(offset + 16), count * 8).ConfigureAwait(false);
            return Enumerable.Range(0, count).Select(i => (U32(entries, i * 8), U32(entries, i * 8 + 4))).ToArray();
        }
        public async Task<uint?> FindDirectoryAsync(uint offset, uint id)
        {
            var matches = (await EntriesAsync(offset).ConfigureAwait(false)).Where(e => e.Id == id).ToArray();
            if (matches.Length == 0) return null;
            if (matches.Length != 1 || (matches[0].Target & 0x80000000) == 0) throw new InvalidDataException();
            return matches[0].Target & 0x7fffffff;
        }
    }
}
