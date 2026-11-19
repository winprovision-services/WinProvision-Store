using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstallerInspectionStoreTests : IDisposable
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinProvision-inspection-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Pe1EvidenceIsIgnoredByTheCurrentInspector()
    {
        var store = new InstallerInspectionStore(_root);
        await store.WriteAsync(new(Hash, "pe-1", DateTimeOffset.UtcNow, new("not-required", "old-result", [])), default);
        int reads = 0;
        byte[] bytes = InstallerElevationInspectorTests.PeFixture("requireAdministrator");
        var inspector = new InstallerElevationInspector(_ => { reads++; return new MemoryStream(bytes, false); }, TimeSpan.FromSeconds(2));
        Assert.Equal("pe-msi-2", inspector.Version);
        var result = await new CachedInstallerElevationInspector(inspector, store).InspectAsync("unused", Hash, default);
        Assert.Equal("required", result.Decision); Assert.False(result.FromStorage); Assert.Equal(1, reads);
        Assert.NotNull(await store.ReadAsync(Hash, "pe-1", default));
        Assert.NotNull(await store.ReadAsync(Hash, inspector.Version, default));
    }

    [Fact]
    public async Task SameHashAcrossInstancesUsesInspectableDiskStorage()
    {
        var inspector = new CountingInspector();
        var first = await new CachedInstallerElevationInspector(inspector, new InstallerInspectionStore(_root)).InspectAsync("unused", Hash, default);
        var second = await new CachedInstallerElevationInspector(inspector, new InstallerInspectionStore(_root)).InspectAsync("different-path", Hash.ToUpperInvariant(), default);
        Assert.False(first.FromStorage); Assert.True(second.FromStorage); Assert.Equal(1, inspector.Calls);
        Assert.Equal(first.Signals, second.Signals);
        string json = await File.ReadAllTextAsync(Path.Combine(_root, inspector.Version, Hash + ".json"));
        Assert.Contains("inspectedAt", json); Assert.Contains("requestedExecutionLevel", json);
    }

    [Fact]
    public async Task VersionAndHashChangesInvalidatePreviousResult()
    {
        var inspector = new CountingInspector(); var store = new InstallerInspectionStore(_root);
        await new CachedInstallerElevationInspector(inspector, store).InspectAsync("unused", Hash, default);
        inspector.Version = "test-2";
        Assert.False((await new CachedInstallerElevationInspector(inspector, store).InspectAsync("unused", Hash, default)).FromStorage);
        Assert.False((await new CachedInstallerElevationInspector(inspector, store).InspectAsync("unused", new string('a', 64), default)).FromStorage);
        Assert.Equal(3, inspector.Calls);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("{}")]
    public async Task DamagedStorageFallsBackToInspection(string json)
    {
        string dir = Path.Combine(_root, "test-1"); Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, Hash + ".json"), json);
        var inspector = new CountingInspector();
        Assert.False((await new CachedInstallerElevationInspector(inspector, new InstallerInspectionStore(_root)).InspectAsync("unused", Hash, default)).FromStorage);
        Assert.Equal(1, inspector.Calls);
    }

    [Fact]
    public async Task IoFailureCannotLoseTheInspectionResult()
    {
        Directory.CreateDirectory(_root);
        string file = Path.Combine(_root, "not-a-directory"); await File.WriteAllTextAsync(file, "occupied");
        foreach (IInstallerInspectionStore store in new IInstallerInspectionStore[] { new InstallerInspectionStore(file), new ThrowingStore() })
        {
            var inspector = new CountingInspector();
            var result = await new CachedInstallerElevationInspector(inspector, store).InspectAsync("unused", Hash, default);
            Assert.Equal("required", result.Decision); Assert.False(result.FromStorage);
        }
    }

    [Fact]
    public async Task TimeoutAndOtherIncompleteResultsAreNotStored()
    {
        var inspector = new CountingInspector { Result = InstallerElevationInspection.Unknown("time-limit", false) };
        var cached = new CachedInstallerElevationInspector(inspector, new InstallerInspectionStore(_root));
        await cached.InspectAsync("unused", Hash, default); await cached.InspectAsync("unused", Hash, default);
        Assert.Equal(2, inspector.Calls); Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task OversizedEntryAndMismatchedEnvelopeAreIgnored()
    {
        var store = new InstallerInspectionStore(_root); var inspector = new CountingInspector();
        await new CachedInstallerElevationInspector(inspector, store).InspectAsync("unused", Hash, default);
        string path = Path.Combine(_root, inspector.Version, Hash + ".json");
        string json = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(path, json.Replace(Hash, new string('b', 64), StringComparison.Ordinal));
        Assert.Null(await store.ReadAsync(Hash, inspector.Version, default));
        await File.WriteAllTextAsync(path, new string(' ', InstallerInspectionStore.MaximumEntryBytes + 1));
        Assert.Null(await store.ReadAsync(Hash, inspector.Version, default));
    }

    [Fact]
    public async Task StorageDeadlineDoesNotHoldUpInspectionAndLateWorkIsObserved()
    {
        var store = new SlowStore(); var inspector = new CountingInspector();
        var result = await new CachedInstallerElevationInspector(inspector, store).InspectAsync("unused", Hash, default)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("required", result.Decision);
        store.Completion.SetException(new IOException("late failure"));
    }

    [Fact]
    public async Task CancellationIsNotConvertedIntoAnInstallationDecision()
    {
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var inspector = new CountingInspector();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CachedInstallerElevationInspector(inspector,
            new InstallerInspectionStore(_root)).InspectAsync("unused", Hash, canceled.Token));
        Assert.Equal(0, inspector.Calls);
    }

    [Fact]
    public async Task InvalidHashOrVersionCannotEscapeStorageDirectory()
    {
        var store = new InstallerInspectionStore(_root);
        Assert.Null(await store.ReadAsync("../escape", "test-1", default));
        Assert.Null(await store.ReadAsync(Hash, "../escape", default));
        await store.WriteAsync(new(Hash, "../escape", DateTimeOffset.UtcNow, new CountingInspector().Result), default);
        Assert.False(Directory.Exists(_root));
    }

    private sealed class CountingInspector : IInstallerElevationInspector
    {
        public string Version { get; set; } = "test-1";
        public int Calls { get; private set; }
        public InstallerElevationInspection Result { get; init; } = new("required", "pe-requested-execution-level", [new("requestedExecutionLevel", "requireAdministrator")]);
        public Task<InstallerElevationInspection> InspectAsync(string path, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Result); }
    }
    private sealed class ThrowingStore : IInstallerInspectionStore
    {
        public Task<StoredInstallerInspection?> ReadAsync(string sha256, string inspectorVersion, CancellationToken token) => throw new IOException("read");
        public Task WriteAsync(StoredInstallerInspection entry, CancellationToken token) => throw new IOException("write");
    }
    private sealed class SlowStore : IInstallerInspectionStore
    {
        public TaskCompletionSource<StoredInstallerInspection?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StoredInstallerInspection?> ReadAsync(string sha256, string inspectorVersion, CancellationToken token) => Completion.Task;
        public Task WriteAsync(StoredInstallerInspection entry, CancellationToken token) => Task.CompletedTask;
    }
    public void Dispose()
    {
        // Fixed, uniquely-created test root; never computed from untrusted cache keys.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
