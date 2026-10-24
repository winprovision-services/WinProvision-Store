using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed class ProvisioningWallpaperCatalogService
{
    private const string CatalogUrl = CatalogEndpoints.PublicBaseUrl + "/windows/wallpaper/wallpapers.json";
    private const string WallpaperPrefix = "windows/wallpaper/";
    private const int MaxCatalogBytes = 256 * 1024;
    private const int MaxWallpaperBytes = 25 * 1024 * 1024;
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "Cache", "Wallpapers");
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static readonly HttpClient Client = new(new RequestRateLimitHandler(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })) { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<IReadOnlyList<ProvisioningWallpaperDescriptor>> GetWallpapersAsync(CancellationToken token)
    {
        using var response = await Client.GetAsync(CatalogUrl, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxCatalogBytes)
            throw new InvalidDataException("O catálogo de wallpapers excede o limite permitido.");

        using Stream body = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[8192];
        int read;
        while ((read = await body.ReadAsync(chunk.AsMemory(), token)) > 0)
        {
            if (buffer.Length + read > MaxCatalogBytes)
                throw new InvalidDataException("O catálogo de wallpapers excede o limite permitido.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), token);
        }

        using JsonDocument document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out JsonElement version)
            || version.GetInt32() != 1
            || !root.TryGetProperty("wallpapers", out JsonElement wallpapers)
            || wallpapers.ValueKind != JsonValueKind.Array
            || wallpapers.GetArrayLength() > 100)
            throw new InvalidDataException("O catálogo de wallpapers tem um formato inesperado.");

        var result = new List<ProvisioningWallpaperDescriptor>(wallpapers.GetArrayLength());
        foreach (JsonElement entry in wallpapers.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            string fileName = entry.GetProperty("fileName").GetString() ?? string.Empty;
            string key = entry.GetProperty("key").GetString() ?? string.Empty;
            string id = entry.GetProperty("id").GetString() ?? string.Empty;
            long sizeBytes = entry.TryGetProperty("sizeBytes", out JsonElement size) && size.TryGetInt64(out long parsedSize)
                ? parsedSize : 0;
            string? etag = entry.TryGetProperty("etag", out JsonElement tag) ? tag.GetString() : null;
            if (!key.StartsWith(WallpaperPrefix, StringComparison.Ordinal)
                || key[WallpaperPrefix.Length..] != fileName
                || fileName.Length is < 5 or > 64
                || fileName.Contains('/') || fileName.Contains('\\')
                || !(fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                || id != Path.GetFileNameWithoutExtension(fileName))
                throw new InvalidDataException("O catálogo contém um caminho de wallpaper inválido.");

            result.Add(new ProvisioningWallpaperDescriptor(id, fileName,
                CatalogEndpoints.PublicBaseUrl + "/" + WallpaperPrefix + Uri.EscapeDataString(fileName), sizeBytes, etag));
        }

        return result;
    }

    public async Task<byte[]> DownloadWallpaperAsync(ProvisioningWallpaperDescriptor wallpaper, CancellationToken token)
    {
        string fileName = wallpaper.FileName;
        if (fileName.Length is < 5 or > 64 || fileName.Contains('/') || fileName.Contains('\\')
            || !(fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("O nome do wallpaper não é válido.");

        if (wallpaper.SizeBytes is < 1 or > MaxWallpaperBytes)
            throw new InvalidDataException("O tamanho publicado para o wallpaper não é válido.");

        await CacheGate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            string path = Path.Combine(CacheDirectory, fileName);
            string etagPath = path + ".etag";
            byte[]? cachedBytes = File.Exists(path) ? await File.ReadAllBytesAsync(path, token) : null;
            string? cachedEtag = File.Exists(etagPath) ? await File.ReadAllTextAsync(etagPath, token) : null;
            if (cachedBytes is { Length: > 0 } && cachedBytes.Length == wallpaper.SizeBytes
                && string.Equals(cachedEtag, wallpaper.ETag, StringComparison.Ordinal))
                return cachedBytes;

            string url = CatalogEndpoints.PublicBaseUrl + "/" + WallpaperPrefix + Uri.EscapeDataString(fileName);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cachedBytes is { Length: > 0 } && cachedBytes.Length == wallpaper.SizeBytes
                && EntityTagHeaderValue.TryParse(cachedEtag, out var priorTag))
                request.Headers.IfNoneMatch.Add(priorTag);
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.StatusCode == HttpStatusCode.NotModified && cachedBytes is { Length: > 0 })
                return cachedBytes;
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is > MaxWallpaperBytes)
                throw new InvalidDataException("O wallpaper excede o limite de 25 MB.");
            string mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (mediaType is not ("image/jpeg" or "image/png"))
                throw new InvalidDataException("O arquivo recebido não é uma imagem JPEG ou PNG.");

            await using Stream body = await response.Content.ReadAsStreamAsync(token);
            using var buffer = new MemoryStream();
            byte[] chunk = new byte[64 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk.AsMemory(), token)) > 0)
            {
                if (buffer.Length + read > MaxWallpaperBytes)
                    throw new InvalidDataException("O wallpaper excede o limite de 25 MB.");
                await buffer.WriteAsync(chunk.AsMemory(0, read), token);
            }

            byte[] bytes = buffer.ToArray();
            if (bytes.Length != wallpaper.SizeBytes)
                throw new InvalidDataException("O wallpaper baixado não corresponde ao tamanho publicado no catálogo.");
            await AtomicFile.WriteAllBytesAsync(path, bytes, token);
            if (response.Headers.ETag is { } responseTag)
                await AtomicFile.WriteAllBytesAsync(etagPath, System.Text.Encoding.UTF8.GetBytes(responseTag.ToString()), token);
            return bytes;
        }
        finally { CacheGate.Release(); }
    }
}

public sealed record ProvisioningWallpaperDescriptor(string Id, string FileName, string Url, long SizeBytes, string? ETag);
