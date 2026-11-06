using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinProvisionApiReleaseCatalogTests
{
    private const string DatabaseSha256 = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public async Task HealthCheckUsesTheCurrentReleaseCatalog()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var snapshot = new ReleaseCatalogSnapshot(commit, 2, 0, 0, "unused.sqlite", [], DatabaseSha256: DatabaseSha256);
        var release = new StubReleaseIndexService(refresh: snapshot);
        using var http = new HttpClient(new JsonHandler(_ => null));
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-health-" + Guid.NewGuid().ToString("N"));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"),
            Path.Combine(root, "records.json"), releaseCatalogIndexService: release);
        try
        {
            CatalogApiHealth health = await api.CheckCatalogHealthAsync();
            Assert.True(Assert.Single(health.Sources).Available);
            Assert.Equal(1, release.RefreshCalls);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UsesValidatedReleaseIndexAndFetchesDetailsAtItsPinnedCommit()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        const string appPath = "c/Contoso/App/app.json";
        const string versionPath = "versions/1.0/app.json";
        const string detail = """{"schemaVersion":1,"source":"winget","id":"Contoso.App","version":"1.0","installers":[{"type":"exe","url":"https://contoso.example/setup.exe","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","elevation":{"requirement":"elevationRequired"}}]}""";
        string app = """{"schemaVersion":1,"source":"winget","id":"Contoso.App","latestVersion":"1.0","package":{"name":"Contoso App","publisher":"Contoso"},"architectures":["x64"],"versions":[{"version":"1.0","detailPath":"versions/1.0/app.json","detailSha256":"DETAIL_HASH"}]}"""
            .Replace("DETAIL_HASH", Hash(detail), StringComparison.Ordinal);
        var appEntry = new AppEntry
        {
            Id = "Contoso.App", Source = "winget", Name = "Contoso App", Publisher = "Contoso", Version = "1.0",
            Architectures = ["x64"], CatalogDetailPath = appPath, CatalogDetailSha256 = Hash(app),
            CatalogDetailCatalogSha256 = commit
        };
        var release = new ReleaseCatalogSnapshot(commit, 2, 1, 1, "unused.sqlite", (AppEntry[])[appEntry],
            DatabaseSha256: DatabaseSha256);
        var releaseService = new StubReleaseIndexService(refresh: release);
        var handler = new JsonHandler(request =>
        {
            string expectedPrefix = $"/winprovision-services/WinProvision-PKGS/{commit}/";
            Assert.StartsWith(expectedPrefix, request.RequestUri!.AbsolutePath, StringComparison.Ordinal);
            string path = request.RequestUri.AbsolutePath[expectedPrefix.Length..];
            return path switch
            {
                appPath => app,
                $"c/Contoso/App/{versionPath}" => detail,
                _ => null
            };
        });
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-release-index-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient(handler);
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"),
            Path.Combine(root, "records.json"), releaseCatalogIndexService: releaseService);

        try
        {
            PackageIndex index = await api.GetIndexAsync(forceRefresh: true);
            PackageIndexEntry packageEntry = Assert.Single(index.Packages);
            Assert.Equal("Contoso.App", packageEntry.Id);
            Assert.Equal(DatabaseSha256.ToLowerInvariant(), index.SourceIndexHashes["winget"]);

            PackageManifest package = Assert.IsType<PackageManifest>(await api.GetPackageAsync("Contoso.App"));
            Assert.Equal("1.0", package.Version);
            Assert.Equal("elevationRequired", Assert.Single(package.Installers).ElevationRequirement);
            Assert.Equal(1, releaseService.RefreshCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FallsBackToFreshCdnIndexWhenReleaseIndexIsUnavailable()
    {
        string indexBytes = """[{"id":"Contoso.App","appPath":"manifests/c/contoso/app/app.json","appSha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","name":"Contoso App","latestVersion":"1.0","architectures":["x64"]}]""";
        string indexHash = Hash(indexBytes);
        string catalog = $$"""{"schemaVersion":1,"generation":"generation-1","generatedAtUtc":"2026-10-21T00:00:00Z","packageCount":1,"searchIndexPath":"winget/v1/search/{{indexHash}}.json","searchIndexSha256":"{{indexHash}}"}""";
        var releaseService = new StubReleaseIndexService(refreshException: new HttpRequestException("release unavailable"));
        using var http = new HttpClient(new JsonHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/winget/v1/catalog.json", StringComparison.Ordinal)) return catalog;
            if (path.EndsWith($"/winget/v1/search/{indexHash}.json", StringComparison.Ordinal)) return indexBytes;
            return null;
        }));
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-release-fallback-" + Guid.NewGuid().ToString("N"));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"),
            Path.Combine(root, "records.json"), releaseCatalogIndexService: releaseService);

        try
        {
            PackageIndex index = await api.GetIndexAsync(forceRefresh: true);
            Assert.Equal("Contoso.App", Assert.Single(index.Packages).Id);
            Assert.Equal(indexHash, index.SourceIndexHashes["winget"]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RequireRemoteDoesNotAcceptCachedReleaseOrCdnIndex()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var cached = new ReleaseCatalogSnapshot(commit, 2, 0, 0, "unused.sqlite", [], DatabaseSha256: DatabaseSha256);
        var releaseService = new StubReleaseIndexService(refresh: cached,
            refreshExceptionAfterFirst: new HttpRequestException("release unavailable"));
        using var http = new HttpClient(new JsonHandler(_ => null));
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-release-strict-" + Guid.NewGuid().ToString("N"));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"),
            Path.Combine(root, "records.json"), releaseCatalogIndexService: releaseService);

        try
        {
            Assert.Empty((await api.GetIndexAsync(forceRefresh: true)).Packages);
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                api.GetIndexAsync(requireRemote: true));
            Assert.Equal(1, releaseService.LocalCalls);
            Assert.Equal(2, releaseService.RefreshCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PrefersRecentSqliteCacheOverLegacyCdnWhenReleaseRefreshFails()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-release-priority-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "winprovision-catalog.sqlite");
        await File.WriteAllBytesAsync(database, [1, 2, 3]);
        File.SetLastWriteTimeUtc(database, DateTime.UtcNow.AddDays(-2));
        var release = new ReleaseCatalogSnapshot(commit, 2, 0, 0, database, [], DatabaseSha256: HashBytes([1, 2, 3]));
        var releaseService = new StubReleaseIndexService(local: release,
            refreshException: new HttpRequestException("release unavailable"));
        int httpCalls = 0;
        using var http = new HttpClient(new JsonHandler(_ => { httpCalls++; return null; }));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"),
            Path.Combine(root, "records.json"), releaseCatalogIndexService: releaseService);

        try
        {
            PackageIndex index = await api.GetIndexAsync(forceRefresh: true);
            Assert.Equal(release.DatabaseSha256!.ToLowerInvariant(), index.SourceIndexHashes["winget"]);
            Assert.Equal(0, httpCalls);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(-31)]
    [InlineData(1)]
    public async Task OfflineUpdateDiscoveryRejectsSqliteSnapshotOutsideValidAge(int days)
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-release-expiration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "winprovision-catalog.sqlite");
        await File.WriteAllBytesAsync(database, [1, 2, 3]);
        File.SetLastWriteTimeUtc(database, DateTime.UtcNow.AddDays(days));
        var app = new AppEntry
        {
            Id = "Contoso.App", Source = "winget", Name = "Contoso App", Version = "2.0",
            CatalogDetailPath = "c/Contoso/App/app.json", CatalogDetailSha256 = new string('A', 64),
            CatalogDetailCatalogSha256 = commit
        };
        var release = new ReleaseCatalogSnapshot(commit, 2, 1, 1, database, (AppEntry[])[app],
            DatabaseSha256: HashBytes([1, 2, 3]));
        var releaseService = new StubReleaseIndexService(local: release,
            refreshException: new HttpRequestException("release unavailable"));
        using var http = new HttpClient(new JsonHandler(_ => null));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"),
            Path.Combine(root, "records.json"), releaseCatalogIndexService: releaseService);

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => api.GetUpdatesAsync(
                (UpgradablePackage[])[new UpgradablePackage { Id = "Contoso.App", Name = "Contoso App", Source = "winget", CurrentVersion = "1.0" }],
                refreshRemote: false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineUpdateDiscoveryRejectsFutureSqliteValidationTimestamp()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-release-future-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "winprovision-catalog.sqlite");
        await File.WriteAllBytesAsync(database, [1, 2, 3]);
        var app = new AppEntry
        {
            Id = "Contoso.App", Source = "winget", Name = "Contoso App", Version = "2.0",
            CatalogDetailPath = "c/Contoso/App/app.json", CatalogDetailSha256 = new string('A', 64),
            CatalogDetailCatalogSha256 = commit
        };
        var release = new ReleaseCatalogSnapshot(commit, 2, 1, 1, database, (AppEntry[])[app],
            DateTimeOffset.UtcNow.AddDays(2), DatabaseSha256);
        var releaseService = new StubReleaseIndexService(local: release,
            refreshException: new HttpRequestException("release unavailable"));
        using var http = new HttpClient(new JsonHandler(_ => null));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"),
            Path.Combine(root, "records.json"), releaseCatalogIndexService: releaseService);

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => api.GetUpdatesAsync(
                (UpgradablePackage[])[new UpgradablePackage { Id = "Missing.App", Name = "Missing App", Source = "winget", CurrentVersion = "1.0" }],
                refreshRemote: false));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task OfflineUpdateDiscoveryUsesRecentlyRevalidatedSqliteSnapshotWithOldDatabaseMtime()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-release-validated-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string database = Path.Combine(root, "winprovision-catalog.sqlite");
        await File.WriteAllBytesAsync(database, [1, 2, 3]);
        File.SetLastWriteTimeUtc(database, DateTime.UtcNow.AddDays(-45));
        var app = new AppEntry
        {
            Id = "Contoso.App", Source = "winget", Name = "Contoso App", Version = "2.0",
            CatalogDetailPath = "c/Contoso/App/app.json", CatalogDetailSha256 = new string('A', 64),
            CatalogDetailCatalogSha256 = commit
        };
        var release = new ReleaseCatalogSnapshot(commit, 2, 1, 1, database, (AppEntry[])[app],
            DateTimeOffset.UtcNow.AddMinutes(-1), DatabaseSha256);
        var releaseService = new StubReleaseIndexService(localAfterFirst: release,
            refreshException: new HttpRequestException("release unavailable"));
        using var http = new HttpClient(new JsonHandler(_ => null));
        await using var api = new WinProvisionApiService(http, Path.Combine(root, "cache"),
            Path.Combine(root, "records.json"), releaseCatalogIndexService: releaseService);

        try
        {
            List<UpgradablePackage> updates = await api.GetUpdatesAsync(
                (UpgradablePackage[])[new UpgradablePackage { Id = "Missing.App", Name = "Missing App", Source = "winget", CurrentVersion = "1.0" }]);
            Assert.Empty(updates);
            Assert.True(releaseService.LocalCalls >= 2);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string HashBytes(byte[] value) => Convert.ToHexString(SHA256.HashData(value));

    private sealed class StubReleaseIndexService(ReleaseCatalogSnapshot? local = null,
        ReleaseCatalogSnapshot? refresh = null, Exception? refreshException = null,
        Exception? refreshExceptionAfterFirst = null, ReleaseCatalogSnapshot? localAfterFirst = null) : IReleaseCatalogIndexService
    {
        public int LocalCalls { get; private set; }
        public int RefreshCalls { get; private set; }

        public Task<ReleaseCatalogSnapshot?> TryLoadLocalSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LocalCalls++;
            return Task.FromResult(LocalCalls > 1 ? localAfterFirst ?? local : local);
        }

        public Task<ReleaseCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCalls++;
            if (RefreshCalls > 1 && refreshExceptionAfterFirst is not null) throw refreshExceptionAfterFirst;
            if (refreshException is not null) throw refreshException;
            return Task.FromResult(refresh ?? throw new InvalidOperationException("No release snapshot configured."));
        }

        public Task ClearCacheAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class JsonHandler(Func<HttpRequestMessage, string?> content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? value = content(request);
            return Task.FromResult(value is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(value, Encoding.UTF8, "application/json")
                });
        }
    }
}
