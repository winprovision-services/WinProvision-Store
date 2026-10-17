// WinProvisionApiService.cs
// Cliente de instalação própria: lê os instaladores dos mesmos detalhes publicados pelo catálogo V2.
//
// Formato observado:
//   Store/Catalog/manifest.json + Store/Catalog/manifest/search-index.json (WinGet)
//   Store/Catalog/msstore/manifest.json + .../search-index.json (Microsoft Store)
//   Cada detailPath aponta para o app.json que também contém "installers".
//
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

#region Modelos (batem 1:1 com o JSON publicado)

public sealed class PackageIndex
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("generatedAt")] public DateTimeOffset GeneratedAt { get; set; }
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("packages")] public List<PackageIndexEntry> Packages { get; set; } = [];
    [JsonPropertyName("sourceIndexHashes")] public Dictionary<string, string> SourceIndexHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PackageIndexEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "winget";
    [JsonPropertyName("architectures")] public List<string> Architectures { get; set; } = [];
    [JsonPropertyName("installerCount")] public int InstallerCount { get; set; }
    [JsonPropertyName("detailPath")] public string? ManifestPath { get; set; }
    [JsonPropertyName("recordSha256")] public string? ManifestSha256 { get; set; }
}

public sealed class CatalogIndexManifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; }
    [JsonPropertyName("generatedUtc")] public DateTimeOffset GeneratedUtc { get; set; }
    [JsonPropertyName("appCount")] public int AppCount { get; set; }
    [JsonPropertyName("indexSha256")] public string IndexSha256 { get; set; } = "";
}

public sealed record CatalogSourceHealth(
    string Source,
    bool Available,
    int? AppCount,
    DateTimeOffset? GeneratedUtc,
    TimeSpan? ResponseTime,
    string? Error);

public sealed record CatalogApiHealth(
    DateTimeOffset CheckedAt,
    IReadOnlyList<CatalogSourceHealth> Sources);

public sealed class PackageManifest
{
    [JsonPropertyName("schema")] public int Schema { get; set; }
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "winget";
    [JsonPropertyName("installers")] public List<CatalogInstaller> Installers { get; set; } = [];
}

/// <summary>Compatibilidade para consumidores antigos do modelo de instalador.</summary>
public sealed class PackageInstaller : CatalogInstaller { }

#endregion

public enum WinProvisionInstallOutcome
{
    Success,
    PackageNotFound,
    NoCompatibleInstaller,
    SilentInstallNotSupported,
    DownloadFailed,
    HashMismatch,
    ExtractionFailed,
    InstallProcessFailed,
    ElevationCanceled,
    InvalidManifest,
    RequiresWinget
}

public sealed record WinProvisionInstallResult(
    WinProvisionInstallOutcome Outcome,
    int? ExitCode = null,
    string? Message = null,
    bool InstallerStarted = false);

public sealed record WinProvisionInstallerCommand(string FileName, string Arguments);

public static class WinProvisionInstallerCommandBuilder
{
    public static WinProvisionInstallerCommand Build(string installerPath, string? installerType, string? silentArgs)
    {
        string type = installerType?.Trim().ToLowerInvariant() ?? string.Empty;
        bool isMsi = type is "msi" or "wixmsi"
                     || Path.GetExtension(installerPath).Equals(".msi", StringComparison.OrdinalIgnoreCase);
        string args = silentArgs?.Trim() ?? string.Empty;

        if (isMsi)
        {
            string msiexec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe");
            return new WinProvisionInstallerCommand(msiexec, $"/i \"{installerPath}\" {args}".TrimEnd());
        }

        return new WinProvisionInstallerCommand(installerPath, args);
    }
}

