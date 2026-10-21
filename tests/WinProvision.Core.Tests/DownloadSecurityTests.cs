using System.Net;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Profile;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class DownloadSecurityTests
{
    [Theory]
    [InlineData("http://attacker.invalid/catalog.json")]
    [InlineData("https://attacker.invalid/catalog.json")]
    public async Task CatalogMetadataRejectsDowngradeAndCrossOriginRedirects(string redirect)
    {
        string cache = Path.Combine(Path.GetTempPath(), "WinProvision-redirect-test-" + Guid.NewGuid().ToString("N"));
        var handler = new RedirectHandler(redirect);
        using var http = new HttpClient(handler);
        await using var api = new WinProvisionApiService(http, cache);
        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => api.GetIndexAsync());
            Assert.Single(handler.Requests); // Only the current catalog endpoint.
            Assert.All(handler.Requests, uri => Assert.Equal(Uri.UriSchemeHttps, uri.Scheme));
        }
        finally
        {
            if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task JsonPayloadLimitIsEnforcedEvenWhenContentLengthIsMissing()
    {
        using var content = new ByteArrayContent(new byte[16]);
        content.Headers.ContentLength = null;
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WinProvisionApiService.ReadBoundedPayloadAsync(content, 8, CancellationToken.None));
    }

    [Theory]
    [InlineData(1, 16)]
    [InlineData(2, 12)]
    [InlineData(3, 8)]
    [InlineData(4, 6)]
    [InlineData(8, 3)]
    public void InstallerConnectionBudgetBalancesFilesAndSegments(int parallelDownloads, int perFile)
    {
        Assert.Equal(perFile, WinProvisionApiService.ConnectionsPerDownload(parallelDownloads));
        Assert.True(parallelDownloads * perFile <= 24);
    }

    [Theory]
    [InlineData("../outside.exe")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("folder/LPT³.log")]
    public void OfflineZipPathRejectsTraversalAndWindowsDeviceNames(string relative)
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-archive-test");
        Assert.Throws<InvalidDataException>(() => OfflineCollectionExportService.SafePath(root, relative));
    }

    [Fact]
    public void OfflineZipPathAcceptsOrdinaryNestedNames()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-archive-test");
        string path = OfflineCollectionExportService.SafePath(root, "vendor/setup.exe");
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RedirectHandler(string location) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect)
            {
                Headers = { Location = new Uri(location) }
            });
        }
    }
}
