using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstallerInspectionIntegrationTests : IDisposable
{
    private const string Hash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinProvision-inspection-integration-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("inno", "asInvoker", "unknown", "none", "direct")]
    [InlineData("burn", "asInvoker", "unknown", "none", "direct")]
    [InlineData("nullsoft", "asInvoker", "not-required", "inspected", "direct")]
    [InlineData("exe", "asInvoker", "not-required", "inspected", "direct")]
    [InlineData("inno", "requireAdministrator", "required", "inspected", "elevated")]
    [InlineData("burn", "requireAdministrator", "required", "inspected", "elevated")]
    [InlineData("nullsoft", "requireAdministrator", "required", "inspected", "elevated")]
    public async Task RealPeManifestHasTypeSpecificSemantics(string type, string level, string decision, string source, string launch)
    {
        byte[] bytes = InstallerElevationInspectorTests.PeFixture(level);
        var inspector = new InstallerElevationInspector(_ => new MemoryStream(bytes, false), TimeSpan.FromSeconds(2));
        var starter = new Starter(); var log = new RecordingLog(); CoreLog.Configure(log);
        try
        {
            await using var service = Service(inspector, new MemoryStore(), starter);
            await service.RunDownloadedInstallerAsync("demo.id", "2.0", new CatalogInstaller { Type = type }, false,
                "fixture.exe", type, "", Hash, default);
            var data = Assert.Single(log.Events).Data;
            Assert.Equal(decision, data.GetProperty("decision").GetString());
            Assert.Equal(source, data.GetProperty("source").GetString());
            Assert.Equal(launch, Assert.Single(starter.Calls));
            Assert.Contains(data.GetProperty("signals").EnumerateArray(), s => s.GetProperty("value").GetString() == level);
        }
        finally { CoreLog.Configure(new RecordingLog()); }
    }

    [Fact]
    public async Task CachedAsInvokerIsReinterpretedForEachTypeWithTheSameHash()
    {
        byte[] bytes = InstallerElevationInspectorTests.PeFixture("asInvoker");
        int reads = 0;
        var inspector = new InstallerElevationInspector(_ => { reads++; return new MemoryStream(bytes, false); }, TimeSpan.FromSeconds(2));
        var cached = new CachedInstallerElevationInspector(inspector, new MemoryStore());
        foreach (string type in new[] { "exe", "inno", "burn", "nullsoft" })
        {
            var result = await cached.InspectAsync("unused", Hash, default, type);
            Assert.Equal(type is "inno" or "burn" ? "unknown" : "not-required", result.Decision);
            Assert.Equal(type != "exe", result.FromStorage);
        }
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData("inno")]
    [InlineData("burn")]
    public async Task AsInvokerBootstrapperKeepsPreStart740Retry(string type)
    {
        byte[] bytes = InstallerElevationInspectorTests.PeFixture("asInvoker");
        var inspector = new InstallerElevationInspector(_ => new MemoryStream(bytes, false), TimeSpan.FromSeconds(2));
        var starter = new Starter { FailDirect = true };
        await using var service = Service(inspector, new MemoryStore(), starter);
        await service.RunDownloadedInstallerAsync("demo.id", "2.0", new CatalogInstaller { Type = type }, false,
            "fixture.exe", type, "", Hash, default);
        Assert.Equal(new[] { "direct", "elevated" }, starter.Calls);
    }

    [Fact]
    public async Task MixedRealMsiBatchKeepsPerHashDecisionsAndCachesEvidence()
    {
        Directory.CreateDirectory(_root);
        string machine = Path.Combine(_root, "machine.msi"), user = Path.Combine(_root, "user.msi"), uncertain = Path.Combine(_root, "unknown.msi");
        MsiInspectionFixture.Create(machine, "1", null, 0);
        MsiInspectionFixture.Create(user, "2", "1", 8);
        MsiInspectionFixture.Create(uncertain, null, null, 0);
        var starter = new Starter(); var log = new RecordingLog(); CoreLog.Configure(log);
        try
        {
            await using var service = Service(new InstallerElevationInspector(), new MemoryStore(), starter);
            foreach (string path in new[] { machine, user, uncertain, machine })
            {
                string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))).ToLowerInvariant();
                await service.RunDownloadedInstallerAsync("demo.id", "2.0", new CatalogInstaller { Type = "msi" }, false,
                    path, "msi", "/quiet", hash, default);
            }
            Assert.Equal(new[] { "elevated", "direct", "direct", "elevated" }, starter.Calls);
            Assert.Equal(new[] { "required", "not-required", "unknown", "required" }, log.Events.Select(e => e.Data.GetProperty("decision").GetString()));
            Assert.Equal(new[] { "inspected", "inspected", "none", "inspected" }, log.Events.Select(e => e.Data.GetProperty("source").GetString()));
            Assert.Equal(new[] { false, false, false, true }, log.Events.Select(e => e.Data.GetProperty("fromStorage").GetBoolean()));
        }
        finally { CoreLog.Configure(new RecordingLog()); }
    }

    [Theory]
    [InlineData("elevationRequired", null, "elevated")]
    [InlineData(null, "user", "direct")]
    [InlineData(null, "machine", "elevated")]
    public async Task KnownMsiDecisionNeverStartsTheNativeReader(string? requirement, string? scope, string launch)
    {
        bool called = false;
        var inspector = new InstallerElevationInspector(_ => { called = true; throw new IOException("must not open"); }, TimeSpan.FromSeconds(2));
        var starter = new Starter();
        await using var service = Service(inspector, new MemoryStore(), starter);
        await service.RunDownloadedInstallerAsync("demo.id", "2.0", new CatalogInstaller { Type = "msi", Scope = scope, ElevationRequirement = requirement },
            false, "never-open.msi", "msi", "", Hash, default);
        Assert.False(called); Assert.Equal(launch, Assert.Single(starter.Calls));
    }

    [Theory]
    [InlineData("elevationRequired", null, "required", "declared")]
    [InlineData("elevatesSelf", "user", "required", "declared")]
    [InlineData(null, "user", "not-required", "scope")]
    [InlineData(null, "machine", "required", "scope")]
    public async Task DeclarationAndScopeSkipInspectionAndStorage(string? requirement, string? scope, string decision, string source)
    {
        var inspector = new FakeInspector("required"); var store = new MemoryStore(); var starter = new Starter();
        await using var service = Service(inspector, store, starter);
        var installer = new CatalogInstaller { Type = "exe", Scope = scope, ElevationRequirement = requirement };
        await Run(service, installer);
        Assert.Equal(0, inspector.Calls); Assert.Equal(0, store.Reads); Assert.Equal(0, store.Writes);
        var state = InstallerElevationResolver.Resolve(installer);
        Assert.Equal(decision, state.Decision); Assert.Equal(source, state.Source);
        Assert.Equal(decision == "required" ? "elevated" : "direct", Assert.Single(starter.Calls));
        Assert.Equal(state, InstallerElevationResolver.ApplyInspection(state, new("required", "test", [])));
    }

    [Fact]
    public async Task ResolvedV3ScopeIsNeverOverwritten()
    {
        var inspector = new FakeInspector("required"); var starter = new Starter();
        await using var service = Service(inspector, new MemoryStore(), starter);
        await Run(service, new CatalogInstaller { Type = "exe", Elevation = new()
            { Decision = "not-required", Source = "scope", Requirement = null, Prohibited = false } }, useResolvedState: true);
        Assert.Equal(0, inspector.Calls); Assert.Equal("direct", Assert.Single(starter.Calls));
    }

    [Theory]
    [InlineData("required", "elevated", "inspected")]
    [InlineData("not-required", "direct", "inspected")]
    [InlineData("unknown", "direct", "none")]
    public async Task UnknownUsesInspectionAndLogsStructuredEvidence(string decision, string launch, string source)
    {
        var inspector = new FakeInspector(decision); var starter = new Starter(); var log = new RecordingLog();
        CoreLog.Configure(log);
        try
        {
            await using var service = Service(inspector, new MemoryStore(), starter);
            await Run(service, new CatalogInstaller { Id = "installer-x64", Type = "exe" });
            Assert.Equal(1, inspector.Calls); Assert.Equal(launch, Assert.Single(starter.Calls));
            var data = Assert.Single(log.Events).Data;
            Assert.Equal("demo.id", data.GetProperty("id").GetString());
            Assert.Equal("2.0", data.GetProperty("version").GetString());
            Assert.Equal("installer-x64", data.GetProperty("installerId").GetString());
            Assert.Equal(source, data.GetProperty("source").GetString());
            Assert.Equal(decision, data.GetProperty("decision").GetString());
            Assert.Single(data.GetProperty("signals").EnumerateArray());
            Assert.False(data.GetProperty("fromStorage").GetBoolean());
            if (decision == "required") Assert.Equal(Hash, starter.ExpectedHash);
        }
        finally { CoreLog.Configure(new RecordingLog()); }
    }

    [Theory]
    [InlineData("unknown")]
    public async Task InconclusiveAndAsInvokerKeepSinglePreStart740Retry(string decision)
    {
        var starter = new Starter { FailDirect = true };
        await using var service = Service(new FakeInspector(decision), new MemoryStore(), starter);
        await Run(service, new CatalogInstaller { Type = "exe" });
        Assert.Equal(new[] { "direct", "elevated" }, starter.Calls);
    }

    [Fact]
    public async Task ConclusiveNotRequiredDoesNotRetry740()
    {
        var starter = new Starter { FailDirect = true };
        await using var service = Service(new FakeInspector("not-required"), new MemoryStore(), starter);
        await Assert.ThrowsAsync<Win32Exception>(() => Run(service, new CatalogInstaller { Type = "exe" }));
        Assert.Equal(new[] { "direct" }, starter.Calls);
    }

    [Theory]
    [InlineData("portable")]
    [InlineData("pwa")]
    [InlineData("msix")]
    [InlineData("appx")]
    public async Task TypeNotRequiredSkipsInspectionAndDoesNotRetry740(string type)
    {
        var starter = new Starter { FailDirect = true };
        var inspector = new FakeInspector("required");
        await using var service = Service(inspector, new MemoryStore(), starter);
        await Assert.ThrowsAsync<Win32Exception>(() => Run(service, new CatalogInstaller { Type = type }));
        Assert.Equal(0, inspector.Calls);
        Assert.Equal(new[] { "direct" }, starter.Calls);
    }

    [Fact]
    public async Task TimeLimitIsInconclusiveAndDoesNotFailInstallation()
    {
        var starter = new Starter();
        await using var service = Service(new IncompleteInspector(), new MemoryStore(), starter);
        Assert.Equal(0, await Run(service, new CatalogInstaller { Type = "exe" }));
        Assert.Equal("direct", Assert.Single(starter.Calls));
    }

    [Fact]
    public async Task CancellationDuringInspectionCannotStartInstaller()
    {
        using var canceled = new CancellationTokenSource();
        var starter = new Starter(); var inspector = new CancelingInspector(canceled);
        await using var service = Service(inspector, new MemoryStore(), starter);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunDownloadedInstallerAsync(
            "demo.id", "2.0", new CatalogInstaller { Type = "exe" }, false, "fixture.exe", "exe", "", Hash, canceled.Token));
        Assert.Empty(starter.Calls);
    }

    [Fact]
    public async Task InspectedProhibitedElevationLogsBothTheConflictAndEvidence()
    {
        var log = new RecordingLog(); CoreLog.Configure(log);
        try
        {
            var starter = new Starter();
            await using var service = Service(new FakeInspector("required"), new MemoryStore(), starter);
            await Run(service, new CatalogInstaller { Id = "prohibited", Type = "exe", ElevationRequirement = "elevationProhibited" });
            Assert.Equal(2, log.Events.Count);
            var entry = log.Events[0];
            Assert.Contains("elevationProhibited", log.Events[1].Message, StringComparison.Ordinal);
            Assert.True(entry.Data.GetProperty("prohibited").GetBoolean());
            Assert.Equal("not-required", entry.Data.GetProperty("decision").GetString());
            Assert.Equal("declared", entry.Data.GetProperty("source").GetString());
            Assert.Single(entry.Data.GetProperty("signals").EnumerateArray());
            Assert.Equal("direct", Assert.Single(starter.Calls));
        }
        finally { CoreLog.Configure(new RecordingLog()); }
    }

    [Fact]
    public async Task MixedBatchAndCacheKeepDecisionsPerHash()
    {
        var inspector = new FakeInspector("required"); var starter = new Starter(); var store = new MemoryStore();
        var log = new RecordingLog(); CoreLog.Configure(log);
        try
        {
            await using var service = Service(inspector, store, starter);
            var installer = new CatalogInstaller { Type = "exe" };
            await Run(service, installer);
            inspector.Decision = "unknown";
            await Run(service, installer, hash: new string('b', 64));
            await Run(service, installer);
            Assert.Equal(new[] { "elevated", "direct", "elevated" }, starter.Calls);
            Assert.Equal(2, inspector.Calls);
            Assert.Equal(new[] { false, false, true }, log.Events.Select(e => e.Data.GetProperty("fromStorage").GetBoolean()));
        }
        finally { CoreLog.Configure(new RecordingLog()); }
    }

    [Fact]
    public async Task MissingIndividualHashDoesNotCacheZipEvidenceForItsNestedInstaller()
    {
        var inspector = new FakeInspector("required"); var store = new MemoryStore(); var starter = new Starter();
        await using var service = Service(inspector, store, starter);
        await Run(service, new CatalogInstaller { Type = "zip" }, hash: null);
        Assert.Equal(0, inspector.Calls); Assert.Equal(0, store.Reads);
        Assert.Equal("direct", Assert.Single(starter.Calls));
    }

    [Fact]
    public async Task FullOwnApiPathVerifiesCachedFileThenInspectsThenDispatches()
    {
        byte[] bytes = InstallerElevationInspectorTests.PeFixture("requireAdministrator");
        Guid.NewGuid().ToByteArray().CopyTo(bytes, bytes.Length - 16); // unique hash; no collision with a user's cache
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvision", "InstallerCache");
        Directory.CreateDirectory(directory);
        string cached = Path.Combine(directory, hash.ToLowerInvariant() + ".exe");
        await File.WriteAllBytesAsync(cached, bytes);
        var starter = new Starter();
        var inspector = new VerifyingInspector(hash, starter);
        try
        {
            await using var service = new WinProvisionApiService(cacheDir: _root, installRecordsPath: Path.Combine(_root, "installed.json"),
                installerProcessStarter: starter, installerElevationInspector: inspector, installerInspectionStore: new MemoryStore());
            var manifest = new PackageManifest { Id = "test.inspection", Version = "1.0", Schema = 2, Installers =
                [new CatalogInstaller { Id = "exe", Type = "exe", Architecture = "neutral", Sha256 = hash,
                    Url = "https://example.invalid/never-download.exe" }] };
            var result = await service.TryInstallAsync(manifest.Id, suppliedManifest: manifest, recordInstall: false);
            Assert.Equal(WinProvisionInstallOutcome.Success, result.Outcome);
            Assert.True(result.InstallerStarted); Assert.Equal(1, inspector.Calls);
            Assert.Equal("elevated", Assert.Single(starter.Calls));
            Assert.Equal(hash, starter.ExpectedHash);
        }
        finally { File.Delete(cached); }
    }

    private WinProvisionApiService Service(IInstallerElevationInspector inspector, IInstallerInspectionStore store, Starter starter)
        => new(cacheDir: _root, installRecordsPath: Path.Combine(_root, "installed.json"), installerProcessStarter: starter,
            installerElevationInspector: inspector, installerInspectionStore: store);
    private static Task<int> Run(WinProvisionApiService service, CatalogInstaller installer, bool useResolvedState = false, string? hash = Hash)
        => service.RunDownloadedInstallerAsync("demo.id", "2.0", installer, useResolvedState, "fixture.exe", "exe", "", hash, default);

    private sealed class FakeInspector(string decision) : IInstallerElevationInspector
    {
        public string Version => "fake-1";
        public string Decision { get; set; } = decision;
        public int Calls { get; private set; }
        public Task<InstallerElevationInspection> InspectAsync(string path, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new InstallerElevationInspection(Decision, "inspection-test", [new("test-level", Decision)])); }
    }
    private sealed class IncompleteInspector : IInstallerElevationInspector
    {
        public string Version => "incomplete-test-1";
        public Task<InstallerElevationInspection> InspectAsync(string path, CancellationToken cancellationToken)
            => Task.FromResult(InstallerElevationInspection.Unknown("time-limit", false));
    }
    private sealed class CancelingInspector(CancellationTokenSource source) : IInstallerElevationInspector
    {
        public string Version => "cancel-test-1";
        public Task<InstallerElevationInspection> InspectAsync(string path, CancellationToken cancellationToken)
        { source.Cancel(); return Task.FromCanceled<InstallerElevationInspection>(cancellationToken); }
    }
    private sealed class VerifyingInspector(string hash, Starter starter) : IInstallerElevationInspector
    {
        public string Version => "verification-test-1";
        public int Calls { get; private set; }
        public async Task<InstallerElevationInspection> InspectAsync(string path, CancellationToken cancellationToken)
        {
            Assert.Empty(starter.Calls);
            await using var stream = File.OpenRead(path);
            Assert.Equal(hash, Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)));
            Calls++;
            return await new InstallerElevationInspector().InspectAsync(path, cancellationToken);
        }
    }
    private sealed class MemoryStore : IInstallerInspectionStore
    {
        private readonly Dictionary<(string, string), StoredInstallerInspection> _items = [];
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public Task<StoredInstallerInspection?> ReadAsync(string sha256, string inspectorVersion, CancellationToken token)
        { Reads++; return Task.FromResult(_items.GetValueOrDefault((sha256, inspectorVersion))); }
        public Task WriteAsync(StoredInstallerInspection entry, CancellationToken token)
        { Writes++; _items[(entry.Sha256, entry.InspectorVersion)] = entry; return Task.CompletedTask; }
    }
    private sealed class Starter : IInstallerProcessStarter
    {
        public List<string> Calls { get; } = [];
        public string? ExpectedHash { get; private set; }
        public bool FailDirect { get; init; }
        public Task<int> StartDirectAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token, Action? onStarted = null)
        {
            Calls.Add("direct");
            if (FailDirect) throw new Win32Exception(740);
            onStarted?.Invoke(); return Task.FromResult(0);
        }
        public Task<int> StartElevatedAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token,
            Action? onStarted = null, Action<string>? onLog = null, string? expectedSha256 = null)
        { Calls.Add("elevated"); ExpectedHash = expectedSha256; onStarted?.Invoke(); return Task.FromResult(0); }
    }
    private sealed class RecordingLog : IAppLog
    {
        public List<(string Message, JsonElement Data)> Events { get; } = [];
        public string SessionId => "test";
        public string LogDirectory => "";
        public AppLogLevel MinimumLevel { get; set; }
        public string CurrentLogFilePath => "";
        public long DroppedEventCount => 0;
        public void Write(AppLogLevel level, string category, string source, string message, Guid? taskId = null,
            string eventName = "Message", JsonElement? data = null, Exception? exception = null, IReadOnlyList<string>? knownSecrets = null)
        { if (source == "InstallerElevation" && data is { } value) Events.Add((message, value.Clone())); }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
