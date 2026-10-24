using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public sealed record ImageCacheOptions(int MaxDiskMegabytes = 512, int RevalidateAfterDays = 14)
{
    public void Validate()
    {
        if (MaxDiskMegabytes is < 64 or > 2048 || RevalidateAfterDays is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(MaxDiskMegabytes), "Use de 64 a 2048 MB e de 1 a 30 dias.");
    }
}

public sealed class ImageCachePreferencesService
{
    private readonly string _path;
    public ImageCacheOptions Options { get; private set; } = new();
    public ImageCachePreferencesService(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStoreWinUI3", "image-cache-preferences.json");
        try
        {
            var saved = JsonSerializer.Deserialize(File.ReadAllText(_path), ImageCacheJsonContext.Default.ImageCacheOptions);
            saved?.Validate();
            Options = saved ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { if (ex is not FileNotFoundException and not DirectoryNotFoundException) CoreLog.Warning(ex, "ImageCache.Preferences"); }
    }
    public void Save(ImageCacheOptions options)
    {
        options.Validate();
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(options, ImageCacheJsonContext.Default.ImageCacheOptions));
        Options = options;
    }
}

public sealed record ImageCacheMetadata(string? ETag, DateTimeOffset? LastModified, DateTimeOffset ValidatedUtc);
public sealed record ImageDownload(byte[]? Bytes, ImageCacheMetadata? Metadata = null, bool NotModified = false);

public static class ImageCacheTransfer
{
    public const int MaxBytes = 16 * 1024 * 1024;
    public static ImageCacheMetadata? ReadMetadata(string path)
    {
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), ImageCacheJsonContext.Default.ImageCacheMetadata); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static Task WriteMetadataAsync(string path, ImageCacheMetadata metadata, CancellationToken token)
        => AtomicFile.WriteAllBytesAsync(path, JsonSerializer.SerializeToUtf8Bytes(metadata, ImageCacheJsonContext.Default.ImageCacheMetadata), token);

    public static async Task<ImageDownload> DownloadAsync(HttpClient client, string url, ImageCacheMetadata? metadata, CancellationToken token)
    {
        HttpResponseMessage? response = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (metadata is not null)
            {
                if (EntityTagHeaderValue.TryParse(metadata.ETag, out var etag)) request.Headers.IfNoneMatch.Add(etag);
                if (metadata.LastModified is { } modified) request.Headers.IfModifiedSince = modified;
            }

            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (attempt == 2 || !IsTransient(response.StatusCode))
                break;

            TimeSpan delay = GetRetryDelay(response, attempt);
            response.Dispose();
            response = null;
            await Task.Delay(delay, token).ConfigureAwait(false);
        }

        using HttpResponseMessage finalResponse = response ?? throw new HttpRequestException("O servidor não retornou uma resposta.");
        var updated = new ImageCacheMetadata(finalResponse.Headers.ETag?.ToString(), finalResponse.Content.Headers.LastModified, DateTimeOffset.UtcNow);
        if (finalResponse.StatusCode == HttpStatusCode.NotModified && metadata is not null)
            return new(null, updated with { ETag = updated.ETag ?? metadata.ETag, LastModified = updated.LastModified ?? metadata.LastModified }, true);
        finalResponse.EnsureSuccessStatusCode();
        if (finalResponse.Content.Headers.ContentLength > MaxBytes) throw new InvalidDataException("A imagem excede o limite do carregador.");
        await using var stream = await finalResponse.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[32768];
        int count;
        while ((count = await stream.ReadAsync(chunk, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxBytes) throw new InvalidDataException("A imagem excede o limite do carregador.");
            buffer.Write(chunk, 0, count);
        }
        return new(buffer.ToArray(), updated);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
            retryAfter = date - DateTimeOffset.UtcNow;
        return retryAfter is { } specified
            ? TimeSpan.FromMilliseconds(Math.Clamp(specified.TotalMilliseconds, 0, 30_000))
            : TimeSpan.FromMilliseconds(500 * (1 << attempt));
    }
}

[JsonSerializable(typeof(ImageCacheOptions))]
[JsonSerializable(typeof(ImageCacheMetadata))]
internal partial class ImageCacheJsonContext : JsonSerializerContext;
