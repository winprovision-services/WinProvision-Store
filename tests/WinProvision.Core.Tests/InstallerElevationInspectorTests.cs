using System.Buffers.Binary;
using System.Text;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstallerElevationInspectorTests
{
    [Theory]
    [InlineData("exe")]
    [InlineData("inno")]
    [InlineData("nullsoft")]
    [InlineData("burn")]
    public async Task ConfirmedPeTypesReadOnlyManifestBytes(string type)
    {
        byte[] bytes = PeFixture("requireAdministrator");
        using var stream = new TrackingStream(bytes);
        var inspector = new InstallerElevationInspector(_ => stream, TimeSpan.FromSeconds(2));
        var result = await inspector.InspectAsync("never-execute-" + type + ".exe", default);
        Assert.Equal("required", result.Decision);
        Assert.Contains(result.Signals, s => s.Value == "requireAdministrator");
        Assert.True(stream.ReadBytes > 0);
        Assert.True(stream.ReadBytes < bytes.Length); // no payload scan
        Assert.Equal(0, stream.WriteCalls);
        Assert.True(stream.Disposed);
    }

    [Theory]
    [InlineData("asInvoker", "not-required")]
    [InlineData("highestAvailable", "unknown")]
    [InlineData("requireAdministrator", "required")]
    [InlineData("unrecognized", "unknown")]
    public async Task ManifestLevelsHaveExactSemantics(string level, string decision)
        => Assert.Equal(decision, (await InspectBytes(PeFixture(level))).Decision);

    [Fact]
    public async Task Pe32PlusIsSupported()
        => Assert.Equal("required", (await InspectBytes(PeFixture("requireAdministrator", pe64: true))).Decision);

    [Fact]
    public async Task DocumentedV2TrustInfoWithV3PrivilegesIsSupported()
    {
        string xml = Manifest("requireAdministrator").Replace("<trustInfo xmlns='urn:schemas-microsoft-com:asm.v3'>",
            "<trustInfo xmlns='urn:schemas-microsoft-com:asm.v2'>", StringComparison.Ordinal)
            .Replace("<requestedPrivileges>", "<requestedPrivileges xmlns='urn:schemas-microsoft-com:asm.v3'>", StringComparison.Ordinal);
        Assert.Equal("required", (await InspectBytes(PeWithXml(xml))).Decision);
    }

    [Fact]
    public async Task RandomTruncationsAndCorruptHeadersCannotCrashInspection()
    {
        byte[] original = PeFixture("requireAdministrator");
        foreach (int length in new[] { 0, 1, 63, 64, 127, 151, 200, 511, 512, 800 })
            Assert.Equal("unknown", (await InspectBytes(original[..length])).Decision);
        foreach (int offset in new[] { 0x3c, 0x80, 0x86, 0x94, 0x98, 0x108, 0x18c, 0x20e, 0x214, 0x254, 0x260 })
        {
            byte[] corrupt = (byte[])original.Clone();
            Put32(corrupt, offset, uint.MaxValue);
            Assert.Equal("unknown", (await InspectBytes(corrupt)).Decision);
        }
    }

    [Fact]
    public async Task CyclicResourceTreeAndExcessiveEntriesAreInconclusive()
    {
        byte[] data = PeFixture("requireAdministrator");
        Put32(data, 0x214, 0x80000000);
        Assert.Equal("unknown", (await InspectBytes(data)).Decision);
        data = PeFixture("requireAdministrator"); Put16(data, 0x24e, 257);
        Assert.Equal("unknown", (await InspectBytes(data)).Decision);
    }

    [Fact]
    public async Task TotalReadBudgetBoundsRepeatedManifestReferences()
    {
        byte[] data = PeFixture("requireAdministrator");
        Put16(data, 0x24e, 256);
        for (int i = 0; i < 256; i++)
        {
            Put32(data, 0x250 + i * 8, (uint)i);
            Put32(data, 0x254 + i * 8, 0x900);
        }
        Put32(data, 0xb00, 0x1a00); Put32(data, 0xb04, 32 * 1024);
        Array.Resize(ref data, 64 * 1024);
        Array.Fill(data, (byte)' ', 0xc00, 32 * 1024);
        Encoding.UTF8.GetBytes(Manifest("requireAdministrator")).CopyTo(data, 0xc00);
        Put32(data, 0x98 + 96 + 20, 0xfe00);
        Put32(data, 0x178 + 8, 0xfe00); Put32(data, 0x178 + 16, 0xfe00);
        using var stream = new TrackingStream(data);
        var result = await new InstallerElevationInspector(_ => stream, TimeSpan.FromSeconds(2)).InspectAsync("unused", default);
        Assert.Equal("unknown", result.Decision);
        Assert.True(stream.ReadBytes <= InstallerElevationInspector.MaximumReadBytes);
        Assert.True(stream.ReadBytes > 900 * 1024); // reaches aggregate budget, not a malformed XML shortcut
    }

    [Fact]
    public async Task ReadFailuresAreInconclusive()
    {
        foreach (Exception exception in new Exception[] { new IOException("read"), new UnauthorizedAccessException("access") })
        {
            var result = await new InstallerElevationInspector(_ => throw exception, TimeSpan.FromSeconds(2)).InspectAsync("unused", default);
            Assert.Equal("unknown", result.Decision); Assert.False(result.Complete);
        }
    }

    [Fact]
    public void InspectorHasNoProcessOrModuleLoadingPath()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WinProvision.Store.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root.FullName, "src/WinProvision.Core/Services/InstallerElevationInspector.cs"));
        foreach (string forbidden in new[] { "Process.Start", "ProcessStartInfo", "LoadLibrary", "NativeLibrary", "Assembly.Load", "DllImport", "LibraryImport", "MsiOpenPackage", "ExtractToDirectory" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        Assert.Contains("FileAccess.Read", source, StringComparison.Ordinal);
        Assert.Contains("FileShare.ReadWrite | FileShare.Delete", source, StringComparison.Ordinal);
        Assert.Contains("ReadExactlyAsync", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("msi")]
    [InlineData("wix")]
    public async Task InvalidCompoundHeaderIsInconclusive(string type)
    {
        // Compound-file signature, not an invented MSI database format.
        byte[] data = new byte[512];
        Convert.FromHexString("D0CF11E0A1B11AE1").CopyTo(data, 0);
        Assert.Equal("unknown", (await InspectBytes(data, type)).Decision);
    }

    [Fact]
    public async Task EmptyNonPeAndTruncatedAreInconclusive()
    {
        foreach (byte[] data in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("not a PE"),
            PeFixture("asInvoker")[..100], new byte[128] })
            Assert.Equal("unknown", (await InspectBytes(data)).Decision);
    }

    [Fact]
    public async Task MalformedAndOverflowingResourceOffsetsAreInconclusive()
    {
        byte[] data = PeFixture("requireAdministrator");
        Put32(data, 0x200 + 20, uint.MaxValue);
        Assert.Equal("unknown", (await InspectBytes(data)).Decision);
        data = PeFixture("requireAdministrator");
        Put32(data, 0x3c, uint.MaxValue);
        Assert.Equal("unknown", (await InspectBytes(data)).Decision);
    }

    [Fact]
    public async Task ManifestLimitAndMissingResourceAreInconclusive()
    {
        byte[] data = PeFixture("requireAdministrator");
        Put32(data, 0x264, InstallerElevationInspector.MaximumManifestBytes + 1);
        Assert.Equal("unknown", (await InspectBytes(data)).Decision);
        data = PeFixture("requireAdministrator");
        Put32(data, 0x80 + 24 + 96 + 16, 0);
        Assert.Equal("no-resource-directory", (await InspectBytes(data)).Reason);
    }

    [Fact]
    public async Task XmlDtdAndWrongNamespaceCannotAuthorizeElevation()
    {
        string[] invalid =
        [
            "<!DOCTYPE assembly [<!ENTITY x SYSTEM 'file:///never-read'>]><assembly>&x;</assembly>",
            "<assembly manifestVersion='1.0'><trustInfo><security><requestedPrivileges><requestedExecutionLevel level='requireAdministrator'/></requestedPrivileges></security></trustInfo></assembly>",
            Manifest("requireAdministrator").Replace("uiAccess='false'", "uiAccess='true'"),
            Manifest("requireAdministrator").Replace("</requestedPrivileges>", "<requestedExecutionLevel level='asInvoker'/></requestedPrivileges>"),
            "<assembly"
        ];
        foreach (string xml in invalid) Assert.Equal("unknown", (await InspectBytes(PeWithXml(xml))).Decision);
    }

    [Fact]
    public async Task DifferentLanguageLevelsAreInconclusive()
    {
        byte[] data = PeFixture("requireAdministrator");
        Put16(data, 0x24e, 2);
        Put32(data, 0x258, 1046); Put32(data, 0x25c, 0x70);
        byte[] other = Encoding.UTF8.GetBytes(Manifest("asInvoker"));
        Put32(data, 0x270, 0x1800); Put32(data, 0x274, (uint)other.Length);
        other.CopyTo(data, 0xa00);
        Assert.Equal("unknown", (await InspectBytes(data)).Decision);
    }

    [Fact]
    public async Task AboveFileLimitReadsNoBytes()
    {
        using var stream = new TrackingStream([]) { ReportedLength = InstallerElevationInspector.MaximumFileBytes + 1 };
        var inspector = new InstallerElevationInspector(_ => stream, TimeSpan.FromSeconds(2));
        Assert.Equal("file-size-limit", (await inspector.InspectAsync("unused", default)).Reason);
        Assert.Equal(0, stream.ReadBytes);
    }

    [Fact]
    public async Task TimeoutReturnsUnknownAndLateReadIsObservedAndDisposed()
    {
        using var stream = new SlowStream(PeFixture("requireAdministrator"));
        var inspector = new InstallerElevationInspector(_ => stream, TimeSpan.FromMilliseconds(30));
        var result = await inspector.InspectAsync("unused", default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("time-limit", result.Reason);
        Assert.False(result.Complete);
        stream.Release.TrySetResult();
        await stream.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task CancellationBeforeAndDuringReadPropagates()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool opened = false;
        var inspector = new InstallerElevationInspector(_ => { opened = true; return new MemoryStream(); }, TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inspector.InspectAsync("unused", canceled.Token));
        Assert.False(opened);
        using var stream = new SlowStream(PeFixture("asInvoker"));
        using var during = new CancellationTokenSource();
        inspector = new InstallerElevationInspector(_ => stream, TimeSpan.FromSeconds(2));
        Task<InstallerElevationInspection> pending = inspector.InspectAsync("unused", during.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        during.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        stream.Release.TrySetResult();
        await stream.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task ActualFileIsSharedForWritesAndDeleteAndRemainsUnchanged()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe");
        byte[] bytes = PeFixture("requireAdministrator");
        await File.WriteAllBytesAsync(path, bytes);
        try
        {
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
                Assert.Equal("required", (await new InstallerElevationInspector().InspectAsync(path, default)).Decision);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }

    private static async Task<InstallerElevationInspection> InspectBytes(byte[] bytes, string type = "exe")
        => await new InstallerElevationInspector(_ => new MemoryStream(bytes, writable: false), TimeSpan.FromSeconds(2))
            .InspectAsync("fixture." + type, default);

    internal static byte[] PeFixture(string level, bool pe64 = false) => PeWithXml(Manifest(level), pe64);
    private static string Manifest(string level) => $"<assembly xmlns='urn:schemas-microsoft-com:asm.v1' manifestVersion='1.0'><trustInfo xmlns='urn:schemas-microsoft-com:asm.v3'><security><requestedPrivileges><requestedExecutionLevel level='{level}' uiAccess='false'/></requestedPrivileges></security></trustInfo></assembly>";
    private static byte[] PeWithXml(string xml, bool pe64 = false)
    {
        // Microsoft PE/COFF headers and three-level RT_MANIFEST/1/LANGID tree. No executable code.
        byte[] data = new byte[4096];
        Put16(data, 0, 0x5a4d); Put32(data, 0x3c, 0x80); Put32(data, 0x80, 0x4550);
        Put16(data, 0x84, pe64 ? 0x8664 : 0x14c); Put16(data, 0x86, 1);
        int optionalSize = pe64 ? 240 : 224, directories = pe64 ? 112 : 96;
        Put16(data, 0x94, optionalSize); Put16(data, 0x98, pe64 ? 0x20b : 0x10b);
        Put32(data, 0x98 + directories - 4, 16);
        Put32(data, 0x98 + directories + 16, 0x1000); Put32(data, 0x98 + directories + 20, 0xe00);
        int section = 0x98 + optionalSize;
        Put32(data, section + 8, 0xe00); Put32(data, section + 12, 0x1000);
        Put32(data, section + 16, 0xe00); Put32(data, section + 20, 0x200);
        Put16(data, 0x20e, 1); Put32(data, 0x210, 24); Put32(data, 0x214, 0x80000020);
        Put16(data, 0x22e, 1); Put32(data, 0x230, 1); Put32(data, 0x234, 0x80000040);
        Put16(data, 0x24e, 1); Put32(data, 0x250, 1033); Put32(data, 0x254, 0x60);
        byte[] encoded = Encoding.UTF8.GetBytes(xml);
        Put32(data, 0x260, 0x1100); Put32(data, 0x264, (uint)encoded.Length);
        encoded.CopyTo(data, 0x300);
        return data;
    }
    private static void Put16(byte[] data, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), (ushort)value);
    private static void Put32(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);

    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public long? ReportedLength { get; init; }
        public override long Length => ReportedLength ?? base.Length;
        public int ReadBytes { get; private set; }
        public int WriteCalls { get; private set; }
        public bool Disposed { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            int read = await base.ReadAsync(buffer, token); ReadBytes += read; return read;
        }
        public override void Write(byte[] buffer, int offset, int count) { WriteCalls++; throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class SlowStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            Entered.TrySetResult();
            await Release.Task; // deliberately ignores cancellation, like a stalled device
            return await base.ReadAsync(buffer, token);
        }
        protected override void Dispose(bool disposing) { base.Dispose(disposing); Closed.TrySetResult(); }
    }
}