/// <summary>
/// Cliente da API própria da WinProvision Store. Serve como:
///  1) fallback direto quando o Winget COM API falha;
///  2) caminho primário quando a reelevação falha ou o processo já roda como SYSTEM
///     (cenários em que a COM do winget não é viável).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinProvisionApiService : IAsyncDisposable
{
    private readonly System.Threading.Lock _lifecycleGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _revalidationTask;
    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        Task? pending;
        lock (_lifecycleGate)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
            pending = _revalidationTask;
        }
        try
        {
            if (pending is not null) await pending.ConfigureAwait(false);
        }
        finally
        {
            if (_ownsHttpClient) _http.Dispose();
            _lifetime.Dispose();
        }
    }

    private const string CatalogBaseUrl = CatalogEndpoints.CatalogBaseUrl;
    private const string MsStoreCatalogBaseUrl = CatalogBaseUrl + "msstore/";

    private static readonly RequestRateLimitPolicy MetadataRateLimit = RequestRateLimitHandler.SharedPolicy;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly string _cacheDir;
    private readonly CatalogCacheService _catalogCache;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    private readonly SemaphoreSlim _manifestCacheLock = new(1, 1);
    private readonly SemaphoreSlim _installRecordsLock = new(1, 1);
    private readonly string _installRecordsPath;

    private PackageIndex? _indexCache;
    private bool _startupRevalidationScheduled;
    private IReadOnlyDictionary<string, PackageIndexEntry> _catalogEntriesById =
        new Dictionary<string, PackageIndexEntry>(StringComparer.OrdinalIgnoreCase);
    // O índice local é servido imediatamente; os manifestos remotos são revalidados
    // uma vez por sessão e os índices só baixam de novo quando o hash muda.
    private static readonly TimeSpan MaxStaleCacheAge = TimeSpan.FromDays(30);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(12);
    private const int MaxRequestAttempts = 4;
    private const long MaxInstallerBytes = 4L * 1024 * 1024 * 1024;
    private const long MaxInstallerCacheBytes = 2L * 1024 * 1024 * 1024;
    private static readonly TimeSpan InstallerCacheRetention = TimeSpan.FromDays(30);
    private static readonly SemaphoreSlim InstallerCacheGate = new(1, 1);

    private sealed class RetryableDownloadException(string message, HttpStatusCode statusCode, TimeSpan retryDelay)
        : HttpRequestException(message, null, statusCode)
    {
        public TimeSpan RetryDelay { get; } = retryDelay;
    }

    public WinProvisionApiService(HttpClient? http = null, string? cacheDir = null, CatalogCacheService? catalogCache = null)
    {
        _catalogCache = catalogCache ?? (cacheDir is null ? new CatalogCacheService() : new CatalogCacheService(Path.Combine(cacheDir, "shared"), new()));
        _ownsHttpClient = http is null;
        _http = http ?? new HttpClient(new RequestRateLimitHandler(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(25),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            MaxConnectionsPerServer = 8
        }))
        { Timeout = Timeout.InfiniteTimeSpan };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("WinProvisionStore/1.0");
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "ApiCache");
        _installRecordsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "ApiInstalledPackages.json");
        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>Detecta se o processo atual roda sob a conta SYSTEM (NT AUTHORITY\SYSTEM).</summary>
    public static bool IsRunningAsSystem()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.IsWellKnown(System.Security.Principal.WellKnownSidType.LocalSystemSid) == true;
    }

    public Task<PackageIndex> GetIndexAsync(bool forceRefresh = false, CancellationToken ct = default)
        => GetIndexCoreAsync(forceRefresh, forceRefresh, ct);

    internal Task<PackageIndex> RefreshFromSharedCacheAsync(CancellationToken ct)
        => GetIndexCoreAsync(true, false, ct);

    private async Task<PackageIndex> GetIndexCoreAsync(bool forceRefresh, bool forceRemote, CancellationToken ct)
    {
        await _catalogCache.MaintainAsync(false, ct).ConfigureAwait(false);
        await _indexLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh && _indexCache is not null)
            {
                ScheduleStartupRevalidation();
                return _indexCache;
            }

            if (!forceRefresh && _indexCache is null)
            {
                var diskCache = await ReadCacheFileAsync<PackageIndex>(IndexCachePath, ct);
                if (diskCache is not null)
                {
                    try
                    {
                        ValidateIndex(diskCache);
                        DateTimeOffset writtenAt = new(File.GetLastWriteTimeUtc(IndexCachePath));
                        TimeSpan age = DateTimeOffset.UtcNow - writtenAt;
                        if (age >= TimeSpan.Zero && age < MaxStaleCacheAge)
                        {
                            _indexCache = diskCache;
                            _catalogEntriesById = diskCache.Packages
                                .ToDictionary(entry => CatalogEntryKey(entry.Source, entry.Id), StringComparer.OrdinalIgnoreCase);
                            ScheduleStartupRevalidation();
                            return diskCache;
                        }
                    }
                    catch (InvalidDataException)
                    {
                        // Cache local corrompido: consulta a origem e o substitui se ela responder.
                    }
                }
            }

            try
            {
                var sourceIndexes = await Task.WhenAll(
                    LoadSourceIndexAsync("winget", CatalogBaseUrl, _indexCache, ct, forceRemote),
                    LoadSourceIndexAsync("msstore", MsStoreCatalogBaseUrl, _indexCache, ct, forceRemote));
                var combined = sourceIndexes.SelectMany(result => result.Entries).ToList();
                DateTimeOffset generatedAt = sourceIndexes.Max(result => result.Manifest.GeneratedUtc);

                var index = new PackageIndex
                {
                    Schema = 2,
                    GeneratedAt = generatedAt,
                    Count = combined.Count,
                    Packages = combined,
                    SourceIndexHashes = sourceIndexes.ToDictionary(
                        result => result.Source,
                        result => result.Manifest.IndexSha256,
                        StringComparer.OrdinalIgnoreCase)
                };
                ValidateIndex(index);
                _indexCache = index;
                _catalogEntriesById = index.Packages
                    .ToDictionary(entry => CatalogEntryKey(entry.Source, entry.Id), StringComparer.OrdinalIgnoreCase);
                await WriteCacheFileAsync(IndexCachePath, WinProvision.Core.Services.AotJson.Serialize(index), ct);
                _startupRevalidationScheduled = true;
                return index;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or InvalidDataException)
            {
                var cached = await ReadCacheFileAsync<PackageIndex>(IndexCachePath, ct);
                if (cached is not null && IsRecentCache(IndexCachePath))
                {
                    ValidateIndex(cached);
                    _indexCache = cached;
                    _catalogEntriesById = cached.Packages
                        .ToDictionary(entry => CatalogEntryKey(entry.Source, entry.Id), StringComparer.OrdinalIgnoreCase);
                    return cached;
                }
                throw new HttpRequestException("Não foi possível consultar a API e não há índice local recente disponível.", ex);
            }
        }
        finally
        {
            _indexLock.Release();
        }
    }

    /// <summary>Verifica cada catálogo remoto individualmente.</summary>
    public async Task<CatalogApiHealth> CheckCatalogHealthAsync(CancellationToken ct = default)
    {
        var sources = await Task.WhenAll(
            CheckSourceHealthAsync("WinGet", "winget", CatalogBaseUrl, ct),
            CheckSourceHealthAsync("Microsoft Store", "msstore", MsStoreCatalogBaseUrl, ct));
        return new CatalogApiHealth(DateTimeOffset.UtcNow, sources);
    }

    /// <summary>Remove os índices, detalhes e instaladores em cache local, sem tocar nos catálogos remotos.</summary>
    public async Task ClearLocalCacheAsync(CancellationToken ct = default)
    {
        await _indexLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _manifestCacheLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Directory.Exists(_cacheDir))
                    Directory.Delete(_cacheDir, recursive: true);
                Directory.CreateDirectory(_cacheDir);
                _indexCache = null;
                _catalogEntriesById = new Dictionary<string, PackageIndexEntry>(StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                _manifestCacheLock.Release();
            }
        }
        finally
        {
            _indexLock.Release();
        }

        await InstallerCacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string installerCacheDirectory = GetInstallerCacheDirectory();
            if (Directory.Exists(installerCacheDirectory))
                Directory.Delete(installerCacheDirectory, recursive: true);
        }
        finally
        {
            InstallerCacheGate.Release();
        }
    }

    private async Task<CatalogSourceHealth> CheckSourceHealthAsync(
        string displayName, string source, string baseUrl, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sourceTimeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            using var response = await _catalogCache.GetAsync(_http, baseUrl + "manifest.json", sourceTimeout.Token,
                forceRefresh: true, allowStale: false, maximumBytes: 256 * 1024).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var manifest = AotJson.Deserialize<CatalogIndexManifest>(await response.Content.ReadAsByteArrayAsync(sourceTimeout.Token).ConfigureAwait(false))
                ?? throw new InvalidDataException("Manifesto vazio.");
            ValidateSourceManifest(manifest, source);
            stopwatch.Stop();
            return new CatalogSourceHealth(displayName, true, manifest.AppCount, manifest.GeneratedUtc, stopwatch.Elapsed, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new CatalogSourceHealth(displayName, false, null, null, stopwatch.Elapsed, "A consulta excedeu 45 segundos.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or InvalidDataException)
        {
            stopwatch.Stop();
            string message = ex is HttpRequestException { StatusCode: { } status }
                ? $"Servidor respondeu {(int)status} ({status})."
                : ex is TaskCanceledException
                    ? "A consulta excedeu o tempo limite."
                    : "Não foi possível consultar ou validar o catálogo.";
            return new CatalogSourceHealth(displayName, false, null, null, stopwatch.Elapsed, message);
        }
    }

    private async Task<(string Source, CatalogIndexManifest Manifest, List<PackageIndexEntry> Entries)>
        LoadSourceIndexAsync(string source, string baseUrl, PackageIndex? cachedIndex, CancellationToken ct, bool forceRemote = false)
    {
        var manifest = await GetJsonWithRetryAsync<CatalogIndexManifest>(baseUrl + "manifest.json", ct, forceRemote: forceRemote)
            ?? throw new InvalidDataException($"Manifesto da origem {source} vazio.");
        ValidateSourceManifest(manifest, source);

        List<PackageIndexEntry>? cachedEntries = null;
        if (cachedIndex is not null
            && cachedIndex.SourceIndexHashes.TryGetValue(source, out string? cachedHash)
            && string.Equals(cachedHash, manifest.IndexSha256, StringComparison.OrdinalIgnoreCase))
        {
            cachedEntries = cachedIndex.Packages
                .Where(entry => string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (cachedEntries.Count == manifest.AppCount)
                return (source, manifest, cachedEntries);
        }

        var entries = await GetJsonWithRetryAsync<List<PackageIndexEntry>>(
            baseUrl + "manifest/search-index.json", ct, manifest.IndexSha256)
            ?? throw new InvalidDataException($"Índice da origem {source} vazio.");
        if (entries.Count != manifest.AppCount
            || entries.Any(entry => !string.Equals(entry.Source, source, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException($"Índice da origem {source} não corresponde ao manifesto.");
        return (source, manifest, entries);
    }

    private void ScheduleStartupRevalidation()
    {
        lock (_lifecycleGate)
        {
            if (_startupRevalidationScheduled || _disposed) return;
            if (File.Exists(IndexCachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(IndexCachePath) < _catalogCache.RevalidationInterval) return;
            _startupRevalidationScheduled = true;
            _revalidationTask = RevalidateIndexInBackgroundAsync();
        }
    }

    private async Task RevalidateIndexInBackgroundAsync()
    {
        try
        {
            await GetIndexCoreAsync(true, false, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { CoreLog.Warning(exception, "RevalidateCatalogIndex"); }
    }

    public async Task<PackageManifest?> GetPackageAsync(string id, CancellationToken ct = default, string? source = null)
    {
        try { return await GetPackageCoreAsync(id, ct, source).ConfigureAwait(false); }
        catch (Exception exception) when (HasIntegrityFailure(exception))
        {
            ct.ThrowIfCancellationRequested();
            await GetIndexAsync(forceRefresh: true, ct).ConfigureAwait(false);
            return await GetPackageCoreAsync(id, ct, source).ConfigureAwait(false);
        }
    }

    private static bool HasIntegrityFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is InvalidDataException) return true;
        return false;
    }

    private async Task<PackageManifest?> GetPackageCoreAsync(string id, CancellationToken ct, string? source)
    {
        string requestedSource = string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase) ? "msstore" : "winget";
        string cachePath = GetManifestCachePath(id, requestedSource);
        try
        {
            await GetIndexAsync(ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or InvalidDataException)
        {
            throw new HttpRequestException("Não foi possível validar o pacote contra o índice da API.", ex);
        }

        if (!_catalogEntriesById.TryGetValue(CatalogEntryKey(requestedSource, id), out var entry)
            && (source is not null || !_catalogEntriesById.TryGetValue(CatalogEntryKey("msstore", id), out entry)))
            return null;
        if (!IsValidManifestReference(entry))
            throw new InvalidDataException($"Referência do manifesto de '{id}' inválida no índice.");

        string sourceBaseUrl = string.Equals(entry.Source, "msstore", StringComparison.OrdinalIgnoreCase)
            ? MsStoreCatalogBaseUrl
            : CatalogBaseUrl;
        string manifestUrl = sourceBaseUrl + entry.ManifestPath;

        // O manifesto é imutável pelo hash publicado no índice. Se o cache local
        // corresponde ao hash e aos metadados da entrada atual, não precisa de round-trip.
        var cachedManifest = await ReadCacheFileAsync<PackageManifest>(cachePath, ct, allowExpired: true);
        if (cachedManifest is not null && IsManifestCacheValid(cachePath, entry))
        {
            try
            {
                ValidateManifest(cachedManifest, entry);
                return cachedManifest;
            }
            catch (InvalidDataException)
            {
                // Baixa novamente se os metadados não corresponderem ao índice atual.
            }
        }

        try
        {
            var manifest = await GetJsonWithRetryAsync<PackageManifest>(manifestUrl, ct, entry.ManifestSha256);
            if (manifest is not null)
            {
                ValidateManifest(manifest, entry);
                return manifest;
            }
            var cached = await ReadCacheFileAsync<PackageManifest>(cachePath, ct, allowExpired: true);
            return cached is not null && IsManifestCacheValid(cachePath, entry) && IsManifestForEntry(cached, entry) ? cached : null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            var cached = await ReadCacheFileAsync<PackageManifest>(cachePath, ct, allowExpired: true);
            return cached is not null && IsManifestCacheValid(cachePath, entry) && IsManifestForEntry(cached, entry) ? cached : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or InvalidDataException)
        {
            var cached = await ReadCacheFileAsync<PackageManifest>(cachePath, ct, allowExpired: true);
            if (cached is not null && IsManifestCacheValid(cachePath, entry) && IsManifestForEntry(cached, entry)) return cached;
            throw new HttpRequestException($"Falha ao consultar o manifesto de '{id}' e não há cache local válido.", ex);
        }
    }

    private static bool IsValidManifestReference(PackageIndexEntry entry)
    {
        if (!IsSafeCatalogPackageId(entry.Id) || string.IsNullOrWhiteSpace(entry.ManifestPath)
            || !IsValidSha256(entry.ManifestSha256)) return false;
        string path;
        try { path = Uri.UnescapeDataString(entry.ManifestPath); }
        catch (UriFormatException) { return false; }
        string[] segments = path.Split('/');
        return segments.Length >= 4
            && segments[0] == "apps"
            && segments[1] == GetCatalogPrefix(entry.Id, entry.Source)
            && segments[^1] == "app.json"
            && segments.Skip(2).Take(segments.Length - 3).All(segment => segment.Length > 0
                && segment is not ("." or "..")
                && segment.All(character => !char.IsControl(character) && !char.IsWhiteSpace(character)
                    && character is not ('/' or '\\')));
    }

    private static string GetCatalogPrefix(string id, string source) =>
        source == "msstore" && id[0] is >= '0' and <= '9' ? "0-9"
        : id[0] is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z' ? char.ToLowerInvariant(id[0]).ToString()
        : "_";

    private static string CatalogEntryKey(string source, string id) => $"{source}\0{id}";

    private static bool IsSafeCatalogPackageId(string id) =>
        !string.IsNullOrWhiteSpace(id)
        && id.Length <= 128
        && char.IsLetterOrDigit(id[0])
        && id.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character) && character is not ('/' or '\\'))
        && id.Split('.').All(part => part.Length > 0 && part is not ("." or ".."));

    private static void ValidateSourceManifest(CatalogIndexManifest manifest, string source)
    {
        if (manifest.SchemaVersion != 2
            || manifest.AppCount < 1 || manifest.GeneratedUtc == default
            || manifest.GeneratedUtc > DateTimeOffset.UtcNow.AddMinutes(10)
            || !IsValidSha256(manifest.IndexSha256))
            throw new InvalidDataException($"Manifesto da origem {source} inválido.");
    }

    private static void ValidateIndex(PackageIndex index)
    {
        if (index.Schema != 2 || index.Packages is null || index.Count != index.Packages.Count
            || index.GeneratedAt == default || index.GeneratedAt > DateTimeOffset.UtcNow.AddMinutes(10))
            throw new InvalidDataException("O índice da API tem estrutura ou data inválida.");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in index.Packages)
        {
            if (entry is null || !IsSafeCatalogPackageId(entry.Id)
                || (entry.Source is not ("winget" or "msstore"))
                || !ids.Add(CatalogEntryKey(entry.Source, entry.Id)))
                throw new InvalidDataException("O índice da API contém uma entrada inválida ou duplicada.");
            if (!IsValidManifestReference(entry))
                throw new InvalidDataException($"Referência do manifesto de '{entry.Id}' inválida.");
        }
    }

    private static bool IsManifestForEntry(PackageManifest manifest, PackageIndexEntry entry) =>
        string.Equals(manifest.Id, entry.Id, StringComparison.OrdinalIgnoreCase)
        && string.Equals(manifest.Version, entry.Version, StringComparison.Ordinal)
        && string.Equals(manifest.Source, entry.Source, StringComparison.OrdinalIgnoreCase);

    private static void ValidateManifest(PackageManifest manifest, PackageIndexEntry entry)
    {
        if (!IsManifestForEntry(manifest, entry) || manifest.Installers is null
            || manifest.Installers.Count != entry.InstallerCount)
            throw new InvalidDataException("O manifesto não corresponde à entrada validada do índice.");
    }

    private static bool IsManifestCacheValid(string path, PackageIndexEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.ManifestSha256)) return true;
        try
        {
            byte[] expected = Convert.FromHexString(entry.ManifestSha256);
            byte[] actual = SHA256.HashData(File.ReadAllBytes(path));
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (Exception ex) when (ex is IOException or FormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }
    private static bool IsRecentCache(string path) =>
        File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) <= MaxStaleCacheAge;
    private string IndexCachePath => Path.Combine(_cacheDir, "catalog-index.json");

    private string GetManifestCachePath(string id, string source)
    {
        string key = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"{source}\0{id.ToUpperInvariant()}")));
        return Path.Combine(_cacheDir, $"catalog-app-{key}.json");
    }

    private async Task<T?> GetJsonWithRetryAsync<T>(string url, CancellationToken ct, string? expectedSha256 = null, Func<byte[], CancellationToken, Task>? onPayloadReceived = null, bool forceRemote = false)
    {
        using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        operationTimeout.CancelAfter(RequestTimeout);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await _catalogCache.GetAsync(_http, url, operationTimeout.Token, expectedSha256, forceRemote,
                    maximumBytes: typeof(T) == typeof(CatalogIndexManifest) ? 256 * 1024 : CatalogCacheService.MaximumPayloadBytes).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return default;
                if (IsTransientStatus(response.StatusCode) && attempt < MaxRequestAttempts)
                {
                    var delay = GetRetryDelay(response, attempt);
                    response.Dispose();
                    await Task.Delay(delay, operationTimeout.Token).ConfigureAwait(false);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                byte[] payload = await response.Content.ReadAsByteArrayAsync(operationTimeout.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(expectedSha256))
                {
                    byte[] expected = Convert.FromHexString(expectedSha256);
                    byte[] actual = SHA256.HashData(payload);
                    if (!CryptographicOperations.FixedTimeEquals(expected, actual))
                        throw new InvalidDataException("O SHA-256 do manifesto não corresponde ao índice.");
                }
                T? value = WinProvision.Core.Services.AotJson.Deserialize<T>(payload);
                if (value is not null && onPayloadReceived is not null)
                    await onPayloadReceived(payload, operationTimeout.Token).ConfigureAwait(false);
                return value;
            }
            catch (OperationCanceledException) when (
                !ct.IsCancellationRequested && !operationTimeout.IsCancellationRequested && attempt < MaxRequestAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), operationTimeout.Token).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < MaxRequestAttempts
                && (ex.StatusCode is null || IsTransientStatus(ex.StatusCode.Value)))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), operationTimeout.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken ct, bool metadata = false)
    {
        Uri uri = new(url);
        if (metadata) await MetadataRateLimit.WaitAsync(uri, ct).ConfigureAwait(false);
        var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (metadata) MetadataRateLimit.Observe(uri, response);
        return response;
    }
    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)status is >= 500 and <= 599;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta) return TimeSpan.FromMilliseconds(Math.Clamp(delta.TotalMilliseconds, 250, 8000));
        if (retryAfter?.Date is { } date) return TimeSpan.FromMilliseconds(Math.Clamp((date - DateTimeOffset.UtcNow).TotalMilliseconds, 250, 8000));
        return TimeSpan.FromMilliseconds(Math.Min(5000, 350 * Math.Pow(2, attempt - 1)));
    }

    private async Task<T?> ReadCacheFileAsync<T>(string path, CancellationToken ct, bool allowExpired = false)
    {
        if (!allowExpired && !IsRecentCache(path)) return default;
        try
        {
            await using var stream = File.OpenRead(path);
            return await WinProvision.Core.Services.AotJson.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    private async Task WriteCacheBytesAsync(string path, byte[] contents, CancellationToken ct)
    {
        await _manifestCacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteAllBytesAsync(path, contents, ct).ConfigureAwait(false);
        }
        finally
        {
            _manifestCacheLock.Release();
        }
    }
    private async Task WriteCacheFileAsync(string path, string contents, CancellationToken ct)
    {
        await _manifestCacheLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteAllTextAsync(path, contents, ct).ConfigureAwait(false);
        }
        finally
        {
            _manifestCacheLock.Release();
        }
    }

    /// <summary>
    /// Escolhe o melhor installer para a arquitetura atual da máquina, com fallback pra x86
    /// (compatível via WOW64) quando não existe build nativa.
    /// </summary>
    public static CatalogInstaller? PickInstaller(
        PackageManifest manifest,
        Architecture? preferred = null,
        string? preferredScope = null)
    {
        var current = preferred ?? RuntimeInformation.OSArchitecture;
        var order = current switch
        {
            Architecture.Arm64 => new[] { "arm64", "x64", "x86" },
            Architecture.X64 => new[] { "x64", "x86" },
            Architecture.Arm => new[] { "arm", "x86" },
            _ => new[] { "x86" }
        };

        foreach (var arch in order)
        {
            // Dentro da mesma arquitetura, prefere um installer com silentSupported=true
            // (ex.: pacote que publica tanto um .exe quanto um .zip pra mesma arch) —
            // evita escolher por acaso um tipo que a API própria não sabe instalar sem
            // interação quando existe alternativa silenciosa disponível.
            var candidates = manifest.Installers
                .Where(i => string.Equals(i.Architecture, arch, StringComparison.OrdinalIgnoreCase))
                .ToList();
            candidates = PreferScope(candidates, preferredScope)
                .OrderByDescending(i => i.SilentSupported)
                .ToList();
            if (candidates.Count > 0) return candidates[0];
        }
        // Um instalador sem arquitetura declarada ainda pode ser selecionado. Não escolha
        // um binário marcado para uma arquitetura incompatível só por ser o único restante;
        // o fallback WinGet sabe tratar manifests especiais e emulation melhor.
        return PreferScope(manifest.Installers.Where(i => string.IsNullOrWhiteSpace(i.Architecture)
                || string.Equals(i.Architecture, "neutral", StringComparison.OrdinalIgnoreCase)), preferredScope)
            .OrderByDescending(i => i.SilentSupported)
            .FirstOrDefault();
    }

    private static IEnumerable<CatalogInstaller> PreferScope(
        IEnumerable<CatalogInstaller> installers,
        string? preferredScope)
    {
        var available = installers.ToList();
        string? target = preferredScope?.Trim().ToLowerInvariant() switch
        {
            "user" => "user",
            "machine" or "system" => "machine",
            _ => null
        };
        if (target is null)
            return available;

        var matching = available.Where(i => string.Equals(i.Scope, target, StringComparison.OrdinalIgnoreCase)).ToList();
        return matching.Count > 0 ? matching : available;
    }

    /// <summary>
    /// Baixa e instala silenciosamente. Ponto de entrada único a ser chamado
    /// pelas falhas reais da COM, pelo contexto de elevação ou pela ausência do pacote.
    /// </summary>
    public async Task<WinProvisionInstallResult> TryInstallAsync(
        string packageId,
        IProgress<string>? onLog = null,
        CancellationToken ct = default,
        string? preferredArchitecture = null,
        string? preferredScope = null,
        Action<InstallProgressUpdate>? onProgress = null,
        string? source = null,
        bool requiresElevation = false,
        bool elevationProhibited = false)
    {
        onLog?.Report($"[WinProvisionAPI] Consultando manifest de '{packageId}'...");
        PackageManifest? manifest;
        try
        {
            manifest = await GetPackageAsync(packageId, ct, source).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or TaskCanceledException)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.DownloadFailed,
                Message: $"Falha ao consultar o catálogo da API própria: {ex.Message}");
        }

        if (manifest is null)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.PackageNotFound,
                Message: $"Pacote '{packageId}' não encontrado na API própria.");

        if (!string.Equals(manifest.Id, packageId, StringComparison.OrdinalIgnoreCase))
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                Message: "O manifesto retornado não corresponde ao pacote.");

        if (string.Equals(manifest.Source, "msstore", StringComparison.OrdinalIgnoreCase))
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.RequiresWinget,
                Message: "O pacote pertence à Microsoft Store e será instalado pela fonte oficial msstore do WinGet.");

        if (manifest.Installers is null || manifest.Installers.Count == 0)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                Message: "O manifesto não possui instaladores.");

        var requestedArchitecture = preferredArchitecture?.Trim().ToLowerInvariant() switch
        {
            "x86" => Architecture.X86,
            "x64" => Architecture.X64,
            "arm64" => Architecture.Arm64,
            "arm" => Architecture.Arm,
            _ => (Architecture?)null
        };
        bool isSystem = IsRunningAsSystem();
        string? effectiveScope = isSystem ? "machine" : preferredScope;
        var installer = PickInstaller(manifest, requestedArchitecture, effectiveScope);
        if (installer is null)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.NoCompatibleInstaller,
                Message: $"Nenhum installer compatível para '{packageId}'.");

        if (isSystem && string.Equals(installer.Scope, "user", StringComparison.OrdinalIgnoreCase))
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.NoCompatibleInstaller,
                Message: $"'{packageId}' só possui instalador de escopo de usuário. O processo está como SYSTEM e não deve instalar no perfil LocalSystem.");

        if (!Uri.TryCreate(installer.Url, UriKind.Absolute, out var installerUri)
            || installerUri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(installerUri.UserInfo))
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                Message: "O manifesto não contém uma URL HTTPS válida para o instalador.");

        if (!IsValidSha256(installer.Sha256))
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                Message: "O manifesto não contém um SHA-256 válido; o instalador não será executado.");

        // O catálogo WinGet já marca silentSupported=false pra tipos que
        // não sabe instalar sem interação (msix, appx, portable, exe sem switch declarado
        // no manifesto, ou um zip cujo NestedInstallerType/NestedInstallerFiles não foi
        // resolvido). Baixar e tentar EXECUTAR esse arquivo sempre falha nesses casos —
        // melhor falhar aqui, ANTES do download, e deixar o nível 3 (winget.exe) cuidar
        // deles, já que o próprio winget sabe lidar com zip/portable/msix nativamente.
        if (!installer.SilentSupported && !installer.IsPortable)
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.SilentInstallNotSupported,
                Message: $"Installer tipo '{installer.Type}' de '{packageId}' não suporta instalação silenciosa pela API própria.");

        string extension = Path.GetExtension(installerUri.AbsolutePath);
        string workingDirectory = Path.Combine(Path.GetTempPath(), "WinProvision", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);
        var tempFile = Path.Combine(workingDirectory, SanitizeFileName(packageId) + extension);
        // Só usado quando o instalador vem dentro de um zip (extraído aqui, apagado no final).
        string? extractDir = null;
        bool installerStarted = false;

        try
        {
            string? cachedInstaller = await FindVerifiedInstallerCacheAsync(installer.Sha256, extension, ct)
                .ConfigureAwait(false);
            if (cachedInstaller is not null)
            {
                File.Copy(cachedInstaller, tempFile, overwrite: true);
                onLog?.Report("[WinProvisionAPI] Instalador reutilizado do cache local após validação SHA-256.");
                onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.OwnApi));
            }
            else
            {
                onLog?.Report($"[WinProvisionAPI] Baixando {installer.Url}...");
                await DownloadFileWithRetryAsync(installer.Url, tempFile, onLog, onProgress, ct);

                onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.OwnApi));
                onLog?.Report("[WinProvisionAPI] Verificando SHA-256...");
                if (!await VerifySha256Async(tempFile, installer.Sha256, ct))
                    return new WinProvisionInstallResult(WinProvisionInstallOutcome.HashMismatch,
                        Message: "Hash do instalador baixado não confere com o manifest.");

                await SaveVerifiedInstallerCacheAsync(tempFile, installer.Sha256, extension, onLog, ct)
                    .ConfigureAwait(false);
            }

            if (installer.IsPortable)
            {
                onLog?.Report("[WinProvisionAPI] Instalando aplicativo portátil e criando atalho na Área de Trabalho...");
                onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Installing, Method: WingetMethod.OwnApi));
                installerStarted = true;
                try
                {
                    var portableResult = await InstallPortablePackageAsync(tempFile, packageId, installer, ct).ConfigureAwait(false);
                    if (portableResult.Outcome == WinProvisionInstallOutcome.Success)
                        await RememberApiInstallAsync(packageId, isPortable: true, CancellationToken.None).ConfigureAwait(false);
                    return portableResult with { InstallerStarted = installerStarted };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return new WinProvisionInstallResult(WinProvisionInstallOutcome.InstallProcessFailed,
                        ex.HResult, $"Não foi possível instalar o aplicativo portátil: {ex.Message}", InstallerStarted: installerStarted);
                }
            }

            // O que de fato roda: o próprio arquivo baixado, ou (quando o pacote é um zip
            // com instalador aninhado) o instalador extraído de dentro dele.
            string runnablePath = tempFile;
            string effectiveType = installer.Type ?? string.Empty;

            if (installer.IsZipWithNestedInstaller)
            {
                onLog?.Report($"[WinProvisionAPI] Extraindo {installer.NestedInstallerFile} do pacote zip...");
                extractDir = Path.Combine(workingDirectory, "extracted");

                string? extractedPath;
                try
                {
                    extractedPath = ExtractNestedInstaller(tempFile, extractDir, installer.NestedInstallerFile!);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    return new WinProvisionInstallResult(WinProvisionInstallOutcome.ExtractionFailed, Message: ex.Message);
                }

                if (extractedPath is null)
                    return new WinProvisionInstallResult(WinProvisionInstallOutcome.ExtractionFailed,
                        Message: $"'{installer.NestedInstallerFile}' não encontrado dentro do zip de '{packageId}'.");

                runnablePath = extractedPath;
                effectiveType = installer.NestedType ?? string.Empty;
            }

            onLog?.Report($"[WinProvisionAPI] Instalando formato {effectiveType} (silent: {installer.SilentArgs})...");
            onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Installing, Method: WingetMethod.OwnApi));
            var exitCode = await RunInstallerAsync(runnablePath, effectiveType, installer.SilentArgs ?? string.Empty, installer.Scope ?? effectiveScope ?? string.Empty, ct,
                () => installerStarted = true, line => onLog?.Report(line),
                requiresElevation || installer.ElevationRequirement is "elevationRequired" or "elevatesSelf",
                elevationProhibited || installer.ElevationRequirement == "elevationProhibited");

            // Códigos padrão do Windows Installer/Burn mais os códigos de sucesso
            // adicionais declarados no manifesto WinGet.
            var ok = InstallerExitCodes.IsSuccess(exitCode) || installer.SuccessCodes.Contains(exitCode);
            if (ok)
                await RememberApiInstallAsync(packageId, isPortable: false, CancellationToken.None).ConfigureAwait(false);
            return ok
                ? new WinProvisionInstallResult(WinProvisionInstallOutcome.Success, exitCode)
                : new WinProvisionInstallResult(WinProvisionInstallOutcome.InstallProcessFailed, exitCode,
                    $"{InstallerExitCodes.Describe(exitCode)} Código {exitCode}.", InstallerStarted: installerStarted);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.DownloadFailed, Message: ex.Message,
                InstallerStarted: installerStarted);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.ElevationCanceled,
                ex.NativeErrorCode, "A solicitação de elevação foi cancelada.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InstallProcessFailed,
                ex.NativeErrorCode, $"Não foi possível iniciar o instalador: {ex.Message}", InstallerStarted: installerStarted);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new WinProvisionInstallResult(installerStarted
                    ? WinProvisionInstallOutcome.InstallProcessFailed
                    : WinProvisionInstallOutcome.DownloadFailed,
                Message: "A operação excedeu o tempo limite.", InstallerStarted: installerStarted);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.InstallProcessFailed,
                ex.HResult, $"Não foi possível executar o instalador: {ex.Message}", InstallerStarted: installerStarted);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
        }
    }

    /// <summary>
    /// Extrai só o instalador aninhado (não o zip inteiro) para uma pasta temporária
    /// dedicada. <paramref name="relativePath"/> vem tal como publicado no manifesto
    /// (winget-pkgs usa "\" como separador); comparação por sufixo de caminho normalizado
    /// tolera zips onde o entry vem prefixado por uma pasta-raiz extra.
    /// </summary>
    private static void ValidateArchive(ZipArchive archive)
    {
        if (archive.Entries.Count > 10_000)
            throw new InvalidDataException("O ZIP excede o limite de arquivos.");
        long remaining = MaxInstallerBytes;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            remaining -= entry.Length;
            if (remaining < 0)
                throw new InvalidDataException("O ZIP excede o limite de tamanho descompactado.");
            if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("O ZIP contém links não permitidos.");
        }
    }

    private static string? ExtractNestedInstaller(string zipPath, string extractDir, string relativePath)
    {
        string normalizedTarget = relativePath.Replace('\\', '/').TrimStart('/');

        using var archive = ZipFile.OpenRead(zipPath);
        ValidateArchive(archive);
        var entry = archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))
            ?? archive.Entries.FirstOrDefault(e =>
                e.FullName.Replace('\\', '/').EndsWith("/" + normalizedTarget, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            return null;

        Directory.CreateDirectory(extractDir);
        string destination = Path.Combine(extractDir, Path.GetFileName(entry.FullName));
        entry.ExtractToFile(destination, overwrite: true);
        return destination;
    }

    /// <summary>
    /// Remove um pacote previamente instalado por esta API. Primeiro executa o desinstalador
    /// normal (com retries e detecção de tecnologia); se falhar, usa o Force Uninstall. Os
    /// portáteis só são removidos da pasta gerenciada da WinProvision Store.
    /// </summary>
    public async Task<bool?> UninstallAsync(
        InstalledAppDetail app,
        UninstallerEngineService uninstaller,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        ApiInstallRecord? record = await FindApiInstallAsync(app.Id, ct).ConfigureAwait(false);
        if (record is null)
            return null; // Não foi instalado pelo método da API própria.

        onStatus?.Invoke("Tentando a desinstalação normal...");
        bool success;
        if (record.IsPortable)
        {
            // ZIP portátil não registra um desinstalador no Windows. A primeira etapa tenta
            // remover só os atalhos do app; a remoção da pasta gerenciada é a etapa agressiva.
            RemovePortableShortcuts(record.PackageId);
            success = TryRemoveManagedPortableDirectory(record.PackageId);
        }
        else
        {
            success = await uninstaller.RunSilentUninstallAsync(app, ct).ConfigureAwait(false);
            if (success)
            {
                onStatus?.Invoke("Limpando arquivos restantes...");
                await uninstaller.PerformAggressiveCleanupAsync(app, ct).ConfigureAwait(false);
            }
            else
            {
                ct.ThrowIfCancellationRequested();
                onStatus?.Invoke("A desinstalação normal falhou; tentando a remoção avançada...");
                success = await uninstaller.ForceUninstallAsync(app, ct).ConfigureAwait(false);
            }
        }

        if (success)
            await ForgetApiInstallAsync(record.PackageId, CancellationToken.None).ConfigureAwait(false);
        return success;
    }

    private async Task RememberApiInstallAsync(string packageId, bool isPortable, CancellationToken ct)
    {
        await _installRecordsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadInstallRecordsAsync(ct).ConfigureAwait(false);
            records.RemoveAll(record => record.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            records.Add(new ApiInstallRecord(packageId, isPortable));
            await WriteInstallRecordsAsync(records, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[WinProvisionAPI] Não foi possível salvar o registro da instalação: {ex.Message}");
        }
        finally
        {
            _installRecordsLock.Release();
        }
    }

    private async Task<ApiInstallRecord?> FindApiInstallAsync(string packageId, CancellationToken ct)
    {
        await _installRecordsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await ReadInstallRecordsAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(record => record.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[WinProvisionAPI] Não foi possível ler o registro de instalações: {ex.Message}");
            return null;
        }
        finally
        {
            _installRecordsLock.Release();
        }
    }

    private async Task ForgetApiInstallAsync(string packageId, CancellationToken ct)
    {
        await _installRecordsLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ReadInstallRecordsAsync(ct).ConfigureAwait(false);
            records.RemoveAll(record => record.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            await WriteInstallRecordsAsync(records, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[WinProvisionAPI] Não foi possível atualizar o registro da instalação: {ex.Message}");
        }
        finally
        {
            _installRecordsLock.Release();
        }
    }

    private async Task<List<ApiInstallRecord>> ReadInstallRecordsAsync(CancellationToken ct)
    {
        if (!File.Exists(_installRecordsPath))
            return [];
        await using var stream = File.OpenRead(_installRecordsPath);
        return await WinProvision.Core.Services.AotJson.DeserializeAsync<List<ApiInstallRecord>>(stream, cancellationToken: ct)
            .ConfigureAwait(false) ?? [];
    }

    private async Task WriteInstallRecordsAsync(List<ApiInstallRecord> records, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_installRecordsPath)!);
        await AtomicFile.WriteAllTextAsync(_installRecordsPath, WinProvision.Core.Services.AotJson.Serialize(records), ct).ConfigureAwait(false);
    }

    private static bool TryRemoveManagedPortableDirectory(string packageId)
    {
        string root = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "PortablePackages"));
        string directory = Path.GetFullPath(Path.Combine(root, SanitizeFileName(packageId)));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(directory))
            return !Directory.Exists(directory);

        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[WinProvisionAPI] Não foi possível remover o pacote portátil: {ex.Message}");
            return false;
        }
    }

    private static void RemovePortableShortcuts(string packageId)
    {
        string packageDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "PortablePackages", SanitizeFileName(packageId))) + Path.DirectorySeparatorChar;
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) return;

        foreach (string shortcutPath in Directory.EnumerateFiles(desktop, "*.lnk"))
        {
            try
            {
                using var shortcut = new ShellShortcut();
                string target = shortcut.ReadTarget(shortcutPath);
                if (!string.IsNullOrWhiteSpace(target) && Path.GetFullPath(target).StartsWith(packageDirectory, StringComparison.OrdinalIgnoreCase))
                    File.Delete(shortcutPath);
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Debug.WriteLine($"[WinProvisionAPI] Ignorando atalho portátil inválido: {ex.Message}");
            }
        }
    }

    internal sealed record ApiInstallRecord(string PackageId, bool IsPortable);

    private static async Task<WinProvisionInstallResult> InstallPortablePackageAsync(
        string downloadedPath,
        string packageId,
        CatalogInstaller installer,
        CancellationToken ct)
    {
        string portableRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "PortablePackages");
        Directory.CreateDirectory(portableRoot);

        string installName = SanitizeFileName(packageId);
        string destination = Path.Combine(portableRoot, installName);
        string staging = Path.Combine(portableRoot, $".{installName}.staging-{Guid.NewGuid():N}");
        string backup = Path.Combine(portableRoot, $".{installName}.backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        bool movedExisting = false;
        try
        {
            string relativeExe;
            if (string.Equals(installer.Type, "zip", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(installer.NestedInstallerFile))
                    return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                        Message: "O pacote portátil ZIP não declara o EXE em NestedInstallerFiles.");

                using (var archive = ZipFile.OpenRead(downloadedPath))
                {
                    ValidateArchive(archive);
                    archive.ExtractToDirectory(staging, overwriteFiles: false);
                }
                relativeExe = installer.NestedInstallerFile;
            }
            else
            {
                string extension = Path.GetExtension(downloadedPath);
                string fileName = SanitizeFileName(installer.PortableCommandAlias ?? packageId) + extension;
                File.Copy(downloadedPath, Path.Combine(staging, fileName), overwrite: true);
                relativeExe = fileName;
            }

            relativeExe = relativeExe.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
            string stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
            string executablePath = Path.GetFullPath(Path.Combine(staging, relativeExe));
            if (!executablePath.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(executablePath)
                || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
                return new WinProvisionInstallResult(WinProvisionInstallOutcome.InvalidManifest,
                    Message: "O executável portátil declarado não foi encontrado dentro do ZIP.");

            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(destination))
            {
                Directory.Move(destination, backup);
                movedExisting = true;
            }

            try
            {
                Directory.Move(staging, destination);
            }
            catch
            {
                if (movedExisting && !Directory.Exists(destination))
                    Directory.Move(backup, destination);
                throw;
            }

            string installedExe = Path.Combine(destination, relativeExe);
            string shortcutName = SanitizeFileName(installer.PortableCommandAlias
                ?? Path.GetFileNameWithoutExtension(installedExe));
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            Directory.CreateDirectory(desktop);
            try
            {
                CreateDesktopShortcut(installedExe, shortcutName);
            }
            catch
            {
                TryDeleteDirectory(destination);
                if (movedExisting && Directory.Exists(backup))
                {
                    Directory.Move(backup, destination);
                    movedExisting = false;
                }
                throw;
            }

            if (movedExisting) TryDeleteDirectory(backup);
            return new WinProvisionInstallResult(WinProvisionInstallOutcome.Success, 0,
                $"Aplicativo portátil instalado em '{destination}' e atalho criado na Área de Trabalho.");
        }
        finally
        {
            TryDeleteDirectory(staging);
            if (movedExisting && Directory.Exists(backup) && !Directory.Exists(destination))
                Directory.Move(backup, destination);
        }
    }

    private static void CreateDesktopShortcut(string targetPath, string shortcutName)
    {
        using var shortcut = new ShellShortcut();
        shortcut.Save(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), shortcutName + ".lnk"),
            targetPath, $"Atalho para {shortcutName}");
    }

    /// <summary>Baixa ou reutiliza um instalador verificado, sem executá-lo.</summary>
    public async Task DownloadVerifiedInstallerAsync(CatalogInstaller installer, string destination,
        Action<InstallProgressUpdate>? onProgress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(installer);
        if (!Uri.TryCreate(installer.Url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !IsValidSha256(installer.Sha256))
            throw new InvalidDataException("O instalador não possui URL HTTPS e SHA-256 válidos.");
        destination = Path.GetFullPath(destination);
        if (File.Exists(destination)) throw new IOException("O destino do instalador já existe.");
        string extension = Path.GetExtension(uri.AbsolutePath);
        string? cached = await FindVerifiedInstallerCacheAsync(installer.Sha256!, extension, ct).ConfigureAwait(false);
        try
        {
            if (cached is not null)
            {
                await using var input = new FileStream(cached, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous);
                await input.CopyToAsync(output, ct).ConfigureAwait(false);
            }
            else await DownloadFileWithRetryAsync(installer.Url, destination, null, onProgress, ct).ConfigureAwait(false);
            if (!await VerifySha256Async(destination, installer.Sha256!, ct).ConfigureAwait(false))
                throw new InvalidDataException("O SHA-256 do instalador difere do manifesto.");
            if (cached is null)
                await SaveVerifiedInstallerCacheAsync(destination, installer.Sha256!, extension, null, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteFile(destination);
            throw;
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destination,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken ct)
    {
        using var response = await SendAsync(url, ct).ConfigureAwait(false);
        if (response.RequestMessage?.RequestUri is not { Scheme: "https" } finalUri || !string.IsNullOrEmpty(finalUri.UserInfo))
            throw new InvalidDataException("O download foi redirecionado para uma URL insegura.");
        if (!response.IsSuccessStatusCode)
        {
            string message = $"O servidor recusou o download (HTTP {(int)response.StatusCode} {response.ReasonPhrase}).";
            if (IsTransientStatus(response.StatusCode))
                throw new RetryableDownloadException(message, response.StatusCode, GetRetryDelay(response, 1));
            throw new HttpRequestException(message, null, response.StatusCode);
        }

        long? expectedLength = response.Content.Headers.ContentLength;
        if (expectedLength is <= 0 || expectedLength > MaxInstallerBytes)
            throw new InvalidDataException($"Tamanho de download inválido ou acima do limite ({expectedLength} bytes).");

        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await AtomicFile.WriteAsync(destination, async (output, token) =>
        {
            bool canReportPercentage = expectedLength is > 0
                && !response.Content.Headers.ContentEncoding.Any();
            long bytesRead = 0;
            double lastPercent = -1;
            var buffer = new byte[128 * 1024];

            onProgress?.Invoke(new InstallProgressUpdate(
                InstallProgressPhase.Downloading,
                canReportPercentage ? 0 : null,
                WingetMethod.OwnApi));

            while (true)
            {
                int count = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (count == 0) break;

                bytesRead += count;
                if (bytesRead > MaxInstallerBytes)
                    throw new InvalidDataException($"O download excedeu o limite de {MaxInstallerBytes} bytes.");

                await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);

                if (canReportPercentage && expectedLength is > 0)
                {
                    double percent = Math.Clamp(
                        Math.Floor(bytesRead * 10_000d / expectedLength.Value) / 100d, 0, 99.99);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        onProgress?.Invoke(new InstallProgressUpdate(
                            InstallProgressPhase.Downloading, percent, WingetMethod.OwnApi));
                    }
                }
            }

            await output.FlushAsync(ct).ConfigureAwait(false);

            if (output.Length == 0 || output.Length > MaxInstallerBytes)
                throw new InvalidDataException($"O download terminou com tamanho inválido ({output.Length} bytes).");
            onProgress?.Invoke(new InstallProgressUpdate(
                InstallProgressPhase.Downloading, 100, WingetMethod.OwnApi));
        }, ct).ConfigureAwait(false);
        // O Content-Length pode descrever a representação comprimida/intermediária do CDN.
        // A integridade real do arquivo é confirmada pelo SHA-256 do manifesto.
    }

    private async Task DownloadFileWithRetryAsync(
        string url,
        string destination,
        IProgress<string>? onLog,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken ct)
    {
        // Um único prazo cobre todas as tentativas e os intervalos entre elas.
        // Assim, três falhas não multiplicam o limite de 12 minutos por tentativa.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await DownloadFileAsync(url, destination, onProgress, timeout.Token);
                return;
            }
            catch (Exception ex) when (attempt < 4
                && (ex is IOException
                    || ex is RetryableDownloadException
                    || ex is HttpRequestException { StatusCode: null }
                    || ex is OperationCanceledException && !ct.IsCancellationRequested)
                && !ct.IsCancellationRequested
                && !timeout.IsCancellationRequested)
            {
                TryDeleteFile(destination);
                onLog?.Report($"Falha temporária no download; nova tentativa {attempt + 1} de 4...");
                TimeSpan delay = ex is RetryableDownloadException retryable
                    ? retryable.RetryDelay
                    : TimeSpan.FromMilliseconds(700 * Math.Pow(2, attempt - 1));
                await Task.Delay(delay, timeout.Token);
            }
        }
    }

    internal static async Task MaintainInstallerCacheAsync(CancellationToken token)
    {
        await InstallerCacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string directory = GetInstallerCacheDirectory();
            if (!Directory.Exists(directory)) return;
            var files = new DirectoryInfo(directory).EnumerateFiles().Where(file =>
                (file.Attributes & FileAttributes.ReparsePoint) == 0
                && Path.GetFileNameWithoutExtension(file.Name) is { Length: 64 } name && name.All(char.IsAsciiHexDigit))
                .OrderBy(file => file.LastWriteTimeUtc).ToList();
            long total = files.Sum(file => file.Length);
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                // Recently verified/copying payloads belong to active operations.
                if (DateTime.UtcNow - file.LastWriteTimeUtc < TimeSpan.FromMinutes(10)) continue;
                if (DateTime.UtcNow - file.LastWriteTimeUtc <= InstallerCacheRetention && total <= MaxInstallerCacheBytes) continue;
                try { long bytes = file.Length; file.Delete(); total -= bytes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLog.Warning(ex, "InstallerCache.MaintainFile"); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLog.Warning(ex, "InstallerCache.Maintain"); }
        finally { InstallerCacheGate.Release(); }
    }

    private static string GetInstallerCacheDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvision", "InstallerCache");

    private static string GetInstallerCachePath(string sha256, string extension) => Path.Combine(
        GetInstallerCacheDirectory(), sha256.ToLowerInvariant() + extension.ToLowerInvariant());

    private static async Task<string?> FindVerifiedInstallerCacheAsync(
        string sha256, string extension, CancellationToken ct)
    {
        string path = GetInstallerCachePath(sha256, extension);
        await InstallerCacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path)) return null;
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > InstallerCacheRetention
                || !await VerifySha256Async(path, sha256, ct).ConfigureAwait(false))
            {
                TryDeleteFile(path);
                return null;
            }

            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            return path;
        }
        catch (IOException)
        {
            TryDeleteFile(path);
            return null;
        }
        finally
        {
            InstallerCacheGate.Release();
        }
    }

    private static async Task SaveVerifiedInstallerCacheAsync(
        string sourcePath, string sha256, string extension, IProgress<string>? onLog, CancellationToken ct)
    {
        var fileInfo = new FileInfo(sourcePath);
        if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > MaxInstallerCacheBytes)
        {
            onLog?.Report("[WinProvisionAPI] Instalador fora do limite do cache; será removido ao fim desta instalação.");
            return;
        }

        string directory = GetInstallerCacheDirectory();
        string destination = GetInstallerCachePath(sha256, extension);
        await InstallerCacheGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            await AtomicFile.WriteAsync(destination, async (output, token) =>
            {
                await using var input = File.OpenRead(sourcePath);
                await input.CopyToAsync(output, token).ConfigureAwait(false);
            }, ct).ConfigureAwait(false);
            File.SetLastWriteTimeUtc(destination, DateTime.UtcNow);

            var files = new DirectoryInfo(directory).EnumerateFiles()
                .Where(file => !file.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(file => file.LastWriteTimeUtc)
                .ToList();
            long totalBytes = files.Sum(file => file.Length);
            DateTime expiry = DateTime.UtcNow - InstallerCacheRetention;
            foreach (var cachedFile in files)
            {
                ct.ThrowIfCancellationRequested();
                if (cachedFile.FullName.Equals(destination, StringComparison.OrdinalIgnoreCase)) continue;
                if (cachedFile.LastWriteTimeUtc >= expiry && totalBytes <= MaxInstallerCacheBytes) continue;
                try
                {
                    long length = cachedFile.Length;
                    cachedFile.Delete();
                    totalBytes -= length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onLog?.Report($"[WinProvisionAPI] Cache do instalador indisponível; seguindo com a instalação: {ex.Message}");
        }
        finally
        {
            InstallerCacheGate.Release();
        }
    }

    private static async Task<bool> VerifySha256Async(string filePath, string expectedHex, CancellationToken ct)
    {
        await using var stream = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(stream, ct);
        var actualHex = Convert.ToHexString(hash);
        return string.Equals(actualHex, expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<int> RunInstallerAsync(
        string filePath, string installerType, string silentArgs, string scope, CancellationToken ct,
        Action? onStarted = null, Action<string>? onLog = null,
        bool requiresElevation = false, bool elevationProhibited = false)
    {
        ct.ThrowIfCancellationRequested();
        bool needsElevation = InstallerElevationPolicy.RequiresElevation(scope, requiresElevation, elevationProhibited);
        bool elevate = needsElevation && !IsRunningElevated();
        var command = WinProvisionInstallerCommandBuilder.Build(filePath, installerType, silentArgs);
        if (elevate)
        {
            onLog?.Invoke("A elevação deste instalador será feita pelo WinProvision Elevator.");
            return await GsudoElevationService.RunElevatedAsync(command.FileName, command.Arguments, ct, onStarted, onLog).ConfigureAwait(false);
        }
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = command.FileName,
            Arguments = command.Arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Não foi possível iniciar o instalador.");
        onStarted?.Invoke();
        // Do not dispose the process or delete its files while an installer is still active.
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    private static bool IsRunningElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static bool IsValidSha256([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        safe = safe.Trim().TrimEnd('.');
        if (safe.Length > 72) safe = safe[..72];
        return string.IsNullOrWhiteSpace(safe) ? "WinProvisionPackage" : safe;
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { CoreLog.Warning(exception, "InstallerTemporaryFileCleanup"); }
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (path is null) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { CoreLog.Warning(exception, "InstallerTemporaryFileCleanup"); }
    }
}
