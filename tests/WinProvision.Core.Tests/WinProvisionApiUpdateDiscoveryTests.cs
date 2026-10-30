using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinProvisionApiUpdateDiscoveryTests
{
    [Fact]
    public async Task ApiEngineListsPreparesAndVerifiesUsingOnlyRegistryAndR2()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-independent-api-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new CatalogHandler(R2Fixture.Create().Payloads));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"), Path.Combine(root, "records.json"));
        string installedVersion = "1.0.0";
        int provisionCalls = 0;
        var backend = new WinGetService(new WingetExecutor(), api, new WingetBootstrapper(),
            provision: (_, _) => { provisionCalls++; throw new InvalidOperationException("WinGet must not be used."); },
            preferredUpdateMethod: () => PackageUpdateMethod.WinProvisionApi,
            stuckUpgradeStatePath: Path.Combine(root, "stuck-upgrades.json"),
            apiInventory: token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(new List<UpgradablePackage> { new()
                {
                    Id = "registry-only-key", InstalledRegistryId = "registry-only-key",
                    Name = "Acme App", Source = "winget", CurrentVersion = installedVersion,
                    InstalledScope = "user", InstalledInstallerType = "exe", InstalledArchitecture = "x64"
                } });
            });
        await using var catalog = new StoreService();
        await using var engine = new ApplicationEngine(backend, new WingetExecutor(), api, new WingetBootstrapper(), catalog, _ => { });
        try
        {
            var offer = Assert.Single(await engine.UpdatesAsync(_ => { }, CancellationToken.None));
            Assert.Equal("Acme.App", offer.Id);
            Assert.Equal("registry-only-key", offer.InstalledRegistryId);
            Assert.Equal("2.0.0", (await backend.PrepareApiUpdateAsync(offer.Id)).Manifest.Version);
            // Invalid target must stop before downloading or executing, without provisioning gerenciador de pacotes.
            var rejected = await engine.ExecuteUpdateAsync(offer, new PackageInstallOptions(Version: "9.0.0"), _ => { }, CancellationToken.None);
            Assert.False(rejected.Success);
            Assert.False(rejected.InstallerStarted);
            var unverified = await backend.VerifySuccessfulUpgradeAsync(offer,
                new WingetExecutionResult { Success = true, ApiUpdateTargetVersion = "2.0.0" }, CancellationToken.None);
            Assert.False(unverified.Success);
            installedVersion = "2.0.0";
            Assert.True((await backend.VerifySuccessfulUpgradeAsync(offer,
                new WingetExecutionResult { Success = true, ApiUpdateTargetVersion = "2.0.0" }, CancellationToken.None)).Success);
            Assert.Equal(0, provisionCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RegistryArchitectureComesFromExecutableInsteadOfRegistryView()
    {
        string path = Environment.ProcessPath!;
        Assert.Equal(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            InstalledAppsService.ReadExecutableArchitecture($"\"{path}\",0"));
        Assert.Null(InstalledAppsService.ReadExecutableArchitecture(@"C:\missing\unknown.exe"));
        Assert.Null(InstalledAppsService.ReadExecutableArchitecture(null));
    }

    [Theory]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("1.2.0.0", "1.2.1", true)]
    [InlineData("1.10.0", "1.9.9", false)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("unknown", "2.0.0", false)]
    [InlineData("1.0.0-beta", "1.0.0", false)]
    [InlineData("1.0.0", "1.1.0-preview.1", false)]
    [InlineData("", "2.0.0", false)]
    public void OnlyStrictlyNewerNumericVersionsAreApiUpdateCandidates(
        string installed, string candidate, bool expected) =>
        Assert.Equal(expected,
            WinProvisionApiService.IsStrictlyNewerNumericVersion(installed, candidate));

    [Theory]
    [InlineData("Google Chrome", "google chrome")]
    [InlineData("Notepad++ (64-bit x64)", "notepad++")]
    [InlineData("Git version 2.44.0.windows.1", "git")]
    [InlineData("Mozilla Firefox (x64 pt-BR)", "mozilla firefox")]
    [InlineData("7-Zip 23.01 (x64)", "7-zip")]
    public void NormalizeForMatchingStripsArchitecturesAndVersions(string input, string expected) =>
        Assert.Equal(expected, WinProvisionApiService.NormalizeForMatching(input));

    [Fact]
    public void RegistryInventoryReturnsDesktopAppsWithoutWinget()
    {
        var inventory = InstalledAppsService.GetRegistryInventory();
        Assert.NotNull(inventory);
        // Em um sistema Windows real, o Registro contém aplicativos com Nome e Versão
        Assert.All(inventory, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
            Assert.Equal("winget", item.Source);
        });
    }

    [Fact]
    public async Task DiscoveryJoinsInstalledInventoryWithHashVerifiedR2WithoutOwnershipRecord()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-api-update-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(root, "cache");
        string records = Path.Combine(root, "ApiInstalledPackages.json");
        Directory.CreateDirectory(root);

        var fixture = R2Fixture.Create();
        var handler = new CatalogHandler(fixture.Payloads);
        using var http = new HttpClient(handler);
        await using var api = new WinProvisionApiService(http, cache, records);
        try
        {
            Assert.False(await api.HasManagedInstallsAsync());
            Assert.True(WinProvisionApiService.IsStrictlyNewerNumericVersion("1.0.0", "2.0.0"));
            Assert.Contains((await api.GetIndexAsync()).Packages, item => item.Id == "Acme.App" && item.Version == "2.0.0");
            var updates = await api.GetUpdatesAsync(
                (UpgradablePackage[])[new UpgradablePackage { Id = "Acme.App", Name = "Acme App", CurrentVersion = "1.0.0", Source = "winget" }]);

            var update = Assert.Single(updates);
            Assert.Equal("Acme.App", update.Id);
            Assert.Equal("1.0.0", update.CurrentVersion);
            Assert.Equal("2.0.0", update.AvailableVersion);
            Assert.Equal("winget", update.Source);
            Assert.True(await api.IsCurrentRemoteUpdateTargetAsync("Acme.App", "2.0.0"));
            Assert.False(await api.IsCurrentRemoteUpdateTargetAsync("Acme.App", "1.9.0"));
            var plan = await api.PrepareUpdateAsync(new UpgradablePackage
            {
                Id = "Acme.App", Name = "Acme App", Source = "winget", CurrentVersion = "1.0.0",
                InstalledScope = "machine", InstalledInstallerType = "exe", InstalledArchitecture = "x64"
            }, "2.0.0");
            Assert.Equal("2.0.0", plan.Manifest.Version);
            Assert.Equal("exe", plan.Installer.Type);
            Assert.Single(plan.Manifest.Installers);
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.PrepareUpdateAsync(new UpgradablePackage
            {
                Id = "Acme.App", Name = "Acme App", Source = "winget", CurrentVersion = "1.0.0",
                InstalledScope = "machine", InstalledInstallerType = "msi", InstalledArchitecture = "x64"
            }, "2.0.0"));
            Assert.Single(handler.Requests, path => path.StartsWith("search/", StringComparison.Ordinal));
            await Assert.ThrowsAsync<InvalidOperationException>(() => api.PrepareUpdateAsync(new UpgradablePackage
            {
                Id = "Acme.App", Name = "Acme App", Source = "winget", CurrentVersion = "3.0.0",
                InstalledScope = "machine", InstalledInstallerType = "exe", InstalledArchitecture = "x64"
            }));
            Assert.Empty(await api.GetUpdatesAsync((UpgradablePackage[])[new UpgradablePackage
            {
                Id = "Acme.App", Name = "Acme App", CurrentVersion = "1.0.0", Source = "msstore"
            }], refreshRemote: false));
            Assert.Empty(await api.GetUpdatesAsync((UpgradablePackage[])[new UpgradablePackage
            {
                Id = "Acme.App", Name = "Acme App", CurrentVersion = "2.0.0", Source = "winget"
            }], refreshRemote: false));
            Assert.Empty(await api.GetUpdatesAsync((UpgradablePackage[])[
                new UpgradablePackage { Id = "Acme.App", Name = "Acme App", CurrentVersion = "1.0.0", Source = "winget" },
                new UpgradablePackage { Id = "Acme.App", Name = "Acme App 2", CurrentVersion = "1.0.1", Source = "winget" }
            ], refreshRemote: false));

            // Matching por Nome quando Id do Registro difere do catálogo
            var nameUpdates = await api.GetUpdatesAsync((UpgradablePackage[])[
                new UpgradablePackage { Id = "Acme-Registry-Key-123", Name = "Acme App", CurrentVersion = "1.0.0", Source = "winget" }
            ], refreshRemote: false);
            var nameUpdate = Assert.Single(nameUpdates);
            Assert.Equal("Acme.App", nameUpdate.Id);
            Assert.Equal("2.0.0", nameUpdate.AvailableVersion);

            // Matching por Nome normalizado (remove sufixos de arquitetura)
            var normUpdates = await api.GetUpdatesAsync((UpgradablePackage[])[
                new UpgradablePackage { Id = "Acme-Registry-Key-456", Name = "Acme App (64-bit)", CurrentVersion = "1.0.0", Source = "winget" }
            ], refreshRemote: false);
            var normUpdate = Assert.Single(normUpdates);
            Assert.Equal("Acme.App", normUpdate.Id);
            Assert.Equal("2.0.0", normUpdate.AvailableVersion);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryNameCannotSelectAnAmbiguousCatalogPackage()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-api-ambiguous-" + Guid.NewGuid().ToString("N"));
        var fixture = R2Fixture.Create(duplicateName: true);
        using var http = new HttpClient(new CatalogHandler(fixture.Payloads));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"), Path.Combine(root, "records.json"));
        try
        {
            Assert.Empty(await api.GetUpdatesAsync((UpgradablePackage[])[new UpgradablePackage
            {
                Id = "registry-key", Name = "Acme App", CurrentVersion = "1.0.0", Source = "winget"
            }]));
            // An exact gerenciador de pacotes ID remains usable even when display names collide.
            Assert.Single(await api.GetUpdatesAsync((UpgradablePackage[])[new UpgradablePackage
            {
                Id = "Acme.App", Name = "Acme App", CurrentVersion = "1.0.0", Source = "winget"
            }], refreshRemote: false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ValidatedLocalCatalogCanServeDiscoveryOfflineButCannotAuthorizeExecution()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-api-update-stale-" + Guid.NewGuid().ToString("N"));
        string cache = Path.Combine(root, "cache");
        string records = Path.Combine(root, "ApiInstalledPackages.json");
        Directory.CreateDirectory(root);

        var installed = (UpgradablePackage[])[new UpgradablePackage
        {
            Id = "Acme.App", Name = "Acme App", CurrentVersion = "1.0.0", Source = "winget"
        }];
        var fixture = R2Fixture.Create();
        try
        {
            using (var onlineHttp = new HttpClient(new CatalogHandler(fixture.Payloads)))
            await using (var onlineApi = new WinProvisionApiService(onlineHttp, cache, records))
                Assert.Single(await onlineApi.GetUpdatesAsync(installed));

            using var offlineHttp = new HttpClient(new OfflineHandler());
            await using var offlineApi = new WinProvisionApiService(offlineHttp, cache, records);
            Assert.Single(await offlineApi.GetUpdatesAsync(installed));
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                offlineApi.IsCurrentRemoteUpdateTargetAsync("Acme.App", "2.0.0"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task R2DetailHashMismatchDoesNotPublishAnUpdateOffer()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-api-update-hash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string records = Path.Combine(root, "records.json");

        var fixture = R2Fixture.Create();
        var payloads = fixture.Payloads.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        byte[] tamperedDetail = (byte[])payloads["manifests/a/acme/app/versions/2.0.0/app.json"].Clone();
        tamperedDetail[^2] ^= 1;
        payloads["manifests/a/acme/app/versions/2.0.0/app.json"] = tamperedDetail;
        using var http = new HttpClient(new CatalogHandler(payloads));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"), records);
        try
        {
            var updates = await api.GetUpdatesAsync((UpgradablePackage[])[new UpgradablePackage
            {
                Id = "Acme.App", Name = "Acme App", CurrentVersion = "1.0.0", Source = "winget"
            }]);
            Assert.Empty(updates);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StalledR2CannotHoldUpdateDiscoveryOpenIndefinitely()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-api-update-hang-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string records = Path.Combine(root, "records.json");

        using var http = new HttpClient(new HangingHandler());
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"), records);
        try
        {
            var timer = Stopwatch.StartNew();
            await Assert.ThrowsAsync<HttpRequestException>(() => api.GetUpdatesAsync((UpgradablePackage[])[new UpgradablePackage
            {
                Id = "Acme.App", Name = "Acme App", CurrentVersion = "1.0.0", Source = "winget"
            }]));
            Assert.InRange(timer.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(8));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RemoteTargetValidationFailsClosedWhenR2CannotBeRevalidated()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-api-update-offline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var http = new HttpClient(new OfflineHandler());
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"), Path.Combine(root, "records.json"));
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                api.IsCurrentRemoteUpdateTargetAsync("Acme.App", "2.0.0"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Acme", true)]
    [InlineData("", false)]
    [InlineData("Unrelated Publisher", false)]
    public async Task SharedCatalogNameIsResolvedOnlyByUniquePublisher(string publisher, bool expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-api-publisher-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new CatalogHandler(R2Fixture.Create(duplicateName: true, distinctPublishers: true).Payloads));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"), Path.Combine(root, "records.json"));
        try
        {
            var updates = await api.GetUpdatesAsync((UpgradablePackage[])[new UpgradablePackage
            {
                Id = "registry-key", InstalledRegistryId = "registry-key", Name = "Acme App (64-bit)",
                Publisher = publisher, CurrentVersion = "1.0.0", Source = "winget"
            }]);
            if (expected)
            {
                var update = Assert.Single(updates);
                Assert.Equal("Acme.App", update.Id);
                Assert.Equal("registry-key", update.InstalledRegistryId);
                Assert.Equal("2.0.0", update.AvailableVersion);
            }
            else
                Assert.Empty(updates);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RegistryAppWithoutPublisherOrProvenInstallerTypeOffersLatestMsiVersion()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-api-msi-migration-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new CatalogHandler(R2Fixture.Create(version: "26.09.3", installerType: "wix").Payloads));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"), Path.Combine(root, "records.json"));
        var installed = InstalledAppsService.ToUpdateInventoryEntry(new InstalledAppDetail
        {
            Id = "legacy-key", DisplayName = "Acme App", DisplayVersion = "23.10", Scope = "user",
            UninstallString = @"C:\Apps\Acme\uninstall.exe"
        });
        try
        {
            var offer = Assert.Single(await api.GetUpdatesAsync(new[] { installed }));
            Assert.Equal("Acme.App", offer.Id);
            Assert.Equal("26.09.3", offer.AvailableVersion);
            var plan = await api.PrepareUpdateAsync(offer, offer.AvailableVersion);
            Assert.Equal("26.09.3", plan.Manifest.Version);
            Assert.Equal("wix", plan.Installer.Type);
            Assert.Equal("user", plan.InstalledPackage.InstalledScope);
            Assert.Equal("legacy-key", plan.InstalledPackage.InstalledRegistryId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ApiPreflightRechecksTheRegistryWhenTheAppUpdatesAfterTheScan()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-api-race-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(new CatalogHandler(R2Fixture.Create().Payloads));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"));
        int reads = 0;
        var backend = new WinGetService(new WingetExecutor(), api, new WingetBootstrapper(),
            preferredUpdateMethod: () => PackageUpdateMethod.WinProvisionApi,
            stuckUpgradeStatePath: Path.Combine(root, "stuck.json"),
            provision: (_, _) => throw new InvalidOperationException("Must not use WinGet."),
            apiInventory: _ => Task.FromResult(new List<UpgradablePackage> { new()
            {
                Id = "registry-key", Name = "Acme App", CurrentVersion = ++reads >= 3 ? "2.0.0" : "1.0.0",
                Source = "winget", InstalledScope = "user", InstalledArchitecture = "x64"
            } }));
        try
        {
            var offer = Assert.Single(await backend.GetUpgradablePackagesAsync());
            var result = await backend.UpdateAsync(offer.Id, offeredPackage: offer);
            Assert.True(result.Success);
            Assert.True(result.UpdateAlreadySatisfied);
            Assert.False(result.InstallerStarted);
            Assert.Equal(3, reads);
            Assert.False(File.Exists(Path.Combine(root, "stuck.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("offline");
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = cancellationToken.Register(() => response.TrySetCanceled(cancellationToken));
            return await response.Task.ConfigureAwait(false);
        }
    }

    private sealed class CatalogHandler(IReadOnlyDictionary<string, byte[]> payloads) : HttpMessageHandler
    {
        public System.Collections.Concurrent.ConcurrentBag<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            string prefix = "/winget/v1/";
            if (!path.StartsWith(prefix, StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            string relative = Uri.UnescapeDataString(path[prefix.Length..]);
            Requests.Add(relative);
            if (!payloads.TryGetValue(relative, out var payload))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            };
            return Task.FromResult(response);
        }
    }

    private sealed record R2Fixture(IReadOnlyDictionary<string, byte[]> Payloads)
    {
        public static R2Fixture Create(bool duplicateName = false, bool distinctPublishers = false,
            string version = "2.0.0", string installerType = "exe")
        {
            byte[] detail = Encoding.UTF8.GetBytes("""
                {"schemaVersion":1,"id":"Acme.App","version":"2.0.0","source":"winget","installers":[{"architecture":"x64","type":"exe","url":"https://example.invalid/acme.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","silent":{"supported":true,"arguments":"/S"}}]}
                """.Replace("2.0.0", version, StringComparison.Ordinal)
                    .Replace("\"type\":\"exe\"", $"\"type\":\"{installerType}\"", StringComparison.Ordinal));
            string detailHash = Convert.ToHexString(SHA256.HashData(detail)).ToLowerInvariant();
            byte[] app = Encoding.UTF8.GetBytes($$"""
                {"schemaVersion":1,"id":"Acme.App","source":"winget","latestVersion":"2.0.0","versions":[{"version":"2.0.0","detailPath":"versions/2.0.0/app.json","sha256":"{{detailHash}}","architectures":["x64"]}],"package":{"name":"Acme App","publisher":"Acme"},"architectures":["x64"]}
                """.Replace("2.0.0", version, StringComparison.Ordinal));
            string appHash = Convert.ToHexString(SHA256.HashData(app)).ToLowerInvariant();
            byte[] index = Encoding.UTF8.GetBytes($$"""
                [{"id":"Acme.App","name":"Acme App","publisher":"Acme","latestVersion":"2.0.0","appPath":"manifests/a/acme/app/app.json","appSha256":"{{appHash}}","architectures":["x64"]}]
                """.Replace("2.0.0", version, StringComparison.Ordinal));
            if (duplicateName)
            {
                string json = Encoding.UTF8.GetString(index);
                string duplicate = json[1..^1].Replace("Acme.App", "Acme.Other", StringComparison.Ordinal);
                if (distinctPublishers)
                    duplicate = duplicate.Replace("\"publisher\":\"Acme\"", "\"publisher\":\"Other Publisher\"", StringComparison.Ordinal);
                index = Encoding.UTF8.GetBytes(json[..^1] + "," + duplicate + "]");
            }
            string indexHash = Convert.ToHexString(SHA256.HashData(index)).ToLowerInvariant();
            byte[] catalog = Encoding.UTF8.GetBytes($$"""
                {"schemaVersion":1,"packageCount":{{(duplicateName ? 2 : 1)}},"searchIndexPath":"winget/v1/search/{{indexHash}}.json","searchIndexSha256":"{{indexHash}}","generation":"test-generation","generatedAtUtc":"{{DateTimeOffset.UtcNow:O}}"}
                """);

            return new R2Fixture(new Dictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["catalog.json"] = catalog,
                [$"search/{indexHash}.json"] = index,
                ["manifests/a/acme/app/app.json"] = app,
                [$"manifests/a/acme/app/versions/{version}/app.json"] = detail
            });
        }
    }
}
