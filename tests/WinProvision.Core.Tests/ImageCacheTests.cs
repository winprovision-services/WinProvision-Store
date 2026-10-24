using System.Net;
using System.Net.Http.Headers;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class ImageCacheTests
{
    [Fact]
    public async Task ConditionalRequestReusesValidatorsOnNotModified()
    {
        var previous = new ImageCacheMetadata("\"v1\"", DateTimeOffset.UtcNow.AddDays(-20), DateTimeOffset.UtcNow.AddDays(-20));
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("\"v1\"", Assert.Single(request.Headers.IfNoneMatch).ToString());
            Assert.NotNull(request.Headers.IfModifiedSince);
            return new(HttpStatusCode.NotModified);
        }));
        var result = await ImageCacheTransfer.DownloadAsync(client, "https://example.test/icon", previous, default);
        Assert.True(result.NotModified);
        Assert.Null(result.Bytes);
        Assert.Equal(previous.ETag, result.Metadata!.ETag);
        Assert.True(result.Metadata.ValidatedUtc > previous.ValidatedUtc);
    }

    [Fact]
    public async Task DownloadExposesReplacementOnlyAfterSuccessfulTransfer()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]), Headers = { ETag = new EntityTagHeaderValue("\"v2\"") }
        }));
        var result = await ImageCacheTransfer.DownloadAsync(client, "https://example.test/icon", null, default);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes);
        Assert.Equal("\"v2\"", result.Metadata!.ETag);
        using var failed = new HttpClient(new Handler(_ => new(HttpStatusCode.ServiceUnavailable)));
        await Assert.ThrowsAsync<HttpRequestException>(() => ImageCacheTransfer.DownloadAsync(failed, "https://example.test/icon", result.Metadata, default));
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes);
    }

    [Fact]
    public async Task TransientServerFailureIsRetriedBeforePublishingImage()
    {
        int attempts = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                return new(HttpStatusCode.ServiceUnavailable);
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent([4, 5, 6]) };
        }));

        var result = await ImageCacheTransfer.DownloadAsync(client, "https://example.test/wallpaper", null, default);

        Assert.Equal(2, attempts);
        Assert.Equal(new byte[] { 4, 5, 6 }, result.Bytes);
    }

    [Fact]
    public async Task OversizedAndCanceledRequestsCannotBePublished()
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
            response.Content.Headers.ContentLength = ImageCacheTransfer.MaxBytes + 1;
            return response;
        }));
        await Assert.ThrowsAsync<InvalidDataException>(() => ImageCacheTransfer.DownloadAsync(client, "https://example.test/icon", null, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageCacheTransfer.DownloadAsync(client, "https://example.test/icon", null, new(true)));
    }

    [Fact]
    public async Task PreferencesUsageAndCatalogCleanupStayWithinTheirCategory()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var preferences = new ImageCachePreferencesService(Path.Combine(root, "settings.json"));
            Assert.Equal(new ImageCacheOptions(), preferences.Options);
            preferences.Save(new(128, 7));
            Assert.Equal(new ImageCacheOptions(128, 7), new ImageCachePreferencesService(Path.Combine(root, "settings.json")).Options);
            Assert.Throws<ArgumentOutOfRangeException>(() => preferences.Save(new(0, 0)));
            string images = Path.Combine(root, "images");
            Directory.CreateDirectory(images);
            await File.WriteAllBytesAsync(Path.Combine(images, "image.img"), [1, 2, 3]);
            string catalog = Path.Combine(root, "catalog");
            await using var api = new WinProvisionApiService(cacheDir: catalog);
            await File.WriteAllTextAsync(Path.Combine(catalog, "catalog-index.json"), "{}");
            await api.ClearCatalogCacheAsync();
            Assert.True(File.Exists(Path.Combine(images, "image.img")));
            Assert.True(File.Exists(Path.Combine(root, "settings.json")));
            Assert.Equal(new CacheUsage(3, 1), CacheStorageUsage.Measure(images));
            Assert.Empty(Directory.GetFiles(catalog));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(send(request));
        }
    }
}
