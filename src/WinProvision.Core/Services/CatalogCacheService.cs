using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinProvision.Core.Services;

public sealed record CatalogCacheEntry(string? Sha256, DateTimeOffset ValidatedUtc,
    string? ETag = null, DateTimeOffset? LastModified = null, DateTimeOffset? RetryAfterUtc = null,
    string? FailedExpectedSha256 = null, int? FailureStatus = null);
public sealed record CatalogCacheUsage(long Bytes, int Files, DateTimeOffset? LastCleanupUtc);

/// <summary>Shared raw catalog payloads for UI and CLI. Mutable control files have a persisted TTL;
/// details/indexes are addressed by the SHA-256 supplied by their validated manifest.</summary>
public sealed class CatalogCacheService
{
    public const int MaximumPayloadBytes = 64 * 1024 * 1024;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);
    private readonly CatalogCacheSettings _settings;
    private readonly TimeProvider _clock;
    private readonly string? _sharedRoot;
    private readonly bool _maintainInstallers;
    private long _nextMaintenanceTicks;
    public string LocalRoot { get; }
    public string? SharedRoot => _sharedRoot;
    public TimeSpan RevalidationInterval => TimeSpan.FromHours(_settings.RevalidationHours);

    public CatalogCacheService(string? localRoot = null, CatalogCacheSettings? settings = null, TimeProvider? clock = null)
    {
        _settings = settings ?? CatalogCacheSettingsStore.Load();
        _settings.Validate();
        LocalRoot = Path.GetFullPath(localRoot ?? CatalogCacheSettings.LocalDirectory);
        _sharedRoot = _settings.NetworkCacheDirectory is { } shared ? Path.GetFullPath(shared) : null;
        if (string.Equals(LocalRoot, _sharedRoot, StringComparison.OrdinalIgnoreCase)) _sharedRoot = null;
        _clock = clock ?? TimeProvider.System;
        _maintainInstallers = localRoot is null;
    }

    private DateTimeOffset Now => _clock.GetUtcNow();
    private static string Key(Uri uri) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))).ToLowerInvariant();
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static string Blob(string root, string digest) => Path.Combine(root, "objects", digest.ToLowerInvariant() + ".bin");
    private static string EntryPath(string root, string key) => Path.Combine(root, "entries", key + ".json");
    private static bool Matches(byte[] bytes, string digest) => IsHash(digest)
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(digest));

    private async Task<(CatalogCacheEntry? Entry, byte[]? Bytes)> ReadAsync(string root, string key, string? expected, CancellationToken token)
    {
        try
        {
            CatalogCacheEntry? entry = null;
            string metadata = EntryPath(root, key);
            if (File.Exists(metadata))
                entry = AotJson.Deserialize<CatalogCacheEntry>(await File.ReadAllTextAsync(metadata, token).ConfigureAwait(false));
            string? digest = expected ?? entry?.Sha256;
            if (!IsHash(digest)) return (entry, null);
            string path = Blob(root, digest!);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumPayloadBytes) return (entry, null);
            byte[] bytes = await ReadBoundedFileAsync(path, token).ConfigureAwait(false);
            if (!Matches(bytes, digest!)) return (entry, null);
            if (expected is not null && !string.Equals(entry?.Sha256, expected, StringComparison.OrdinalIgnoreCase))
                entry = new(expected, Now);
            if (Now.UtcDateTime - info.LastWriteTimeUtc > TimeSpan.FromHours(1))
            {
                try { File.SetLastWriteTimeUtc(path, Now.UtcDateTime); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { CoreLog.Warning(ex, "CatalogCache.Touch"); }
            }
            return (entry, bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        { CoreLog.Warning(ex, "CatalogCache.Read"); return (null, null); }
    }

    private bool Fresh(CatalogCacheEntry? entry) => entry is not null && Now >= entry.ValidatedUtc
        && Now - entry.ValidatedUtc < RevalidationInterval;
    private bool CoolingDown(CatalogCacheEntry? entry, string? expected) => entry?.RetryAfterUtc > Now
        && entry.RetryAfterUtc <= Now + RetryDelay
        && string.Equals(entry.FailedExpectedSha256, expected, StringComparison.OrdinalIgnoreCase);
    private bool UsableStale(CatalogCacheEntry? entry, byte[]? bytes) => bytes is not null && entry is not null
        && Now >= entry.ValidatedUtc && Now - entry.ValidatedUtc <= Retention;
    private static HttpResponseMessage Response(byte[] bytes, CatalogCacheEntry? entry)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        if (EntityTagHeaderValue.TryParse(entry?.ETag, out var etag)) response.Headers.ETag = etag;
        response.Content.Headers.LastModified = entry?.LastModified;
        return response;
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, CancellationToken token)
    {
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous);
        using var output = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (output.Length + read > MaximumPayloadBytes) throw new InvalidDataException("Arquivo de cache acima do limite.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private async Task TrimUnderLeaseAsync(string root, CancellationToken token)
    {
        var files = ManagedFiles(root).OrderBy(f => f.LastWriteTimeUtc).ToList();
        long total = files.Sum(f => f.Length);
        long limit = _settings.MaximumMegabytes * 1024L * 1024;
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            TimeSpan retention = file.Extension == ".tmp" ? TimeSpan.FromDays(1) : Retention;
            if (Now.UtcDateTime - file.LastWriteTimeUtc <= retention && total <= limit) continue;
            try { long length = file.Length; file.Delete(); total -= length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLog.Warning(ex, "CatalogCache.TrimFile"); }
        }
        await AtomicFile.WriteAllTextAsync(Path.Combine(root, "size.txt"), total.ToString(System.Globalization.CultureInfo.InvariantCulture), token).ConfigureAwait(false);
    }

    private async Task SaveAsync(string root, string key, CatalogCacheEntry entry, byte[]? bytes, CancellationToken token)
    {
        long delta = 0;
        string metadata = EntryPath(root, key);
        if (bytes is not null)
        {
            string path = Blob(root, entry.Sha256!);
            var previous = new FileInfo(path);
            long previousLength = previous.Exists ? previous.Length : 0;
            if (!previous.Exists || previousLength > MaximumPayloadBytes || !Matches(await ReadBoundedFileAsync(path, token).ConfigureAwait(false), entry.Sha256!))
            {
                await AtomicFile.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
                delta += bytes.Length - previousLength;
            }
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime);
        }
        byte[] json = AotJson.SerializeToUtf8Bytes(entry);
        delta += json.Length - (File.Exists(metadata) ? new FileInfo(metadata).Length : 0);
        await AtomicFile.WriteAllBytesAsync(metadata, json, token).ConfigureAwait(false);
        string sizePath = Path.Combine(root, "size.txt");
        long size = File.Exists(sizePath) && long.TryParse(await File.ReadAllTextAsync(sizePath, token).ConfigureAwait(false), out var stored)
            ? Math.Max(0, stored + delta) : ManagedFiles(root).Sum(f => f.Length);
        if (size > _settings.MaximumMegabytes * 1024L * 1024)
            await TrimUnderLeaseAsync(root, token).ConfigureAwait(false);
        else await AtomicFile.WriteAllTextAsync(sizePath, size.ToString(System.Globalization.CultureInfo.InvariantCulture), token).ConfigureAwait(false);
    }

    private async Task MirrorAsync(string key, CatalogCacheEntry entry, byte[]? bytes, CancellationToken token)
    {
        try
        {
            await using var lease = await LockAsync(LocalRoot, token).ConfigureAwait(false);
            await SaveAsync(LocalRoot, key, entry, bytes, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLog.Warning(ex, "CatalogCache.Mirror"); }
    }

    private static async Task<FileStream> LockAsync(string root, CancellationToken token)
    {
        Directory.CreateDirectory(root);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("A raiz do cache não pode ser um redirecionamento de pasta.");
        long started = Environment.TickCount64;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(root, "access.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (Environment.TickCount64 - started < 5000)
            { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }

    public async Task<HttpResponseMessage> GetAsync(HttpClient client, string url, CancellationToken token,
        string? expectedSha256 = null, bool forceRefresh = false, bool allowStale = true, int maximumBytes = MaximumPayloadBytes)
    {
        if (maximumBytes is < 1 or > MaximumPayloadBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var uri = new Uri(url);
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)
            throw new ArgumentException("O cache de catálogo exige uma URL HTTPS sem credenciais.");
        if (expectedSha256 is not null && !IsHash(expectedSha256)) throw new InvalidDataException("SHA-256 inválido.");
        token.ThrowIfCancellationRequested();
        await MaintainAsync(false, token).ConfigureAwait(false);
        string key = Key(uri);
        var local = await ReadAsync(LocalRoot, key, expectedSha256, token).ConfigureAwait(false);
        if (local.Bytes?.Length > maximumBytes) local = (null, null);
        if (!forceRefresh && local.Bytes is not null && (expectedSha256 is not null || Fresh(local.Entry)))
        {
            if (local.Bytes.Length > maximumBytes) throw new InvalidDataException("Cache acima do limite da consulta.");
            return Response(local.Bytes, local.Entry);
        }
        if (!forceRefresh && CoolingDown(local.Entry, expectedSha256))
        {
            if (local.Entry?.FailureStatus is int status) return new((HttpStatusCode)status);
            if (allowStale && UsableStale(local.Entry, local.Bytes)) return Response(local.Bytes!, local.Entry);
            throw new IOException("A origem está temporariamente indisponível; a próxima tentativa respeitará o prazo do cache.");
        }
        string sharedRetry = Path.Combine(LocalRoot, "shared-retry.txt");
        bool sharedCoolingDown = File.Exists(sharedRetry)
            && DateTimeOffset.TryParse(await File.ReadAllTextAsync(sharedRetry, token).ConfigureAwait(false), out var retry)
            && retry > Now && retry <= Now + RetryDelay;
        string root = !sharedCoolingDown && _sharedRoot is not null ? _sharedRoot : LocalRoot;
        FileStream lease;
        try { lease = await LockAsync(root, token).ConfigureAwait(false); }
        catch (Exception ex) when (_sharedRoot is not null && ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(ex, "CatalogCache.SharedUnavailable");
            root = LocalRoot;
            await AtomicFile.WriteAllTextAsync(sharedRetry, (Now + RetryDelay).ToString("O"), token).ConfigureAwait(false);
            lease = await LockAsync(root, token).ConfigureAwait(false);
        }
        await using (lease)
        {
            var active = await ReadAsync(root, key, expectedSha256, token).ConfigureAwait(false);
            if (active.Bytes?.Length > maximumBytes) active = (null, null);
            if (!forceRefresh && active.Bytes is not null && (expectedSha256 is not null || Fresh(active.Entry)))
            {
                if (root != LocalRoot) await MirrorAsync(key, active.Entry ?? new(expectedSha256, Now), active.Bytes, token).ConfigureAwait(false);
                if (active.Bytes.Length > maximumBytes) throw new InvalidDataException("Cache acima do limite da consulta.");
                return Response(active.Bytes, active.Entry);
            }
            if (active.Bytes is null && local.Bytes is not null) active = local;
            if (!forceRefresh && CoolingDown(active.Entry, expectedSha256))
            {
                if (active.Entry?.FailureStatus is int status) return new((HttpStatusCode)status);
                if (allowStale && UsableStale(active.Entry, active.Bytes)) return Response(active.Bytes!, active.Entry);
                throw new IOException("A origem está em espera após uma falha recente.");
            }
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                if (active.Bytes is not null && EntityTagHeaderValue.TryParse(active.Entry?.ETag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
                if (active.Bytes is not null) request.Headers.IfModifiedSince = active.Entry?.LastModified;
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified && active.Bytes is not null && active.Entry is not null)
                {
                    response.Dispose();
                    var renewed = active.Entry with { ValidatedUtc = Now, RetryAfterUtc = null, FailedExpectedSha256 = null, FailureStatus = null };
                    await SaveAsync(root, key, renewed, active.Bytes, token).ConfigureAwait(false);
                    if (root != LocalRoot) await MirrorAsync(key, renewed, active.Bytes, token).ConfigureAwait(false);
                    return Response(active.Bytes, renewed);
                }
                if (!response.IsSuccessStatusCode)
                {
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        var missing = new CatalogCacheEntry(null, default, RetryAfterUtc: Now + RetryDelay,
                            FailedExpectedSha256: expectedSha256, FailureStatus: (int)response.StatusCode);
                        await SaveAsync(root, key, missing, null, token).ConfigureAwait(false);
                        if (root != LocalRoot) await MirrorAsync(key, missing, null, token).ConfigureAwait(false);
                    }
                    // Do not turn a failed diagnostic into a successful offline response.
                    if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout)
                    {
                        response.Dispose();
                        throw new HttpRequestException("A origem do catálogo está temporariamente indisponível.");
                    }
                    return response;
                }
                using (response)
                {
                    if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Catálogo acima do limite permitido.");
                    await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    using var output = new MemoryStream();
                    byte[] buffer = new byte[64 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        if (output.Length + read > maximumBytes) throw new InvalidDataException("Catálogo acima do limite permitido.");
                        output.Write(buffer, 0, read);
                    }
                    byte[] bytes = output.ToArray();
                    if (expectedSha256 is not null && !Matches(bytes, expectedSha256)) throw new InvalidDataException("O SHA-256 do catálogo não corresponde ao manifesto.");
                    using var json = JsonDocument.Parse(bytes);
                    var saved = new CatalogCacheEntry(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), Now,
                        response.Headers.ETag?.ToString(), response.Content.Headers.LastModified);
                    await SaveAsync(root, key, saved, bytes, token).ConfigureAwait(false);
                    if (root != LocalRoot) await MirrorAsync(key, saved, bytes, token).ConfigureAwait(false);
                    return Response(bytes, saved);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException
                || ex is OperationCanceledException && !token.IsCancellationRequested)
            {
                var failed = (active.Entry ?? new(null, default)) with
                { RetryAfterUtc = Now + RetryDelay, FailedExpectedSha256 = expectedSha256, FailureStatus = null };
                await SaveAsync(root, key, failed, null, token).ConfigureAwait(false);
                if (root != LocalRoot) await MirrorAsync(key, failed, active.Bytes, token).ConfigureAwait(false);
                if (allowStale && UsableStale(active.Entry, active.Bytes)) return Response(active.Bytes!, active.Entry);
                if (ex is InvalidDataException or JsonException) throw;
                throw new IOException("Não foi possível consultar a origem do catálogo.", ex);
            }
        }
    }

    public async Task<CatalogCacheUsage> GetUsageAsync(CancellationToken token = default)
    {
        return await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var files = ManagedFiles(LocalRoot).ToList();
            string marker = Path.Combine(LocalRoot, "maintenance.txt");
            DateTimeOffset? last = File.Exists(marker) && DateTimeOffset.TryParse(File.ReadAllText(marker), out var value) ? value : null;
            return new CatalogCacheUsage(files.Sum(f => f.Length), files.Count, last);
        }, token).ConfigureAwait(false);
    }

    private static IEnumerable<FileInfo> ManagedFiles(string root)
    {
        foreach (var (folder, extension) in new[] { ("objects", ".bin"), ("entries", ".json") })
        {
            string directory = Path.Combine(root, folder);
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                var file = new FileInfo(path);
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (file.Extension == extension && IsHash(Path.GetFileNameWithoutExtension(path))) yield return file;
                else
                {
                    string[] parts = file.Name.Split('.');
                    if (parts.Length == 5 && parts[0].Length == 0 && IsHash(parts[1])
                        && parts[2] == extension[1..] && parts[3].Length == 32 && parts[3].All(char.IsAsciiHexDigit)
                        && parts[4] == "tmp" && DateTime.UtcNow - file.LastWriteTimeUtc > TimeSpan.FromDays(1)) yield return file;
                }
            }
        }
    }

    public async Task MaintainAsync(bool force = false, CancellationToken token = default)
    {
        if (!force && Now.UtcTicks < Interlocked.Read(ref _nextMaintenanceTicks)) return;
        string localMarker = Path.Combine(LocalRoot, "maintenance.txt");
        if (!force && File.Exists(localMarker) && DateTimeOffset.TryParse(await File.ReadAllTextAsync(localMarker, token).ConfigureAwait(false), out var recent)
            && Now >= recent && Now - recent < TimeSpan.FromDays(1))
        {
            Interlocked.Exchange(ref _nextMaintenanceTicks, (recent + TimeSpan.FromDays(1)).UtcTicks);
            return;
        }
        foreach (string root in new[] { LocalRoot, _sharedRoot }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                await using var lease = await LockAsync(root, token).ConfigureAwait(false);
                string marker = Path.Combine(root, "maintenance.txt");
                if (!force && File.Exists(marker) && DateTimeOffset.TryParse(await File.ReadAllTextAsync(marker, token).ConfigureAwait(false), out var last)
                    && Now >= last && Now - last < TimeSpan.FromDays(1)) continue;
                await TrimUnderLeaseAsync(root, token).ConfigureAwait(false);
                await AtomicFile.WriteAllTextAsync(marker, Now.ToString("O"), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLog.Warning(ex, "CatalogCache.Maintain"); }
        }
        if (_maintainInstallers && OperatingSystem.IsWindows()) await WinProvisionApiService.MaintainInstallerCacheAsync(token).ConfigureAwait(false);
        Interlocked.Exchange(ref _nextMaintenanceTicks, (Now + TimeSpan.FromDays(1)).UtcTicks);
    }

    public async Task ClearAsync(CancellationToken token = default)
    {
        foreach (string root in new[] { LocalRoot })
        {
            await using var lease = await LockAsync(root, token).ConfigureAwait(false);
            foreach (var file in ManagedFiles(root)) { token.ThrowIfCancellationRequested(); file.Delete(); }
            await AtomicFile.WriteAllTextAsync(Path.Combine(root, "size.txt"), "0", token).ConfigureAwait(false);
        }
    }
}
