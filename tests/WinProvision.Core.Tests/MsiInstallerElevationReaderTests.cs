using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class MsiInstallerElevationReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinProvision-msi-inspection-" + Guid.NewGuid().ToString("N"));
    private string Create(string? allUsers, string? perUser, int? wordCount, string extension = ".msi",
        bool propertyTable = true, bool summary = true)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, Guid.NewGuid().ToString("N") + extension);
        MsiInspectionFixture.Create(path, allUsers, perUser, wordCount, propertyTable, summary);
        return path;
    }

    [Theory]
    [InlineData("1", null, 0, "required")]
    [InlineData(null, null, 8, "not-required")]
    public async Task RealReadOnlyMsiProducesDocumentedEvidence(string? allUsers, string? perUser, int count, string expected)
    {
        string path = Create(allUsers, perUser, count);
        byte[] before = await File.ReadAllBytesAsync(path);
        var result = await new InstallerElevationInspector().InspectAsync(path, default);
        Assert.Equal(expected, result.Decision);
        Assert.Equal("msi-context-and-word-count", result.Reason);
        Assert.Contains(result.Signals, s => s.Name == "msi.WordCount" && s.Value == count.ToString());
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
        // Successful exclusive open and rename immediately after reading catch leaked native handles.
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        File.Move(path, path + ".closed"); File.Delete(path + ".closed");
    }

    [Fact]
    public async Task ContentSelectsMsiParserEvenWhenTheExtensionIsExe()
    {
        string path = Create("1", null, 0, ".exe");
        Assert.Equal("required", (await new InstallerElevationInspector().InspectAsync(path, default)).Decision);
    }

    [Theory]
    [InlineData("1", null, 8, "required")]
    [InlineData("1", "1", 0, "required")]
    [InlineData("1", "bogus", 8, "required")]
    [InlineData("2", null, 0, "unknown")]
    [InlineData("2", "", 8, "unknown")]
    [InlineData("2", "1", 8, "not-required")]
    [InlineData("2", "1", 10, "not-required")]
    [InlineData("2", "1", 0, "unknown")]
    [InlineData(null, null, 0, "unknown")]
    [InlineData("", "1", 8, "not-required")]
    [InlineData(null, "bogus", 8, "not-required")]
    [InlineData(null, "1", 0, "unknown")]
    [InlineData("2", "0", 8, "unknown")]
    [InlineData("2", "2", 8, "unknown")]
    [InlineData("0", null, 8, "unknown")]
    [InlineData("3", null, 8, "unknown")]
    [InlineData(" 1 ", null, 8, "unknown")]
    public async Task DecisionTableUsesRealAuthoredDefaults(string? allUsers, string? perUser, int count, string expected)
    {
        string path = Create(allUsers, perUser, count);
        var result = await new InstallerElevationInspector().InspectAsync(path, default);
        Assert.True(result.Complete);
        Assert.Equal(expected, result.Decision);
        Assert.Contains(result.Signals, s => s.Name == "msi.ALLUSERS" && s.Value == (allUsers is null or "" ? "<absent>" : allUsers));
        Assert.Contains(result.Signals, s => s.Name == "msi.MSIINSTALLPERUSER" && s.Value == (perUser is null or "" ? "<absent>" : perUser));
        var resolved = InstallerElevationResolver.ApplyInspection(InstallerElevationResolver.Resolve("msi", null, null), result);
        Assert.Equal(expected == "unknown" ? "none" : "inspected", resolved.Source);
    }

    [Fact]
    public async Task MissingPropertyTableAndEmptyDatabaseAreInconclusiveAndCloseHandles()
    {
        foreach (bool summary in new[] { true, false })
        {
            string path = Create(null, null, 8, propertyTable: false, summary: summary);
            var result = await new InstallerElevationInspector().InspectAsync(path, default);
            Assert.Equal("unknown", result.Decision); Assert.False(result.Complete);
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            File.Move(path, path + ".closed"); File.Delete(path + ".closed");
        }
    }

    [Theory]
    [InlineData("1", "required")]
    [InlineData(null, "unknown")]
    public async Task MissingWordCountDefaultsToZeroOnlyWhenSummaryIsReadable(string? allUsers, string expected)
    {
        var result = await new InstallerElevationInspector().InspectAsync(Create(allUsers, null, null), default);
        Assert.Equal(expected, result.Decision);
        Assert.Contains(result.Signals, s => s.Name == "msi.WordCount" && s.Value == "0");
    }

    [Fact]
    public async Task CorruptTruncatedEmptyAndNonMsiAreInconclusive()
    {
        string valid = Create("1", null, 0);
        byte[] bytes = await File.ReadAllBytesAsync(valid);
        var cases = new[] { bytes[..64], bytes[..512], new byte[512], Array.Empty<byte>(), "not an installer"u8.ToArray() };
        foreach (byte[] data in cases)
        {
            string path = Path.Combine(_root, Guid.NewGuid() + ".msi");
            await File.WriteAllBytesAsync(path, data);
            var result = await new InstallerElevationInspector().InspectAsync(path, default);
            Assert.Equal("unknown", result.Decision);
            if (data.AsSpan().StartsWith(bytes.AsSpan(0, 8)))
                Assert.Contains(result.Signals, s => s.Name == "msi.apiError");
            File.Delete(path);
        }
    }

    [Fact]
    public async Task PeWithMsiExtensionStillUsesThePeParser()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "actually-pe.msi");
        await File.WriteAllBytesAsync(path, InstallerElevationInspectorTests.PeFixture("requireAdministrator"));
        var result = await new InstallerElevationInspector().InspectAsync(path, default);
        Assert.Equal("required", result.Decision);
        Assert.All(result.Signals, s => Assert.StartsWith("pe.", s.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task PropertyReadBoundReturnsInconclusiveOnMoreData()
    {
        var result = await new InstallerElevationInspector().InspectAsync(Create(new string('x', 129), null, 8), default);
        Assert.Equal("unknown", result.Decision); Assert.False(result.Complete);
        Assert.Contains(result.Signals, s => s.Name == "msi.apiError" && s.Value == "property-value:234");
    }

    [Fact]
    public async Task OversizedCompoundFileNeverCallsNativeReader()
    {
        bool called = false;
        var native = new MsiInstallerElevationReader((_, _) => { called = true; throw new InvalidOperationException(); }, TimeSpan.FromSeconds(2));
        var inspector = new InstallerElevationInspector(_ => new OversizedStream(), TimeSpan.FromSeconds(2), native);
        Assert.Equal("file-size-limit", (await inspector.InspectAsync("unused.msi", default)).Reason);
        Assert.False(called);
    }

    [Fact]
    public async Task NativeTimeoutIsInconclusiveAndRunsOnADedicatedThread()
    {
        using var release = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool pool = true, background = false;
        var native = new MsiInstallerElevationReader((_, _) =>
        {
            pool = Thread.CurrentThread.IsThreadPoolThread; background = Thread.CurrentThread.IsBackground;
            entered.TrySetResult();
            try { release.Wait(); return new("required", "late", []); }
            finally { finished.Set(); }
        }, TimeSpan.FromMilliseconds(100));
        try
        {
            byte[] compound = Convert.FromHexString("D0CF11E0A1B11AE1");
            var inspector = new InstallerElevationInspector(_ => new MemoryStream(compound, false), TimeSpan.FromSeconds(2), native);
            Task<InstallerElevationInspection> pending = inspector.InspectAsync("unused.msi", default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal("unknown", result.Decision); Assert.Equal("msi-time-limit", result.Reason);
            Assert.False(result.Complete); Assert.False(pool); Assert.True(background); Assert.False(finished.IsSet);
        }
        finally { release.Set(); Assert.True(finished.Wait(TimeSpan.FromSeconds(3))); }
    }

    [Fact]
    public async Task TwoAbandonedWorkersKeepSlotsUntilNativeCallsActuallyFinish()
    {
        using var release = new ManualResetEventSlim(); using var entered = new CountdownEvent(2); using var exited = new CountdownEvent(2);
        int active = 0, calls = 0;
        InstallerElevationInspection Read(string _, CancellationToken __)
        {
            Interlocked.Increment(ref calls); Interlocked.Increment(ref active); entered.Signal();
            try { release.Wait(); throw new IOException("late native failure"); }
            finally { Interlocked.Decrement(ref active); exited.Signal(); }
        }
        var first = new MsiInstallerElevationReader(Read, TimeSpan.FromSeconds(1));
        var second = new MsiInstallerElevationReader(Read, TimeSpan.FromSeconds(1));
        try
        {
            var p1 = first.InspectAsync("one", default); var p2 = second.InspectAsync("two", default);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var timedOut = await Task.WhenAll(p1, p2).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.All(timedOut, r => Assert.Equal("msi-time-limit", r.Reason));
            Assert.Equal(2, Volatile.Read(ref active));
            var third = new MsiInstallerElevationReader((_, _) => { Interlocked.Increment(ref calls); return new("required", "third", []); }, TimeSpan.FromMilliseconds(50));
            Assert.Equal("msi-time-limit", (await third.InspectAsync("three", default)).Reason);
            Assert.Equal(2, Volatile.Read(ref calls));
        }
        finally { release.Set(); Assert.True(exited.Wait(TimeSpan.FromSeconds(3))); }
        var recovered = new MsiInstallerElevationReader((_, _) => new("required", "recovered", []), TimeSpan.FromSeconds(2));
        Assert.Equal("required", (await recovered.InspectAsync("four", default)).Decision);
        Assert.Equal(0, Volatile.Read(ref active));
    }

    [Fact]
    public async Task CancellationDoesNotStartOrAbortNativeWorkers()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool called = false;
        var reader = new MsiInstallerElevationReader((_, _) => { called = true; return new("required", "unused", []); }, TimeSpan.FromSeconds(2));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.InspectAsync("unused", cancellation.Token));
        Assert.False(called);

        using var during = new CancellationTokenSource(); using var release = new ManualResetEventSlim(); using var exited = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader = new MsiInstallerElevationReader((_, _) =>
        {
            entered.TrySetResult();
            try { release.Wait(); return new("required", "late", []); }
            finally { exited.Set(); }
        }, TimeSpan.FromSeconds(2));
        try
        {
            Task<InstallerElevationInspection> pending = reader.InspectAsync("unused", during.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); during.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            Assert.False(exited.IsSet);
        }
        finally { release.Set(); Assert.True(exited.Wait(TimeSpan.FromSeconds(3))); }
    }

    [Fact]
    public void NativeReaderOnlyImportsDatabaseAndSummaryApis()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "WinProvision.Store.slnx"))) root = root.Parent;
        Assert.NotNull(root);
        string source = File.ReadAllText(Path.Combine(root.FullName, "src/WinProvision.Core/Services/MsiInstallerElevationReader.cs"));
        foreach (string forbidden in new[] { "MsiOpenPackage", "MsiInstallProduct", "MsiDoAction", "MsiSequence", "Process.Start", "LoadLibrary" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
        Assert.Contains("MsiOpenDatabase(path, IntPtr.Zero", source, StringComparison.Ordinal);
        Assert.Contains("finally { Native.MsiCloseHandle(database); }", source, StringComparison.Ordinal);
    }

    private sealed class OversizedStream() : MemoryStream(Convert.FromHexString("D0CF11E0A1B11AE1"), false)
    { public override long Length => InstallerElevationInspector.MaximumFileBytes + 1; }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
