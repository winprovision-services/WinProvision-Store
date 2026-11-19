using System.Buffers.Binary;
using System.Text;

namespace WinProvision.Core.Services;

public enum LanInstallerKind
{
    Unknown,
    Msi,
    InnoSetup,
    Nsis,
    Burn,
    InstallShield
}

public sealed record LanInstallerDetection(LanInstallerKind Kind, string ModuleType, string DisplayName);

public enum LanInstallerArgumentSource
{
    Unsupported,
    TypeDefault,
    Server,
    Manual,
    Manifest,
    Zip,
    Collection,
    Detected
}

public sealed record LanInstallerArguments(string? Value, LanInstallerArgumentSource Source);

public static class LanInstallerArgumentsPolicy
{
    public static string? DefaultInstallArguments(LanInstallerKind kind) =>
        InstallerSilentArgumentsResolver.DefaultInstallArguments(ModuleType(kind));

    public static string? DefaultUninstallArguments(LanInstallerKind kind, string? productCode = null) => kind switch
    {
        LanInstallerKind.Msi when Guid.TryParse(productCode, out Guid id) && id != Guid.Empty =>
            $"/x {{{id:D}}} /qn /norestart",
        LanInstallerKind.InnoSetup => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART",
        LanInstallerKind.Nsis => "/S",
        LanInstallerKind.Burn => "/quiet /norestart",
        _ => null
    };

    public static LanInstallerArguments Resolve(
        LanInstallerKind kind,
        string? externalArguments,
        string? manualArguments,
        string? externalSource = null,
        bool allowManualArguments = true,
        bool allowExternalArguments = true)
    {
        InstallerSilentArgumentSource source = string.IsNullOrWhiteSpace(externalSource)
            ? InstallerSilentArgumentSource.Server
            : InstallerSilentArgumentsResolver.ParseSourceName(externalSource);
        InstallerSilentArgumentResolution resolution = InstallerSilentArgumentsResolver.Resolve(
            manualArguments, externalArguments, null, null, ModuleType(kind),
            providedSource: source,
            allowManualArguments: allowManualArguments,
            allowExternalArguments: allowExternalArguments);
        return new(resolution.Arguments, resolution.Source switch
        {
            InstallerSilentArgumentSource.Manual => LanInstallerArgumentSource.Manual,
            InstallerSilentArgumentSource.Server => LanInstallerArgumentSource.Server,
            InstallerSilentArgumentSource.Manifest => LanInstallerArgumentSource.Manifest,
            InstallerSilentArgumentSource.Zip => LanInstallerArgumentSource.Zip,
            InstallerSilentArgumentSource.Collection => LanInstallerArgumentSource.Collection,
            InstallerSilentArgumentSource.TypeDefault => LanInstallerArgumentSource.TypeDefault,
            InstallerSilentArgumentSource.Detected => LanInstallerArgumentSource.Detected,
            _ => LanInstallerArgumentSource.Unsupported
        });
    }

    private static string? ModuleType(LanInstallerKind kind) => kind switch
    {
        LanInstallerKind.Msi => "msi",
        LanInstallerKind.InnoSetup => "inno",
        LanInstallerKind.Nsis => "nullsoft",
        LanInstallerKind.Burn => "burn",
        _ => null
    };
}

public static class LanInstallerSelectionPolicy
{
    public static bool SelectByDefault(bool hashVerified, bool typeMatches, bool supportsSilentInstall, bool argumentsAreSafe) =>
        hashVerified && typeMatches && supportsSilentInstall && argumentsAreSafe;
}

public static class LanInstallerTypeDetector
{
    private static readonly byte[] CompoundFileSignature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private static readonly byte[] InnoDataSignature = Encoding.ASCII.GetBytes("Inno Setup Setup Data");
    private static readonly byte[] NsisSignature = Encoding.ASCII.GetBytes("NullsoftInst");
    private const int MaximumSections = 96;
    private const int MaximumOverlayScanBytes = 1024 * 1024;
    private const int ScanBufferBytes = 64 * 1024;

