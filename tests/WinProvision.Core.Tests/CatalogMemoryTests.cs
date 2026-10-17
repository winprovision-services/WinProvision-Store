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
    public async Task UnchangedRevalidationKeepsInstancesAndSkipsIndexDownload()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var app = new AppEntry { Id = "Test.App", Name = "Teste", CatalogDetailPath = "apps/test.json" };
            string index = AotJson.Serialize(new[] { app }, WinProvisionJsonOptions.Compact);
            var manifest = new StoreService.CatalogManifest
            {
                SchemaVersion = 2, AppCount = 1, CatalogSha256 = new string('a', 64),
                IndexSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index))).ToLowerInvariant()
            };
            string manifestJson = AotJson.Serialize(manifest, WinProvisionJsonOptions.Compact);
            await File.WriteAllTextAsync(Path.Combine(directory, "catalog-v2-manifest.json"), manifestJson);
            await File.WriteAllTextAsync(Path.Combine(directory, $"catalog-v2-search-{manifest.CatalogSha256}.json"), index);
            using var handler = new CatalogHandler(manifestJson, index);
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
            Assert.Equal(0, handler.IndexRequests);

            await service.LoadCatalogAsync(forceRefresh: true);
            Assert.NotSame(original, Assert.Single(service.GetAll()));
            Assert.True(Assert.Single(service.GetAll()).IsInstalled);
            Assert.Equal(1, replacements);
            Assert.Equal(0, handler.IndexRequests);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class CatalogHandler(string manifest, string index) : HttpMessageHandler
    {
        public TaskCompletionSource AllowResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int IndexRequests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await AllowResponse.Task.WaitAsync(cancellationToken);
            string path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/msstore/")) return new(HttpStatusCode.NotFound);
            if (path.EndsWith("search-index.json")) Interlocked.Increment(ref IndexRequests);
            return new(HttpStatusCode.OK) { Content = new StringContent(path.EndsWith("search-index.json") ? index : manifest) };
        }
    }
}
