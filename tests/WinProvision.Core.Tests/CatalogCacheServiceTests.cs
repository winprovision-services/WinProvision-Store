using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class CatalogCacheServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "WinProvision-cache-" + Guid.NewGuid().ToString("N"));
    private readonly Clock _clock = new();
    private const string Url = "https://catalog.example/manifest.json";
    private CatalogCacheService Cache(string name = "local", string? shared = null)
        => new(Path.Combine(_root, name), new(shared), _clock);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    private static HttpResponseMessage Json(string value = "{}") => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [Fact]
    public async Task FreshCacheSurvivesNewProcessesAndExpiredManifestUsesConditionalRequest()
    {
        using var handler = new Handler((request, count) =>
        {
            if (count == 1) { var response = Json(); response.Headers.ETag = new EntityTagHeaderValue("\"v1\""); return response; }
            Assert.Equal("\"v1\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            return new(HttpStatusCode.NotModified);
        });
        using var client = new HttpClient(handler);
        using var first = await Cache().GetAsync(client, Url, default);
        using var second = await Cache().GetAsync(client, Url, default);
        Assert.Equal(1, handler.Count);
        _clock.Advance(TimeSpan.FromHours(7));
        using var third = await Cache().GetAsync(client, Url, default);
        using var fourth = await Cache().GetAsync(client, Url, default);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task ContentHashReusesPayloadBeyondManifestTtlButNewHashDownloadsNewVersion()
    {
        using var handler = new Handler((_, count) => Json(count == 1 ? "{}" : "[]"));
        using var client = new HttpClient(handler);
        using var first = await Cache().GetAsync(client, Url, default, Hash("{}"));
        _clock.Advance(TimeSpan.FromDays(20));
        using var reused = await Cache().GetAsync(client, Url, default, Hash("{}"));
        Assert.Equal(1, handler.Count);
        using var updated = await Cache().GetAsync(client, Url, default, Hash("[]"));
        Assert.Equal("[]", await updated.Content.ReadAsStringAsync());
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task TwoMachinesShareOneDownloadAndKeepIndependentLocalMirrors()
    {
        string shared = Path.Combine(_root, "share");
        using var handler = new Handler((_, _) => Json());
        using var client = new HttpClient(handler);
        using var first = await Cache("machine1", shared).GetAsync(client, Url, default);
        using var second = await Cache("machine2", shared).GetAsync(client, Url, default);
        Assert.Equal(1, handler.Count);
        Directory.Move(shared, shared + "-offline");
        using var offline = await Cache("machine2", shared).GetAsync(client, Url, default);
        Assert.Equal(1, handler.Count);
        await Cache("machine2", shared).ClearAsync();
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(shared + "-offline", "WinProvisionCache", "Catalog-v1", "objects")));
    }

    [Fact]
    public async Task ConcurrentInstancesDeduplicateNetworkDownload()
    {
        using var handler = new Handler((_, _) => Json());
        using var client = new HttpClient(handler);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Cache().GetAsync(client, Url, default)));
        foreach (var result in results) result.Dispose();
        Assert.Equal(1, handler.Count);
    }

    [Fact]
    public async Task FailedRevalidationServesStaleAndPersistsCooldownButDiagnosticFails()
    {
        using var handler = new Handler((_, count) => count == 1 ? Json() : new(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        using var first = await Cache().GetAsync(client, Url, default);
        _clock.Advance(TimeSpan.FromHours(7));
        using var stale = await Cache().GetAsync(client, Url, default);
        using var nextProcess = await Cache().GetAsync(client, Url, default);
        Assert.Equal(2, handler.Count);
        await Assert.ThrowsAsync<IOException>(() => Cache().GetAsync(client, Url, default, allowStale: false));
        Assert.Equal(2, handler.Count);
        _clock.Advance(TimeSpan.FromMinutes(16));
        using var retry = await Cache().GetAsync(client, Url, default);
        Assert.Equal(3, handler.Count);
    }

    [Fact]
    public async Task HashMismatchCannotPoisonCacheAndRepeatedFailureDoesNotRedownload()
    {
        using var handler = new Handler((_, _) => Json());
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => Cache().GetAsync(client, Url, default, Hash("[]")));
        await Assert.ThrowsAsync<IOException>(() => Cache().GetAsync(client, Url, default, Hash("[]")));
        Assert.Equal(1, handler.Count);
        using var correctedManifest = await Cache().GetAsync(client, Url, default, Hash("{}"));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task MissingResourceHasPersistedCooldownAndExplicitRefreshBypassesIt()
    {
        using var handler = new Handler((_, _) => new(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        using var first = await Cache().GetAsync(client, Url, default);
        using var second = await Cache().GetAsync(client, Url, default);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Equal(1, handler.Count);
        using var forced = await Cache().GetAsync(client, Url, default, forceRefresh: true);
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task CorruptDiskPayloadIsRefetchedAndValidated()
    {
        using var handler = new Handler((_, _) => Json());
        using var client = new HttpClient(handler);
        var cache = Cache();
        using var first = await cache.GetAsync(client, Url, default, Hash("{}"));
        await File.WriteAllTextAsync(Assert.Single(Directory.GetFiles(Path.Combine(cache.LocalRoot, "objects"))), "broken");
        using var repaired = await Cache().GetAsync(client, Url, default, Hash("{}"));
        Assert.Equal("{}", await repaired.Content.ReadAsStringAsync());
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public async Task CleanupRemovesOnlyOwnedExpiredFilesAndHonorsQuota()
    {
        var cache = Cache();
        string objects = Path.Combine(cache.LocalRoot, "objects");
        Directory.CreateDirectory(objects);
        string expired = Path.Combine(objects, new string('a', 64) + ".bin");
        await File.WriteAllTextAsync(expired, "{}");
        File.SetLastWriteTimeUtc(expired, _clock.GetUtcNow().AddDays(-31).UtcDateTime);
        string unrelated = Path.Combine(objects, "my-document.txt");
        await File.WriteAllTextAsync(unrelated, "preserve");
        await cache.MaintainAsync(true);
        Assert.False(File.Exists(expired));
        Assert.True(File.Exists(unrelated));
        Assert.Equal(0, (await cache.GetUsageAsync()).Bytes);
        cache = new(cache.LocalRoot, new(MaximumMegabytes: 128), _clock);
        for (int i = 0; i < 3; i++)
        {
            string path = Path.Combine(objects, new string((char)('b' + i), 64) + ".bin");
            using (var file = File.Create(path)) file.SetLength(64L * 1024 * 1024);
            File.SetLastWriteTimeUtc(path, _clock.GetUtcNow().AddHours(-3 + i).UtcDateTime);
        }
        await cache.MaintainAsync(true);
        Assert.Equal(128L * 1024 * 1024, (await cache.GetUsageAsync()).Bytes);
        Assert.False(File.Exists(Path.Combine(objects, new string('b', 64) + ".bin")));
        Assert.True(File.Exists(unrelated));
    }

    [Fact]
    public async Task InterfaceAndCliBackendReuseSameManifestsAndIndexes()
    {
        var requests = new Dictionary<string, int>();
        using var handler = new Handler((request, _) =>
        {
            string path = request.RequestUri!.AbsolutePath;
            requests[path] = requests.GetValueOrDefault(path) + 1;
            string source = path.Contains("/msstore/") ? "msstore" : "winget";
            string detail = $$"""{"schema":2,"id":"Test.App","name":"Teste","version":"1","source":"{{source}}","installers":[]}""";
            if (path.EndsWith("/app.json")) return Json(detail);
            string index = $$"""[{"id":"Test.App","name":"Teste","version":"1","source":"{{source}}","detailPath":"apps/t/test/app/app.json","recordSha256":"{{Hash(detail)}}","installerCount":0}]""";
            return Json(path.EndsWith("search-index.json") ? index : $$"""{"schemaVersion":2,"generatedUtc":"{{DateTimeOffset.UtcNow:O}}","appCount":1,"indexSha256":"{{Hash(index)}}","catalogSha256":"{{new string('b', 64)}}"}""");
        });
        using var client = new HttpClient(handler);
        await using var store = new StoreService(client, null, Path.Combine(_root, "ui"), Cache());
        var apps = await store.LoadCatalogAsync();
        Assert.NotEmpty(apps);
        await using var api = new WinProvisionApiService(client, Path.Combine(_root, "api"), Cache());
        Assert.Equal(2, (await api.GetIndexAsync()).Count);
        Assert.Equal(4, requests.Count);
        Assert.All(requests.Values, count => Assert.Equal(1, count));
        await store.LoadDetailsAsync(apps.First(app => app.Source == "msstore"));
        Assert.NotNull(await api.GetPackageAsync("Test.App", source: "msstore"));
        Assert.Equal(5, requests.Count);
        Assert.All(requests.Values, count => Assert.Equal(1, count));
        Assert.False(Directory.Exists(Path.Combine(_root, "ui", "catalog-v2-details")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "api"), "catalog-app-*.json"));
    }

    [Fact]
    public async Task ConnectionDiagnosticRequestsOnlySmallManifestsAndNeverUsesOfflineSuccess()
    {
        bool unavailable = false;
        using var handler = new Handler((request, _) =>
        {
            Assert.EndsWith("/manifest.json", request.RequestUri!.AbsolutePath);
            if (unavailable) return new(HttpStatusCode.ServiceUnavailable);
            return Json($$"""{"schemaVersion":2,"generatedUtc":"{{DateTimeOffset.UtcNow:O}}","appCount":1,"indexSha256":"{{new string('a', 64)}}"}""");
        });
        using var client = new HttpClient(handler);
        await using var api = new WinProvisionApiService(client, Path.Combine(_root, "api"), Cache());
        Assert.All((await api.CheckCatalogHealthAsync()).Sources, source => Assert.True(source.Available));
        Assert.Equal(2, handler.Count);
        unavailable = true;
        Assert.All((await api.CheckCatalogHealthAsync()).Sources, source => Assert.False(source.Available));
        Assert.Equal(4, handler.Count);
    }

    [Fact]
    public async Task CancellationPublishesNoPayloadAndReleasesLease()
    {
        using var handler = new Handler((_, _) => Json());
        using var client = new HttpClient(handler);
        var cache = Cache();
        await cache.MaintainAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetAsync(client, Url, cancel.Token));
        Assert.Equal(0, (await cache.GetUsageAsync()).Files);
        using var retry = await Cache().GetAsync(client, Url, default);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
    }

    [Fact]
    public async Task DiagnosticSizeLimitAppliesToPreviouslyCachedLargerPayload()
    {
        using var handler = new Handler((_, _) => Json("{\"content\":\"" + new string('x', 1024) + "\"}"));
        using var client = new HttpClient(handler);
        using var first = await Cache().GetAsync(client, Url, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => Cache().GetAsync(client, Url, default,
            forceRefresh: true, maximumBytes: 128));
        Assert.Equal(2, handler.Count);
    }

    [Fact]
    public void SettingsPersistAndRejectRelativePaths()
    {
        string path = Path.Combine(_root, "preferences.json");
        var settings = new CatalogCacheSettings(Path.Combine(_root, "shared"), 256, 12);
        CatalogCacheSettingsStore.Save(settings, path);
        Assert.Equal(settings, CatalogCacheSettingsStore.Load(path));
        Assert.Throws<ArgumentException>(() => new CatalogCacheSettings("relative").Validate());
        new CatalogCacheSettings(@"\\server\cache").Validate();
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _count;
        public int Count => _count;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int count = Interlocked.Increment(ref _count);
            await Task.Delay(20, cancellationToken);
            return respond(request, count);
        }
    }
}
