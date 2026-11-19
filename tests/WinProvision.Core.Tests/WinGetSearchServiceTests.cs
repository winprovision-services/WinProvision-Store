using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

[Collection("WinGet COM native query gate")]
public sealed class WinGetSearchServiceTests
{
    [Fact]
    public async Task SearchCanExcludeMicrosoftStoreCatalog()
    {
        bool includedStore = true;
        var service = new WinGetSearchService(
            (_, _, _) => Task.FromResult(true),
            (_, includeStore, _) =>
            {
                includedStore = includeStore;
                return Task.FromResult<IReadOnlyList<WinGetService.WinGetPackageMatch>>(
                    new WinGetService.WinGetPackageMatch[] { new("Vendor.App", "App", "1.0", "Vendor", "winget") });
            },
            (_, _, _) => throw new InvalidOperationException("O fallback não deveria ser usado."),
            () => WinProvision.Core.Models.WinGetEnginePreference.ComApi);

        await service.SearchAsync("app", CancellationToken.None, includeMicrosoftStore: false);

        Assert.False(includedStore);
    }

    [Fact]
    public async Task SearchAsyncReturnsComResultsWithoutLaunchingRedundantStoreCliSearch()
    {
        bool provisioned = false;
        bool comCalled = false;
        int cliCalls = 0;
        var service = new WinGetSearchService(
            (probeCom, _, _) => { Assert.True(probeCom); provisioned = true; return Task.FromResult(true); },
            (_, _, _) =>
            {
                Assert.True(provisioned);
                comCalled = true;
                return Task.FromResult<IReadOnlyList<WinGetService.WinGetPackageMatch>>(
                    new WinGetService.WinGetPackageMatch[] { new("Vendor.App", "App", "1.0", "Vendor", "winget") });
            },
            (_, _, _) =>
            {
                cliCalls++;
                throw new InvalidOperationException("Unexpected CLI search.");
            },
            () => WinProvision.Core.Models.WinGetEnginePreference.Automatic);

        var results = await service.SearchAsync("app", CancellationToken.None);

        Assert.True(comCalled);
        Assert.Equal(0, cliCalls);
        Assert.Equal("Vendor.App", Assert.Single(results).PackageId);
    }

    [Fact]
    public async Task SearchAsyncUsesCliWhenCliIsPreferred()
    {
        int comCalls = 0;
        var service = new WinGetSearchService(
            (probeCom, _, _) => { Assert.False(probeCom); return Task.FromResult(true); },
            (_, _, _) => { comCalls++; return Task.FromResult<IReadOnlyList<WinGetService.WinGetPackageMatch>>([]); },
            (_, source, _) => Task.FromResult<IReadOnlyList<WinGetService.WinGetPackageMatch>>(
                source is null
                    ? new WinGetService.WinGetPackageMatch[] { new("Vendor.App", "App", "1.0", "", "winget") }
                    : Array.Empty<WinGetService.WinGetPackageMatch>()),
            () => WinProvision.Core.Models.WinGetEnginePreference.WingetCli);

        var results = await service.SearchAsync("app", CancellationToken.None);

        Assert.Equal(0, comCalls);
        Assert.Equal("Vendor.App", Assert.Single(results).PackageId);
    }

    [Fact]
    public async Task SearchFallsBackAfterAutomaticComFailure()
    {
        int primaryCliCalls = 0;
        var service = new WinGetSearchService(
            (_, _, _) => Task.FromResult(true),
            (_, _, _) => throw new InvalidOperationException("Consulta COM falhou."),
            (_, source, _) =>
            {
                if (source is null) primaryCliCalls++;
                return Task.FromResult<IReadOnlyList<WinGetService.WinGetPackageMatch>>(
                    source is null
                        ? new WinGetService.WinGetPackageMatch[] { new("Vendor.App", "App", "1.0", "", "winget") }
                        : Array.Empty<WinGetService.WinGetPackageMatch>());
            },
            () => WinProvision.Core.Models.WinGetEnginePreference.Automatic);

        Assert.Single(await service.SearchAsync("app", CancellationToken.None));
        Assert.Equal(1, primaryCliCalls);
    }

    [Fact]
    public async Task EmptyComSearchDoesNotTriggerFallback()
    {
        int cliCalls = 0;
        var service = new WinGetSearchService(
            (_, _, _) => Task.FromResult(true),
            (_, _, _) => Task.FromResult<IReadOnlyList<WinGetService.WinGetPackageMatch>>([]),
            (_, _, _) => { cliCalls++; throw new InvalidOperationException("CLI must not run for an empty COM result."); },
            () => WinProvision.Core.Models.WinGetEnginePreference.Automatic);

        Assert.Empty(await service.SearchAsync("missing", CancellationToken.None));
        Assert.Equal(0, cliCalls);
    }

    [Fact]
    public void CliParserReadsRowsAndSkipsTruncatedIdentifiers()
    {
        const string output = "Name  Id  Version  Match  Source\n----\nApp  Vendor.App  1.0  Tag: app  winget\nOther  Vendor...  1.0  Tag: other  winget\n";

        var result = WinGetCliSearchService.ParseOutput(output);

        var match = Assert.Single(result);
        Assert.Equal("Vendor.App", match.PackageId);
        Assert.Equal("App", match.Name);
        Assert.Equal("winget", match.Source);
    }

    [Fact]
    public void CliParserUsesRequestedSourceWhenLocalizedTableOmitsIt()
    {
        using var stream = typeof(WinGetSearchServiceTests).Assembly.GetManifestResourceStream(
            "WinProvision.Core.Tests.Fixtures.winget-search-steam.txt")!;
        using var reader = new StreamReader(stream);
        var matches = WinGetCliSearchService.ParseOutput(reader.ReadToEnd(), "winget");

        var steam = Assert.Single(matches, match => match.PackageId == "Valve.Steam");
        Assert.Equal("winget", steam.Source);
        Assert.Equal("2.10.91.91", steam.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("WinGet failed to connect to the source.")]
    [InlineData("Name  Id\n----\nMalformed row")]
    public void CliParserReturnsEmptyForUnrecognizedOutput(string output) =>
        Assert.Empty(WinGetCliSearchService.ParseOutput(output));

    [Fact]
    public async Task BoundedComQueryTimeoutKeepsGateUntilLateWorkerCompletes()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool secondStarted = false;
        try
        {
            await Assert.ThrowsAsync<TimeoutException>(() => WinGetComQueryService.RunBoundedAsync(
                _ => completion.Task, TimeSpan.FromMilliseconds(250), CancellationToken.None));
            await Assert.ThrowsAsync<TimeoutException>(() => WinGetComQueryService.RunBoundedAsync(
                _ => { secondStarted = true; return Task.FromResult(0); }, TimeSpan.FromMilliseconds(30), CancellationToken.None));
            Assert.False(secondStarted);
        }
        finally
        {
            completion.TrySetResult(7);
        }
        int result = await WinGetComQueryService.RunBoundedAsync(
            _ => Task.FromResult(8), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(8, result);
    }

    [Fact]
    public async Task BoundedComQueryCancellationObservesWorkerAndReleasesGateAfterCompletion()
    {
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var query = WinGetComQueryService.RunBoundedAsync(token =>
        {
            started.TrySetResult(true);
            return completion.Task;
        }, TimeSpan.FromSeconds(5), cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);

        completion.SetResult(7);
        int result = await WinGetComQueryService.RunBoundedAsync(
            _ => Task.FromResult(8), TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Equal(8, result);
    }
}
