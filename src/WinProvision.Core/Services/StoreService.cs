using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Collections.Concurrent;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public class StoreService : IStoreCatalog
{
#if DEBUG
    private static long _catalogJsonReads;
    private static long _catalogJsonDeserializations;
    public static (long Reads, long Deserializations) GetCatalogJsonDiagnostics() =>
        (Interlocked.Read(ref _catalogJsonReads), Interlocked.Read(ref _catalogJsonDeserializations));
#else
    public static (long Reads, long Deserializations) GetCatalogJsonDiagnostics() => (0, 0);
#endif
    private const string CatalogV2BaseUrl = CatalogEndpoints.PublicBaseUrl + "/Store/Catalog";
    private const string CatalogManifestUrl = CatalogV2BaseUrl + "/manifest.json";
    private const string MsStoreCatalogBaseUrl = CatalogV2BaseUrl + "/msstore";
    private const string MsStoreManifestUrl = MsStoreCatalogBaseUrl + "/manifest.json";
    private const string LegacyScreenshotBasePath = "/Store/Screenshot_Database/";
    private readonly string _cacheDirectory;
    private readonly string _catalogManifestCacheFilePath;
    private readonly string _catalogManifestEtagPath;
    private readonly string _catalogManifestLastModifiedPath;
    private readonly string _catalogDetailsCacheDirectory;
    private readonly string _screenshotIndexCacheFilePath;
    private readonly string _screenshotIndexEtagPath;
    private readonly string _screenshotIndexLastModifiedPath;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private bool _disposed;
    private readonly IconService _iconService;
    private bool _loadedCatalogV2;
    private readonly CancellationTokenSource _startupCatalogCancellation = new();
    private Task? _startupCatalogTask;

    // Opções centralizadas compartilhadas pelo app para manter o cache compacto
    // e compatível com o modelo AppEntry usado pelo cliente.
    private static readonly JsonSerializerOptions _jsonOptions = WinProvisionJsonOptions.Compact;

    private static T? DeserializeCatalogJson<T>(string json, string source)
    {
#if DEBUG
        Interlocked.Increment(ref _catalogJsonReads);
        long startedAt = Stopwatch.GetTimestamp();
        T? result = AotJson.Deserialize<T>(json, _jsonOptions);
        long count = Interlocked.Increment(ref _catalogJsonDeserializations);
        CoreLog.Debug("[Performance]", $"catalog-json source={source} reads={Interlocked.Read(ref _catalogJsonReads)} deserializations={count} elapsedMs={Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds:F1}");
        return result;
#else
        return AotJson.Deserialize<T>(json, _jsonOptions);
#endif
    }

    private List<AppEntry> _cachedCatalog = [];
    private SearchDocument[] _searchIndex = [];
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly System.Threading.Lock _refreshSync = new();
    private Task<List<AppEntry>>? _refreshTask;
    private CancellationTokenSource? _refreshCts;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _detailLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (CatalogV2App App, DateTimeOffset Expires)> _wingetApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly CatalogV2Client _catalogV2Client;
    private string? _loadedSearchIndexCacheFilePath;
    private string? _catalogSha256;
    private string? _loadedIndexSha256;
    private int _cacheGeneration;
    private bool _startupRevalidationScheduled;

    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromHours(6);

    /// <summary>
    /// Disparado sempre que _cachedCatalog é trocado por uma lista nova
    /// (refresh em background ou forçado). Quem mantém uma referência própria
    /// ao catálogo (ex.: HomePage._allApps) deve assinar isso para não ficar
    /// com dados/flags de instâncias antigas de AppEntry.
    /// </summary>
    public event Action<IReadOnlyList<AppEntry>>? CatalogUpdated;
    public event Action? CacheCleared;

    public StoreService(HttpClient? httpClient = null, IconService? iconService = null)
        : this(httpClient, iconService, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore"))
    {
    }

    internal StoreService(HttpClient? httpClient, IconService? iconService, string cacheDirectory)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? NetworkClientFactory.Create(TimeSpan.FromSeconds(30));
        _iconService = iconService ?? new IconService();

        _cacheDirectory = cacheDirectory;
        _catalogV2Client = new CatalogV2Client(_httpClient, Path.Combine(cacheDirectory, "Cache", "catalog-v2"));

        _catalogManifestCacheFilePath = Path.Combine(_cacheDirectory, "catalog-v2-manifest.json");
        _catalogManifestEtagPath = _catalogManifestCacheFilePath + ".etag";
        _catalogManifestLastModifiedPath = _catalogManifestCacheFilePath + ".lastmodified";
        _catalogDetailsCacheDirectory = Path.Combine(_cacheDirectory, "catalog-v2-details");
        _screenshotIndexCacheFilePath = Path.Combine(_cacheDirectory, "screenshot-index.json");
        _screenshotIndexEtagPath = _screenshotIndexCacheFilePath + ".etag";
        _screenshotIndexLastModifiedPath = _screenshotIndexCacheFilePath + ".lastmodified";
    }

    public Task<List<AppEntry>> LoadCatalogAsync(bool forceRefresh = false, CancellationToken cancellationToken = default) =>
        Task.Run(() => LoadCatalogCoreAsync(forceRefresh, cancellationToken), cancellationToken);

    private async Task<List<AppEntry>> LoadCatalogCoreAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            if (forceRefresh)
            {
                _startupRevalidationScheduled = true;
                return await RefreshCacheInBackgroundAsync(cancellationToken, forceRefresh: true);
            }

            if (_cachedCatalog.Count > 0)
            {
                if (!_startupRevalidationScheduled)
                {
                    _startupRevalidationScheduled = true;
                    _ = RefreshCacheInBackgroundAsync(CancellationToken.None, revalidateRemote: true);
                }
                else if (IsCacheStale())
                {
                    _ = RefreshCacheInBackgroundAsync(CancellationToken.None);
                }
                return _cachedCatalog;
            }

            _startupRevalidationScheduled = true;
            return await FetchAndSaveRemoteCatalogAsync(cancellationToken);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Catálogo completo já carregado em memória (o que LoadCatalogAsync populou).</summary>
    public IReadOnlyList<AppEntry> GetAll() => _cachedCatalog;

    /// <summary>Descarta o catálogo e o índice de busca em memória sem remover os arquivos locais.</summary>
    public async Task ReleaseCatalogMemoryAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _cacheGeneration);
        Task? refresh;
        lock (_refreshSync)
        {
            _refreshCts?.Cancel();
            refresh = _refreshTask;
        }

        if (refresh is not null)
        {
            try { await refresh.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException exception) when (_refreshCts?.IsCancellationRequested == true || cancellationToken.IsCancellationRequested)
            {
                CoreLog.Warning(exception, "ReleaseCatalogMemory.CancelledRefresh");
            }
            catch (Exception exception) { CoreLog.Warning(exception, "ReleaseCatalogMemory"); }
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SetCachedCatalog([]);
            _catalogSha256 = null;
            _loadedIndexSha256 = null;
            _loadedSearchIndexCacheFilePath = null;
            _wingetApps.Clear();
            _startupRevalidationScheduled = false;
            _loadedCatalogV2 = false;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    internal Task WaitForRefreshAsync()
    {
        lock (_refreshSync) return _refreshTask ?? Task.CompletedTask;
    }

    /// <summary>Carrega o registro completo do app selecionado sem baixar o catálogo completo.</summary>
    public Task<AppEntry> LoadDetailsAsync(AppEntry app, CancellationToken cancellationToken = default)
        => LoadDetailsCoreAsync(app, cancellationToken, allowRefresh: true);

    private async Task<AppEntry> LoadDetailsCoreAsync(AppEntry app, CancellationToken cancellationToken, bool allowRefresh)
    {
        if (string.Equals(app.Source, "winget", StringComparison.OrdinalIgnoreCase))
            return await LoadWingetDetailsAsync(app, cancellationToken);
        int cacheGeneration = Volatile.Read(ref _cacheGeneration);
        if (string.IsNullOrWhiteSpace(app.CatalogDetailPath))
        {
            AppEntry? catalogEntry = _cachedCatalog.FirstOrDefault(entry =>
                string.Equals(entry.Id, app.Id, StringComparison.OrdinalIgnoreCase));
            if (catalogEntry is not null && !ReferenceEquals(catalogEntry, app))
                return await LoadDetailsCoreAsync(catalogEntry, cancellationToken, allowRefresh);
        }
        string? catalogHash = app.CatalogDetailCatalogSha256 ?? _catalogSha256;
        if (string.IsNullOrWhiteSpace(app.CatalogDetailPath) || string.IsNullOrWhiteSpace(catalogHash))
            return app;

        string relativePath = app.CatalogDetailPath.Replace('\\', '/');
        if (!IsSafeDetailPath(relativePath))
            return app;

        SemaphoreSlim detailLock = _detailLocks.GetOrAdd(app.Id, _ => new SemaphoreSlim(1, 1));
        bool hashMismatch = false;
        await detailLock.WaitAsync(cancellationToken);
        try
        {
            string detailRoot = Path.Combine(_catalogDetailsCacheDirectory, catalogHash);
            string detailFile = Path.GetFullPath(Path.Combine(detailRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            string normalizedRoot = Path.GetFullPath(detailRoot) + Path.DirectorySeparatorChar;
            if (!detailFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return app;

            string? json = null;
            string? expectedDetailHash = app.CatalogDetailSha256;
            if (File.Exists(detailFile))
            {
                string? cachedJson = null;
                try
                {
                    cachedJson = await File.ReadAllTextAsync(detailFile, cancellationToken);
                }
                catch (IOException exception)
                {
                    CoreLog.Warning(exception, "StoreService.ReadCachedDetails");
                }
                if (cachedJson is not null && HasExpectedHash(cachedJson, expectedDetailHash))
                    json = cachedJson;
            }

            if (cacheGeneration != Volatile.Read(ref _cacheGeneration))
                return app;

            if (json is null)
            {
                string detailBaseUrl = app.CatalogDetailBaseUrl
                    ?? (string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase) ? MsStoreCatalogBaseUrl : CatalogV2BaseUrl);
                string url = $"{detailBaseUrl}/{relativePath}";
                using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();
                json = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!HasExpectedHash(json, expectedDetailHash))
                {
                    hashMismatch = true;
                    throw new JsonException($"O hash do detalhe de {app.Id} não corresponde ao índice de busca.");
                }
                AppEntry? downloaded = WinProvision.Core.Services.AotJson.Deserialize<AppEntry>(json, _jsonOptions);
                if (downloaded is null || !string.Equals(downloaded.Id, app.Id, StringComparison.OrdinalIgnoreCase))
                    return app;
                if (cacheGeneration != Volatile.Read(ref _cacheGeneration))
                    return app;

                Directory.CreateDirectory(Path.GetDirectoryName(detailFile)!);
                await AtomicFile.WriteAllTextAsync(detailFile, json, cancellationToken);
                return CompleteDetails(downloaded, app);
            }

            AppEntry? cached = WinProvision.Core.Services.AotJson.Deserialize<AppEntry>(json, _jsonOptions);
            return cacheGeneration == Volatile.Read(ref _cacheGeneration)
                   && cached is not null && string.Equals(cached.Id, app.Id, StringComparison.OrdinalIgnoreCase)
                ? CompleteDetails(cached, app)
                : app;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WinProvision.Core.Services.CoreLog.Debug($"[StoreService] Não foi possível carregar detalhes de {app.Id}: {ex.Message}");
            if (!hashMismatch || !allowRefresh) return app;
        }
        finally
        {
            detailLock.Release();
        }
        // Release the per-app lock before refreshing and retrying the new generation.
        var catalog = await LoadCatalogAsync(forceRefresh: true, cancellationToken);
        var refreshed = catalog.FirstOrDefault(entry => entry.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)
            && entry.Source.Equals(app.Source, StringComparison.OrdinalIgnoreCase));
        return refreshed is not null && refreshed.CatalogDetailSha256 != app.CatalogDetailSha256
            ? await LoadDetailsCoreAsync(refreshed, cancellationToken, allowRefresh: false) : app;
    }

    private AppEntry CompleteDetails(AppEntry details, AppEntry summary)
    {
        details.IsInstalled = summary.IsInstalled;
        details.IsInstalling = summary.IsInstalling;
        details.IsSelectedForInstall = summary.IsSelectedForInstall;
        details.CatalogDetailPath = summary.CatalogDetailPath;
        details.CatalogDetailSha256 = summary.CatalogDetailSha256;
        details.CatalogDetailBaseUrl = summary.CatalogDetailBaseUrl;
        details.CatalogDetailCatalogSha256 = summary.CatalogDetailCatalogSha256;
        details.Media ??= summary.Media;
        details.IconUrl = _iconService.ResolveIconUrl(details);
        details.StoreBannerUrl = _iconService.ResolveBannerUrl(details);
        if (string.Equals(details.Source, "msstore", StringComparison.OrdinalIgnoreCase))
            details.StoreScreenshotUrls = _iconService.ResolveScreenshotUrls(details);
        RemoveLegacyScreenshotLinks([details]);
        return details;
    }

    private static bool IsSafeDetailPath(string path) =>
        path.StartsWith("apps/", StringComparison.Ordinal)
        && !Path.IsPathRooted(path)
        && !path.Split('/').Any(segment => segment is "" or "." or "..");

    private async Task<CatalogV2App> GetWingetAppAsync(AppEntry summary, CancellationToken token)
    {
        if (_wingetApps.TryGetValue(summary.Id, out var cached)
            && cached.Expires > DateTimeOffset.UtcNow
            && cached.App.Entry.CatalogDetailSha256 == summary.CatalogDetailSha256)
            return cached.App;
        CatalogV2App app = await _catalogV2Client.GetPackageAsync(summary, token).ConfigureAwait(false);
        _wingetApps[summary.Id] = (app, DateTimeOffset.UtcNow.AddMinutes(5));
        return app;
    }

    public async Task<string[]> GetAvailableVersionsAsync(AppEntry summary, CancellationToken token = default)
    {
        if (!string.Equals(summary.Source, "winget", StringComparison.OrdinalIgnoreCase)) return [];
        CatalogV2App app = await GetWingetAppAsync(summary, token);
        return app.Versions.Select(item => item.Number).ToArray();
    }

    public async Task<IReadOnlyList<CatalogInstaller>> GetVersionInstallersAsync(AppEntry summary,
        string? version, CancellationToken token = default)
    {
        CatalogV2App app = await GetWingetAppAsync(summary, token);
        string requestedVersion = string.IsNullOrWhiteSpace(version) ? app.Entry.Version : version;
        if (!app.Versions.Any(item => item.Number == requestedVersion && item.DetailPath is not null)) return [];
        PackageManifest detail = await _catalogV2Client.GetVersionDetailAsync(app.Entry, requestedVersion, token).ConfigureAwait(false);
        return detail.Installers;
    }

    private async Task<AppEntry> LoadWingetDetailsAsync(AppEntry summary, CancellationToken token)
    {
        CatalogV2App app = await GetWingetAppAsync(summary, token);
        AppEntry result = app.Entry;
        if (app.Versions.FirstOrDefault(version => version.Number == app.Entry.Version)?.DetailPath is not null)
        {
            PackageManifest detail = await _catalogV2Client.GetVersionDetailAsync(app.Entry, app.Entry.Version, token).ConfigureAwait(false);
            result.Installers = detail.Installers;
            result.ReleaseDate = detail.ReleaseDate ?? result.ReleaseDate;
        }
        return CompleteDetails(result, summary);
    }

    /// <summary>Momento em que o manifesto do catálogo v2 foi gravado localmente.</summary>
    public DateTime? LastCatalogSyncUtc => _loadedCatalogV2 && _loadedSearchIndexCacheFilePath is { } path && File.Exists(path)
        ? File.GetLastWriteTimeUtc(path) : null;

    public IEnumerable<AppEntry> Search(string query, int maxResults = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(query))
            return _cachedCatalog;

        if (maxResults <= 0)
            return [];
        int limit = maxResults == int.MaxValue ? 50000 : Math.Min(maxResults, 50000);
        IReadOnlyList<AppEntry> matches = _catalogV2Client.SearchAsync(query, limit).GetAwaiter().GetResult();
        var cached = _cachedCatalog.ToDictionary(app => app.Id, StringComparer.OrdinalIgnoreCase);
        return matches.Select(match => cached.TryGetValue(match.Id, out AppEntry? existing) ? existing : match).ToArray();
    }

    private static int ScoreMatch(SearchDocument document, string query, string cleanQuery)
    {
        int matchTier = PackageSearchRanking.MatchTierNormalized(document.CleanName, document.CleanId,
            document.CleanDescription, document.CleanTags, document.CleanPublisher, cleanQuery);
        if (matchTier < 5) return matchTier;

        if (document.Homepage.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            document.PublisherUrl.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            document.PackageUrl.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 10;

        return int.MaxValue;
    }

    private async Task<List<AppEntry>> FetchAndSaveRemoteCatalogAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false,
        bool revalidateRemote = false)
    {
        int generation = Volatile.Read(ref _cacheGeneration);
        try
        {
            CatalogState state = await _catalogV2Client.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _cacheGeneration)) return _cachedCatalog;
            IReadOnlyList<AppEntry> entries = await _catalogV2Client.SearchAsync(null, 50000, cancellationToken).ConfigureAwait(false);
            if (!forceRefresh && _cachedCatalog.Count == entries.Count
                && string.Equals(_loadedIndexSha256, state.CatalogSha256, StringComparison.Ordinal))
                return _cachedCatalog;
            var installedIds = _cachedCatalog.Where(app => app.IsInstalled)
                .Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<AppEntry> apps = entries.ToList();
            foreach (AppEntry app in apps) app.IsInstalled = installedIds.Contains(app.Id);
            PopulateIcons(apps);
            if (generation != Volatile.Read(ref _cacheGeneration)) return _cachedCatalog;
            _loadedCatalogV2 = true;
            _catalogSha256 = _loadedIndexSha256 = state.CatalogSha256;
            _loadedSearchIndexCacheFilePath = _catalogV2Client.DatabasePath;
            _wingetApps.Clear();
            SetCachedCatalog(apps);
            CatalogUpdated?.Invoke(_cachedCatalog);
            return _cachedCatalog;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "RefreshCatalogV2");
            return _cachedCatalog;
        }
    }

    private async Task<List<AppEntry>> FetchAndSaveLegacyRemoteCatalogAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false,
        bool revalidateRemote = false)
    {
        int generation = Volatile.Read(ref _cacheGeneration);
        try
        {
            Task<RemoteTextResult> manifestDownload = FetchTextWithCacheAsync(
                CatalogManifestUrl, _catalogManifestCacheFilePath, _catalogManifestEtagPath,
                _catalogManifestLastModifiedPath, forceRefresh || revalidateRemote, cancellationToken);
            RemoteTextResult manifestResult = await manifestDownload;
            if (manifestResult.Content is null)
                return _cachedCatalog;

            CatalogManifest? manifest = DeserializeCatalogJson<CatalogManifest>(manifestResult.Content, "manifest");
            if (!IsValidManifest(manifest))
                throw new JsonException("Manifesto remoto do catálogo v2 inválido.");

            Task<List<AppEntry>> msStoreCatalogTask = FetchMsStoreCatalogAsync(forceRefresh, cancellationToken);
            if (!forceRefresh && _cachedCatalog.Count > 0 &&
                string.Equals(_catalogSha256, manifest!.CatalogSha256, StringComparison.Ordinal) &&
                string.Equals(_loadedIndexSha256, manifest.IndexSha256, StringComparison.Ordinal))
            {
                var storeCatalog = await msStoreCatalogTask;
                var previousStore = _cachedCatalog.Where(app => app.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase)).ToArray();
                if (previousStore.Length == storeCatalog.Count &&
                    previousStore.Select(app => (app.Id, app.CatalogDetailCatalogSha256))
                        .ToHashSet().SetEquals(storeCatalog.Select(app => (app.Id, app.CatalogDetailCatalogSha256))))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (generation != Volatile.Read(ref _cacheGeneration)) return _cachedCatalog;
                    await CommitRemoteTextAsync(_catalogManifestCacheFilePath, manifestResult, cancellationToken);
                    return _cachedCatalog;
                }
            }
            string indexPath = GetSearchIndexCachePath(manifest!.CatalogSha256!);
            string indexUrl = $"{CatalogV2BaseUrl}/manifest/search-index.json";
            RemoteTextResult indexResult = await FetchTextWithCacheAsync(
                indexUrl, indexPath, indexPath + ".etag", indexPath + ".lastmodified", forceRefresh, cancellationToken);
            if (indexResult.Content is null)
                return _cachedCatalog;
            if (!string.IsNullOrWhiteSpace(manifest.IndexSha256)
                && !string.Equals(
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(indexResult.Content))).ToLowerInvariant(),
                    manifest.IndexSha256,
                    StringComparison.Ordinal))
                throw new JsonException("O hash do índice de busca não corresponde ao manifesto.");

            var catalog = DeserializeCatalogJson<List<AppEntry>>(indexResult.Content, "search-index") ?? [];
            if (catalog.Count == 0 || catalog.Count != manifest.AppCount || !ValidateSearchEntries(catalog))
                throw new JsonException("Índice remoto do catálogo v2 inválido ou incompleto.");

            SetCatalogRoute(catalog, CatalogV2BaseUrl, manifest.CatalogSha256!);
            var msStoreCatalog = await msStoreCatalogTask;
            catalog = MergeMsStoreCatalog(catalog, msStoreCatalog);

            RemoveLegacyScreenshotLinks(catalog);

            if (catalog.Count > 0)
            {
                // Preserva o flag IsInstalled ao trocar as instâncias de AppEntry.
                // Sem isso, buscas feitas após o refresh em background (que roda
                // fire-and-forget e substitui _cachedCatalog por objetos novos)
                // voltam sempre com IsInstalled = false, mesmo já sincronizado antes.
                var installedIds = _cachedCatalog
                    .Where(a => a.IsInstalled)
                    .Select(a => a.Id)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                foreach (var app in catalog)
                {
                    app.IsInstalled = installedIds.Contains(app.Id);
                }

                // Se o usuário limpou o cache enquanto a rede estava em andamento,
                // descarta o resultado antigo para não recriar o cache logo após a limpeza.
                if (generation != Volatile.Read(ref _cacheGeneration))
                    return _cachedCatalog;

                SetCachedCatalog(catalog);
                _catalogSha256 = manifest.CatalogSha256;
                _loadedIndexSha256 = manifest.IndexSha256;
                _loadedSearchIndexCacheFilePath = indexPath;
                PopulateIcons(_cachedCatalog);

                Directory.CreateDirectory(_cacheDirectory);
                await CommitRemoteTextAsync(_catalogManifestCacheFilePath, manifestResult, cancellationToken);
                await CommitRemoteTextAsync(indexPath, indexResult, cancellationToken);
                PruneSearchIndexCache(indexPath);
                PruneMsStoreIndexCache(msStoreCatalog);
                CatalogUpdated?.Invoke(_cachedCatalog);
            }

            return _cachedCatalog;
        }
        catch (Exception ex)
        {
            WinProvision.Core.Services.CoreLog.Debug($"[StoreService] Falha ao baixar catálogo remoto: {ex.Message}");
            return _cachedCatalog;
        }
    }

    private void PopulateIcons(List<AppEntry> catalog)
    {
        foreach (var app in catalog)
        {
            app.IconUrl = _iconService.ResolveIconUrl(app);
            app.StoreBannerUrl = _iconService.ResolveBannerUrl(app);
            if (string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase))
                app.StoreScreenshotUrls = _iconService.ResolveScreenshotUrls(app);
        }
    }

    public void ClearCache() => ClearCacheCore(false);
    public void ClearCacheStrict() => ClearCacheCore(true);
    private void ClearCacheCore(bool throwOnError)
    {
        Interlocked.Increment(ref _cacheGeneration);
        SetCachedCatalog([]);
        _catalogSha256 = null;
        _loadedIndexSha256 = null;
        _loadedSearchIndexCacheFilePath = null;
        _loadedCatalogV2 = false;
        _wingetApps.Clear();

        lock (_refreshSync)
        {
            _refreshCts?.Cancel();
            // Keep the task and source reachable until the refresh has actually stopped.
        }

        try
        {
            foreach (string path in Directory.Exists(_cacheDirectory)
                         ? Directory.EnumerateFiles(_cacheDirectory, "catalog-v2-search-*.json").ToArray()
                         : Array.Empty<string>())
                File.Delete(path);
            foreach (string path in Directory.Exists(_cacheDirectory)
                         ? Directory.EnumerateFiles(_cacheDirectory, "catalog-v2-search-*.json.*").ToArray()
                         : Array.Empty<string>())
                File.Delete(path);
            if (File.Exists(_catalogManifestCacheFilePath))
                File.Delete(_catalogManifestCacheFilePath);
            if (File.Exists(_screenshotIndexCacheFilePath))
                File.Delete(_screenshotIndexCacheFilePath);
            foreach (string path in Directory.Exists(_cacheDirectory)
                         ? Directory.EnumerateFiles(_cacheDirectory, "msstore-catalog-*.json*").ToArray()
                         : Array.Empty<string>())
                File.Delete(path);
            if (Directory.Exists(_catalogDetailsCacheDirectory))
                Directory.Delete(_catalogDetailsCacheDirectory, recursive: true);
            foreach (string metadataPath in new[]
                     {
                         _catalogManifestEtagPath, _catalogManifestLastModifiedPath,
                         _screenshotIndexEtagPath, _screenshotIndexLastModifiedPath
                     })
            {
                if (File.Exists(metadataPath))
                    File.Delete(metadataPath);
            }
        }
        catch (Exception ex)
        {
            WinProvision.Core.Services.CoreLog.Debug($"[StoreService] Não foi possível remover o cache do catálogo: {ex.Message}");
            if (throwOnError) throw;
        }

        CacheCleared?.Invoke();
    }

    private void SetCachedCatalog(List<AppEntry> catalog)
    {
        foreach (AppEntry app in catalog) app.HasCatalogScore = true;
        _cachedCatalog = catalog;
        _searchIndex = catalog.Select((app, index) =>
        {
            string name = app.Name ?? string.Empty;
            string id = app.Id ?? string.Empty;
            return new SearchDocument(
                index,
                name,
                id,
                app.Publisher ?? string.Empty,
                PackageSearchRanking.NormalizeText(name),
                PackageSearchRanking.NormalizeText(id),
                PackageSearchRanking.NormalizeText(app.Description),
                PackageSearchRanking.NormalizeText(app.Publisher),
                app.Tags.Select(PackageSearchRanking.NormalizeText).ToArray(),
                app.Description ?? string.Empty,
                app.Homepage ?? string.Empty,
                app.PublisherUrl ?? string.Empty,
                app.PackageUrl ?? string.Empty,
                app.Tags.ToArray());
        }).ToArray();
    }

    private string GetSearchIndexCachePath(string catalogSha256) =>
        Path.Combine(_cacheDirectory, $"catalog-v2-search-{catalogSha256}.json");

    private void PruneSearchIndexCache(string currentPath)
    {
        PruneVersionedCacheFiles("catalog-v2-search-*.json*", currentPath);
    }

    private void PruneMsStoreIndexCache(IReadOnlyList<AppEntry> currentCatalog)
    {
        string? catalogHash = currentCatalog.FirstOrDefault()?.CatalogDetailCatalogSha256;
        string? currentPath = catalogHash is null
            ? null
            : Path.Combine(_cacheDirectory, $"msstore-catalog-{catalogHash}.json");
        PruneVersionedCacheFiles("msstore-catalog-*.json*", currentPath);
    }

    private void PruneVersionedCacheFiles(string pattern, string? currentPath)
    {
        try
        {
            if (!Directory.Exists(_cacheDirectory))
                return;

            string? fullCurrentPath = currentPath is null ? null : Path.GetFullPath(currentPath);
            foreach (string path in Directory.EnumerateFiles(_cacheDirectory, pattern))
            {
                if (fullCurrentPath is not null)
                {
                    string fullPath = Path.GetFullPath(path);
                    if (string.Equals(fullPath, fullCurrentPath, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fullPath, fullCurrentPath + ".etag", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fullPath, fullCurrentPath + ".lastmodified", StringComparison.OrdinalIgnoreCase))
                        continue;
                }
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WinProvision.Core.Services.CoreLog.Debug($"[StoreService] Não foi possível remover índices de catálogo antigos: {ex.Message}");
        }
    }

    private static bool IsValidManifest(CatalogManifest? manifest) =>
        manifest is { SchemaVersion: 2, AppCount: > 0 }
        && manifest.CatalogSha256 is { Length: 64 } hash
        && hash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
        && manifest.IndexSha256 is { Length: 64 } indexHash
        && indexHash.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool HasExpectedHash(string json, string? expectedHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
            return false;
        string actualHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
        return string.Equals(actualHash, expectedHash, StringComparison.Ordinal);
    }

    private static bool ValidateSearchEntries(List<AppEntry> catalog)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (AppEntry app in catalog)
        {
            if (string.IsNullOrWhiteSpace(app.Id)
                || string.IsNullOrWhiteSpace(app.Name)
                || !ids.Add(app.Id)
                || !IsSafeDetailPath(app.CatalogDetailPath ?? string.Empty))
                return false;
        }
        return true;
    }

    internal sealed record SearchDocument(
        int CatalogIndex,
        string Name,
        string Id,
        string Publisher,
        string CleanName,
        string CleanId,
        string CleanDescription,
        string CleanPublisher,
        string[] CleanTags,
        string Description,
        string Homepage,
        string PublisherUrl,
        string PackageUrl,
        string[] Tags);

    private async Task<RemoteTextResult> FetchTextWithCacheAsync(
        string url,
        string cachePath,
        string etagPath,
        string lastModifiedPath,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        string? cachedContent = null;
        string? cachedEtag = null;
        string? cachedLastModified = null;
        try
        {
            if (File.Exists(cachePath))
                cachedContent = await File.ReadAllTextAsync(cachePath, cancellationToken);
            if (File.Exists(etagPath))
                cachedEtag = await File.ReadAllTextAsync(etagPath, cancellationToken);
            if (File.Exists(lastModifiedPath))
                cachedLastModified = await File.ReadAllTextAsync(lastModifiedPath, cancellationToken);

            if (!forceRefresh && cachedContent is not null && !IsFileStale(cachePath))
                return new RemoteTextResult(cachedContent, cachedEtag, cachedLastModified, ShouldCommit: false);

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(cachedEtag) && EntityTagHeaderValue.TryParse(cachedEtag, out var entityTag))
                request.Headers.IfNoneMatch.Add(entityTag);
            if (!string.IsNullOrWhiteSpace(cachedLastModified) &&
                DateTimeOffset.TryParse(cachedLastModified, out DateTimeOffset modifiedSince))
                request.Headers.IfModifiedSince = modifiedSince;

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == System.Net.HttpStatusCode.NotModified && cachedContent is not null)
            {
                return new RemoteTextResult(
                    cachedContent,
                    response.Headers.ETag?.ToString() ?? cachedEtag,
                    response.Content.Headers.LastModified?.ToString("R") ?? cachedLastModified,
                    ShouldCommit: true,
                    NotModified: true);
            }

            response.EnsureSuccessStatusCode();
            string content = await response.Content.ReadAsStringAsync(cancellationToken);
            return new RemoteTextResult(
                content,
                response.Headers.ETag?.ToString(),
                response.Content.Headers.LastModified?.ToString("R"),
                ShouldCommit: true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            WinProvision.Core.Services.CoreLog.Debug($"[StoreService] Recurso remoto indisponível; usando cache local ({Path.GetFileName(cachePath)}): {ex.Message}");
            return new RemoteTextResult(cachedContent, cachedEtag, cachedLastModified, ShouldCommit: false);
        }
    }

    private async Task CommitRemoteTextAsync(string cachePath, RemoteTextResult result, CancellationToken cancellationToken)
    {
        if (!result.ShouldCommit)
            return;

        if (result.NotModified)
        {
            if (File.Exists(cachePath))
                File.SetLastWriteTimeUtc(cachePath, DateTime.UtcNow);
        }
        else if (result.Content is not null)
        {
            await AtomicFile.WriteAllTextAsync(cachePath, result.Content, cancellationToken);
        }

        await WriteMetadataAsync(cachePath + ".etag", result.ETag, cancellationToken);
        await WriteMetadataAsync(cachePath + ".lastmodified", result.LastModified, cancellationToken);
    }

    private static async Task WriteMetadataAsync(string path, string? value, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { CoreLog.Warning(exception, "CatalogMetadataCleanup"); }
            return;
        }

        await AtomicFile.WriteAllTextAsync(path, value, cancellationToken);
    }

    private static bool IsFileStale(string path)
    {
        try
        {
            return !File.Exists(path) || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= CatalogCacheTtl;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(exception, "InspectCatalogCache");
            return true;
        }
    }

    internal sealed record RemoteTextResult(string? Content, string? ETag, string? LastModified, bool ShouldCommit, bool NotModified = false);

    internal sealed class CatalogManifest
    {
        public int SchemaVersion { get; set; }
        public int AppCount { get; set; }
        public string? CatalogSha256 { get; set; }
        public string? IndexSha256 { get; set; }
    }

    private List<AppEntry> TryLoadLocalMsStoreCatalog()
    {
        try
        {
            string? indexPath = Directory.Exists(_cacheDirectory)
                ? Directory.EnumerateFiles(_cacheDirectory, "msstore-catalog-*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (indexPath is null)
                return [];
            string hash = Path.GetFileNameWithoutExtension(indexPath).Replace("msstore-catalog-", "", StringComparison.Ordinal);
            var apps = DeserializeCatalogJson<List<AppEntry>>(File.ReadAllText(indexPath), "msstore-local-index") ?? [];
            if (apps.Count == 0 || !ValidateSearchEntries(apps))
                return [];
            SetCatalogRoute(apps, MsStoreCatalogBaseUrl, hash);
            return apps;
        }
        catch (Exception ex)
        {
            WinProvision.Core.Services.CoreLog.Debug($"[StoreService] Cache MS Store local inválido: {ex.Message}");
            return [];
        }
    }

    private async Task<List<AppEntry>> FetchMsStoreCatalogAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        try
        {
            using var manifestResponse = await _httpClient.GetAsync(MsStoreManifestUrl, cancellationToken);
            manifestResponse.EnsureSuccessStatusCode();
            string manifestJson = await manifestResponse.Content.ReadAsStringAsync(cancellationToken);
            var manifest = DeserializeCatalogJson<CatalogManifest>(manifestJson, "msstore-manifest");
            if (!IsValidManifest(manifest))
                throw new JsonException("Manifesto do subcatálogo MS Store inválido.");

            string indexPath = Path.Combine(_cacheDirectory, $"msstore-catalog-{manifest!.CatalogSha256}.json");
            string indexUrl = $"{MsStoreCatalogBaseUrl}/manifest/search-index.json";
            string indexJson;
            if (!forceRefresh && File.Exists(indexPath))
            {
                indexJson = await File.ReadAllTextAsync(indexPath, cancellationToken);
            }
            else
            {
                using var indexResponse = await _httpClient.GetAsync(indexUrl, cancellationToken);
                indexResponse.EnsureSuccessStatusCode();
                indexJson = await indexResponse.Content.ReadAsStringAsync(cancellationToken);
            }
            if (!HasExpectedHash(indexJson, manifest.IndexSha256))
            {
                using var indexResponse = await _httpClient.GetAsync(indexUrl, cancellationToken);
                indexResponse.EnsureSuccessStatusCode();
                indexJson = await indexResponse.Content.ReadAsStringAsync(cancellationToken);
                if (!HasExpectedHash(indexJson, manifest.IndexSha256))
                    throw new JsonException("Hash do índice do subcatálogo MS Store divergente.");
            }
            var apps = DeserializeCatalogJson<List<AppEntry>>(indexJson, "msstore-index") ?? [];
            if (apps.Count != manifest.AppCount || !ValidateSearchEntries(apps)
                || apps.Any(app => !string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)))
                throw new JsonException("Índice do subcatálogo MS Store incompleto ou inválido.");
            SetCatalogRoute(apps, MsStoreCatalogBaseUrl, manifest.CatalogSha256!);
            Directory.CreateDirectory(_cacheDirectory);
            await AtomicFile.WriteAllTextAsync(indexPath, indexJson, cancellationToken);
            return apps;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return TryLoadLocalMsStoreCatalog();
        }
        catch (Exception ex)
        {
            WinProvision.Core.Services.CoreLog.Debug($"[StoreService] Subcatálogo MS Store indisponível: {ex.Message}");
            return TryLoadLocalMsStoreCatalog();
        }
    }

    private static void SetCatalogRoute(IEnumerable<AppEntry> apps, string baseUrl, string catalogHash)
    {
        foreach (AppEntry app in apps)
        {
            app.CatalogDetailBaseUrl = baseUrl;
            app.CatalogDetailCatalogSha256 = catalogHash;
            bool isMicrosoftStore = string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase);
            app.StoreBannerUrl = isMicrosoftStore
                ? IconService.ResolveMicrosoftStoreAssetUrl(app.StoreBannerUrl)
                : IconService.ResolveCatalogMediaUrl(app, app.Media?.Banner, app.Media?.BannerSha256)
                    ?? CatalogEndpoints.ResolvePublicUrl(app.StoreBannerUrl);
            if (isMicrosoftStore)
            {
                app.StoreScreenshotUrls = (app.StoreScreenshotUrls ?? app.ScreenshotUrls ?? [])
                    .Select(IconService.ResolveMicrosoftStoreAssetUrl)
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .Cast<string>()
                    .ToList();
            }
        }
    }

    private static List<AppEntry> MergeMsStoreCatalog(List<AppEntry> wingetCatalog, List<AppEntry> msStoreCatalog)
    {
        if (msStoreCatalog.Count == 0)
            return wingetCatalog;
        var storeIds = msStoreCatalog.Select(app => app.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        wingetCatalog.RemoveAll(app => string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)
            || storeIds.Contains(app.Id));
        wingetCatalog.AddRange(msStoreCatalog);
        return wingetCatalog;
    }

    private static void RemoveLegacyScreenshotLinks(IEnumerable<AppEntry> catalog)
    {
        foreach (AppEntry app in catalog)
        {
            if (!string.Equals(app.Source, "msstore", StringComparison.OrdinalIgnoreCase)
                && app.ScreenshotUrls is { Count: > 0 } urls)
            {
                app.ScreenshotUrls = urls
                    .Where(url => !url.Contains(LegacyScreenshotBasePath, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (app.ScreenshotUrls.Count == 0)
                    app.ScreenshotUrls = null;
            }
        }
    }

    private bool IsCacheStale()
    {
        try
        {
            string? searchIndexPath = _loadedSearchIndexCacheFilePath;
            return searchIndexPath is null || !File.Exists(searchIndexPath)
                || DateTime.UtcNow - File.GetLastWriteTimeUtc(searchIndexPath) >= CatalogCacheTtl;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(exception, "InspectCatalogCache");
            return true;
        }
    }

    private Task<List<AppEntry>> RefreshCacheInBackgroundAsync(
        CancellationToken cancellationToken = default,
        bool forceRefresh = false,
        bool revalidateRemote = false)
    {
        lock (_refreshSync)
        {
            if (_refreshTask is { IsCompleted: false })
                return _refreshTask;

            ObjectDisposedException.ThrowIf(_disposed, this);
            var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _refreshCts = source;
            CancellationToken token = source.Token;
            Task<List<AppEntry>> task = FetchAndSaveRemoteCatalogAsync(token, forceRefresh, revalidateRemote);
            _refreshTask = task;

            _ = task.ContinueWith(completed =>

            {
                if (completed.Exception is { } exception)
                    CoreLog.Error(exception, "StoreService");
                lock (_refreshSync)
                {
                    if (ReferenceEquals(_refreshTask, task))
                    {
                        _refreshTask = null;
                        _refreshCts = null;
                    }
                }
                source.Dispose();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            return task;
        }
    }

    /// <summary>Revalida o cache ao abrir a interface, sem carregar a vitrine no modo sob demanda.</summary>
    public void BeginStartupCatalogRevalidation()
    {
        lock (_refreshSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _startupCatalogTask ??= Task.Run(async () =>
            {
                try { await _catalogV2Client.RefreshAsync(_startupCatalogCancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_startupCatalogCancellation.IsCancellationRequested) { }
                catch (Exception exception) { CoreLog.Warning(exception, "StartupReleaseCatalog"); }
            });
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (_refreshSync)
        {
            if (_disposed) return;
            _disposed = true;
            _refreshCts?.Cancel();
            _startupCatalogCancellation.Cancel();
            pending = _refreshTask;
        }
        try
        {
            if (pending is not null) await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            CoreLog.Warning(exception, "StoreService.DisposeCancelledRefresh");
        }
        catch (Exception exception) { CoreLog.Error(exception, "StoreService"); }
        finally
        {
            if (_startupCatalogTask is not null)
            {
                try { await _startupCatalogTask.ConfigureAwait(false); }
                catch (Exception exception) { CoreLog.Warning(exception, "DisposeStartupReleaseCatalog"); }
            }
            _catalogV2Client.Dispose();
            _startupCatalogCancellation.Dispose();
            if (_ownsHttpClient) _httpClient.Dispose();
        }
    }

}
