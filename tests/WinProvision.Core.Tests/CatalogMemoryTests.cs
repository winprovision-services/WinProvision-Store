using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class CatalogMemoryTests
{
    [Fact]
    public async Task UnchangedRevalidationKeepsInstances()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string index = """[{"id":"Test.App","appPath":"manifests/t/test/app/app.json","appSha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","name":"Teste","publisher":"Teste","latestVersion":"1.0","architectures":["x64"],"tags":[]}]""";
            string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index))).ToLowerInvariant();
            string catalog = $$"""{"schemaVersion":1,"generation":"test","generatedAtUtc":"2026-10-21T00:00:00Z","packageCount":1,"searchIndexPath":"winget/v1/search/{{hash}}.json","searchIndexSha256":"{{hash}}"}""";
            await File.WriteAllTextAsync(Path.Combine(directory, "winget-v1-catalog.json"), catalog);
            await File.WriteAllTextAsync(Path.Combine(directory, "winget-v1-search.json"), index);
            using var handler = new CatalogHandler(catalog, index);
            using var client = new HttpClient(handler);
            await using var service = new StoreService(client, null, directory);
            int replacements = 0;
            service.CatalogUpdated += _ => replacements++;
            var loaded = await service.LoadCatalogAsync();
            var original = Assert.Single(loaded);
            original.IsInstalled = true;
            handler.AllowResponse.SetResult();
            await service.WaitForRefreshAsync();
            Assert.Same(original, Assert.Single(service.GetAll()));
            Assert.True(original.IsInstalled);
            Assert.Equal(0, replacements);
            Assert.Equal(1, handler.IndexRequests);

            await service.LoadCatalogAsync(forceRefresh: true);
            Assert.NotSame(original, Assert.Single(service.GetAll()));
            Assert.True(Assert.Single(service.GetAll()).IsInstalled);
            Assert.Equal(1, replacements);
            Assert.Equal(2, handler.IndexRequests);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class CatalogHandler(string catalog, string index) : HttpMessageHandler
    {
        public TaskCompletionSource AllowResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int IndexRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await AllowResponse.Task.WaitAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/search/", StringComparison.Ordinal)) Interlocked.Increment(ref IndexRequests);
            return new(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(path.Contains("/search/", StringComparison.Ordinal) ? index : catalog)
            };
        }
    }
}