    public static LanInstallerDetection Detect(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            ScanBufferBytes, FileOptions.SequentialScan);
        return Detect(stream, Path.GetFileName(filePath));
    }

    public static LanInstallerDetection Detect(Stream stream, string fileName)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
            throw new ArgumentException("O detector requer um fluxo legível e pesquisável.", nameof(stream));

        if (Path.GetExtension(fileName).Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            Span<byte> signature = stackalloc byte[CompoundFileSignature.Length];
            stream.Position = 0;
            if (ReadAtMost(stream, signature) == signature.Length
                && signature.SequenceEqual(CompoundFileSignature))
                return Create(LanInstallerKind.Msi, "msi", "MSI");
            return Create(LanInstallerKind.Unknown, "exe", "Desconhecido");
        }

        if (!Path.GetExtension(fileName).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || !TryReadPeSections(stream, out IReadOnlyList<string> sections, out long overlayOffset))
            return Create(LanInstallerKind.Unknown, "exe", "Desconhecido");

        if (sections.Contains(".wixburn", StringComparer.Ordinal))
            return Create(LanInstallerKind.Burn, "burn", "WiX Burn");

        if (ContainsBytes(stream, overlayOffset, MaximumOverlayScanBytes, InnoDataSignature)
            || ContainsAsciiText(stream, "Inno Setup"))
            return Create(LanInstallerKind.InnoSetup, "inno", "Inno Setup");

        if (ContainsBytes(stream, overlayOffset, MaximumOverlayScanBytes, NsisSignature))
            return Create(LanInstallerKind.Nsis, "nullsoft", "NSIS");

        if (ContainsAsciiText(stream, "InstallShield"))
            return Create(LanInstallerKind.InstallShield, "exe", "InstallShield");

        return Create(LanInstallerKind.Unknown, "exe", "Desconhecido");
    }

    private static LanInstallerDetection Create(LanInstallerKind kind, string moduleType, string displayName) =>
        new(kind, moduleType, displayName);

    private static bool TryReadPeSections(Stream stream, out IReadOnlyList<string> sections, out long imageEnd)
    {
        sections = [];
        imageEnd = 0;
        if (stream.Length < 64)
            return false;

        Span<byte> dosHeader = stackalloc byte[64];
        stream.Position = 0;
        if (ReadAtMost(stream, dosHeader) != dosHeader.Length || dosHeader[0] != (byte)'M' || dosHeader[1] != (byte)'Z')
            return false;

        int peOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader[0x3c..]);
        if (peOffset < dosHeader.Length || peOffset > stream.Length - 24)
            return false;

        Span<byte> coff = stackalloc byte[24];
        stream.Position = peOffset;
        if (ReadAtMost(stream, coff) != coff.Length
            || coff[0] != (byte)'P' || coff[1] != (byte)'E' || coff[2] != 0 || coff[3] != 0)
            return false;

        int sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(coff[6..]);
        int optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(coff[20..]);
        if (sectionCount is < 1 or > MaximumSections)
            return false;

        long sectionOffset = (long)peOffset + 24 + optionalHeaderSize;
        if (sectionOffset < 0 || sectionOffset > stream.Length - (long)sectionCount * 40)
            return false;

        var parsedSections = new List<string>(sectionCount);
        imageEnd = sectionOffset + (long)sectionCount * 40;
        byte[] sectionHeader = new byte[40];
        for (int index = 0; index < sectionCount; index++)
        {
            stream.Position = sectionOffset + (long)index * 40;
            if (ReadAtMost(stream, sectionHeader) != sectionHeader.Length)
                return false;
            int nameLength = Array.IndexOf(sectionHeader, (byte)0, 0, 8);
            if (nameLength < 0) nameLength = 8;
            parsedSections.Add(Encoding.ASCII.GetString(sectionHeader, 0, nameLength));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(sectionHeader.AsSpan(16, 4));
            uint rawOffset = BinaryPrimitives.ReadUInt32LittleEndian(sectionHeader.AsSpan(20, 4));
            imageEnd = Math.Max(imageEnd, (long)rawOffset + rawSize);
        }

        if (imageEnd > stream.Length)
            return false;
        sections = parsedSections;
        return true;
    }

    private static bool ContainsBytes(Stream stream, long start, int maximumBytes, ReadOnlySpan<byte> needle)
    {
        long end = Math.Min(stream.Length, start + maximumBytes);
        byte[] buffer = new byte[ScanBufferBytes + needle.Length - 1];
        int overlap = 0;
        stream.Position = start;
        while (stream.Position < end)
        {
            int amount = (int)Math.Min(ScanBufferBytes, end - stream.Position);
            int read = stream.Read(buffer, overlap, amount);
            if (read == 0) break;
            int available = overlap + read;
            if (buffer.AsSpan(0, available).IndexOf(needle) >= 0)
                return true;
            overlap = Math.Min(needle.Length - 1, available);
            buffer.AsSpan(available - overlap, overlap).CopyTo(buffer);
        }
        return false;
    }

    private static bool ContainsAsciiText(Stream stream, string text)
    {
        byte[] pattern = Encoding.ASCII.GetBytes(text);
        long originalPosition = stream.Position;
        try
        {
            stream.Position = 0;
            byte[] buffer = new byte[ScanBufferBytes + pattern.Length - 1];
            int overlap = 0;
            while (true)
            {
                int read = stream.Read(buffer, overlap, ScanBufferBytes);
                if (read == 0) return false;
                int available = overlap + read;
                for (int index = 0; index <= available - pattern.Length; index++)
                {
                    bool match = true;
                    for (int offset = 0; offset < pattern.Length; offset++)
                    {
                        byte value = buffer[index + offset];
                        if (value is >= (byte)'A' and <= (byte)'Z') value += 32;
                        byte expected = pattern[offset] is >= (byte)'A' and <= (byte)'Z'
                            ? (byte)(pattern[offset] + 32)
                            : pattern[offset];
                        if (value != expected)
                        {
                            match = false;
                            break;
                        }
                    }
                    if (match) return true;
                }
                overlap = Math.Min(pattern.Length - 1, available);
                buffer.AsSpan(available - overlap, overlap).CopyTo(buffer);
            }
        }
        finally
        {
            stream.Position = originalPosition;
        }
    }

    private static int ReadAtMost(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0) break;
            total += read;
        }
        return total;
    }
}
