using System.Net.Http;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ReleaseCatalogStoreIntegrationTests
{
    [Fact]
    public async Task LocalIndexAppearsImmediatelyAndBackgroundRefreshReplacesIt()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wps-sql-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var local = Snapshot(directory, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Local.App", "Local");
            var remote = Snapshot(directory, "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "New.App", "Novo");
            var release = new FakeRelease(local, remote);
            using var http = new HttpClient(new FailingHandler());
            await using var store = new StoreService(http, null, directory, release);
            List<AppEntry> initial = await store.LoadCatalogAsync();
            Assert.Equal("Local.App", Assert.Single(initial).Id);
            Assert.Equal("Local.App", Assert.Single(store.Search("Local")).Id);
            release.Gate.SetResult();
            await store.WaitForRefreshAsync();
            Assert.Equal("New.App", Assert.Single(store.Search("Novo")).Id);
            Assert.Equal(1, release.RefreshCount);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StartupRevalidationDoesNotPopulateSearchOnDemand()
    {
        string directory = Path.Combine(Path.GetTempPath(), "wps-sql-start-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var snapshot = Snapshot(directory, "cccccccccccccccccccccccccccccccccccccccc", "Test.App", "Teste");
            var release = new FakeRelease(null, snapshot);
            using var http = new HttpClient(new FailingHandler());
            await using var store = new StoreService(http, null, directory, release);
            store.BeginStartupCatalogRevalidation();
            release.Gate.SetResult();
            await release.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(store.GetAll());
            Assert.Empty(store.Search("Teste"));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static ReleaseCatalogSnapshot Snapshot(string directory, string commit, string id, string name) =>
        new(commit, 2, 1, 1, Path.Combine(directory, commit + ".sqlite"),
            (AppEntry[])[new AppEntry { Id = id, Name = name, Source = "winget", Version = "1",
                CatalogDetailBaseUrl = WingetCdnCatalog.GitHubRawCatalogBaseUrl,
                CatalogDetailCatalogSha256 = commit }]);

    private sealed class FakeRelease(ReleaseCatalogSnapshot? local, ReleaseCatalogSnapshot remote) : IReleaseCatalogIndexService
    {
        public TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RefreshCount;
        public Task<ReleaseCatalogSnapshot?> TryLoadLocalSnapshotAsync(CancellationToken token = default) => Task.FromResult(local);
        public async Task<ReleaseCatalogSnapshot> RefreshAsync(CancellationToken token = default)
        {
            Interlocked.Increment(ref RefreshCount);
            await Gate.Task.WaitAsync(token);
            Finished.TrySetResult();
            return remote;
        }
        public Task ClearCacheAsync(CancellationToken token = default) => Task.CompletedTask;
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("A rede não deve ser usada neste teste.");
    }
}
