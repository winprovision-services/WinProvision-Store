using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public sealed class FeaturedCatalogDocument
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("generatedUtc")] public DateTimeOffset GeneratedUtc { get; set; }
    [JsonPropertyName("metricWindowDays")] public int MetricWindowDays { get; set; }
    [JsonPropertyName("installMetricsAvailable")] public bool InstallMetricsAvailable { get; set; }
    [JsonPropertyName("hero")] public List<FeaturedAppReference> Hero { get; set; } = [];
    [JsonPropertyName("popular")] public List<FeaturedAppReference> Popular { get; set; } = [];
    [JsonPropertyName("categories")] public List<FeaturedCategory> Categories { get; set; } = [];
}

public sealed class FeaturedAppReference
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = string.Empty;
    [JsonPropertyName("installCount30d")] public int InstallCount30d { get; set; }
    [JsonPropertyName("category")] public string? Category { get; set; }
}

public sealed class FeaturedCategory
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("apps")] public List<FeaturedAppReference> Apps { get; set; } = [];
}

/// <summary>Cache local e atualização em segundo plano do catálogo editorial de Destaques.</summary>
public sealed class FeaturedCatalogService : IAsyncDisposable
{
    private const string CatalogUrl = CatalogEndpoints.FeaturedUrl;
    private static readonly HttpClient Http = NetworkClientFactory.Create(
        TimeSpan.FromSeconds(12), pooledConnectionLifetime: TimeSpan.FromMinutes(5));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
    private readonly string _cachePath;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly System.Threading.Lock _lifecycleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly System.Threading.Lock _loadSync = new();
    private Task? _refreshTask;
    private Task<FeaturedCatalogDocument?>? _loadTask;
    private bool _disposed;
    private FeaturedCatalogDocument? _current;
    private DateTimeOffset _lastRefreshAttemptUtc;

    public event Action<FeaturedCatalogDocument>? Updated;

    public FeaturedCatalogService(string? cacheDirectory = null)
    {
        string root = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "Cache");
        _cachePath = Path.Combine(root, "featured-catalog.json");
    }

    public async Task<FeaturedCatalogDocument?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_current is null)
        {
            Task<FeaturedCatalogDocument?> loadTask;
            lock (_loadSync)
            {
                loadTask = _loadTask ??= ReadCacheAsync(_lifetime.Token);
            }

            _current = await loadTask.ConfigureAwait(false);
            try
            {
                if (File.Exists(_cachePath))
                    _lastRefreshAttemptUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(_cachePath), TimeSpan.Zero);
            }
            catch (IOException) { }
        }

        TimeSpan interval = _current is null ? RetryInterval : RefreshInterval;
        if (DateTimeOffset.UtcNow - _lastRefreshAttemptUtc >= interval)
            _ = RefreshInBackgroundAsync();
        return _current;
    }

    private async Task<FeaturedCatalogDocument?> ReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_cachePath)) return null;
            await using FileStream stream = new(
                _cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1024, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
            FeaturedCatalogDocument? document = await WinProvision.Core.Services.AotJson
                .DeserializeAsync<FeaturedCatalogDocument>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            return Validate(document);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            WinProvision.Core.Services.CoreLog.Debug($"[FeaturedCatalog] Cache local indisponível: {ex.Message}");
            return null;
        }
    }

    private Task RefreshInBackgroundAsync()
    {
        lock (_lifecycleGate)
        {
            if (_disposed) return Task.CompletedTask;
            if (_refreshTask is { IsCompleted: false }) return _refreshTask;
            return _refreshTask = RefreshCoreAsync();
        }
    }

    private async Task RefreshCoreAsync()
    {
        if (!await _refreshGate.WaitAsync(0).ConfigureAwait(false)) return;
        _lastRefreshAttemptUtc = DateTimeOffset.UtcNow;
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, _lifetime.Token)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using Stream stream = await response.Content.ReadAsStreamAsync(_lifetime.Token).ConfigureAwait(false);
            FeaturedCatalogDocument? document = await WinProvision.Core.Services.AotJson
                .DeserializeAsync<FeaturedCatalogDocument>(stream, JsonOptions, _lifetime.Token)
                .ConfigureAwait(false);
            if (document is null) return;

            Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
            await AtomicFile.WriteAsync(
                _cachePath,
                async (cacheStream, token) =>
                {
                    await JsonSerializer.SerializeAsync(
                        cacheStream,
                        document,
                        WinProvision.Core.Services.AotJson.TypeInfo<FeaturedCatalogDocument>(JsonOptions),
                        token).ConfigureAwait(false);
                },
                _lifetime.Token).ConfigureAwait(false);
            _current = document;
            Updated?.Invoke(document);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            CoreLog.Warning(ex, "RefreshFeaturedCatalog");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static FeaturedCatalogDocument? Validate(FeaturedCatalogDocument? document)
    {
        if (document is not { SchemaVersion: 1, MetricWindowDays: > 0 }
            || document.Hero is null || document.Popular is null || document.Categories is null)
            return null;

        static bool Valid(FeaturedAppReference app) =>
            !string.IsNullOrWhiteSpace(app.Id)
            && app.Id.Length <= 128
            && app.Source is "winget" or "msstore"
            && !string.IsNullOrWhiteSpace(app.Name);

        document.Hero = document.Hero.Where(Valid).Take(3).ToList();
        document.Popular = document.Popular.Where(Valid).Take(48).ToList();
        document.Categories = document.Categories
            .Where(category => category.Apps is not null && !string.IsNullOrWhiteSpace(category.Title))
            .Select(category => new FeaturedCategory
            {
                Id = category.Id,
                Title = category.Title,
                Apps = category.Apps.Where(Valid).Take(8).ToList()
            })
            .Where(category => category.Apps.Count > 0)
            .Take(12)
            .ToList();
        return document;
    }
    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            pending = _refreshTask;
        }
        try
        {
            if (pending is not null) await pending.ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Dispose();
            _lifetime.Dispose();
        }
    }

}
