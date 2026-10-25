using WinProvision.PackageManagement;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class CliQueryFallbackTests
{
    [Fact]
    public void WinProvisionCatalogKeepsFullTextMatchesAndAppliesExactSourceFilters()
    {
        var app = new AppEntry
        {
            Id = "Contoso.Editor", Name = "Text Editor", Source = "winget",
            Publisher = "Contoso", Description = "Code editor"
        };

        Assert.True(ApplicationEngine.MatchesWinProvisionCatalogEntry(app,
            new InteractiveCliRequest("search", Query: "code")));
        Assert.False(ApplicationEngine.MatchesWinProvisionCatalogEntry(app,
            new InteractiveCliRequest("search", Query: "Text Editor", Exact: true, Source: "msstore")));
        Assert.True(ApplicationEngine.MatchesWinProvisionCatalogEntry(app,
            new InteractiveCliRequest("search", Id: "contoso.editor", Source: "winget")));
    }

    [Fact]
    public async Task ComFailureUsesCliBeforeOwnCatalog()
    {
        var calls = new List<string>();
        var result = await PackageQueryFallback.RunAsync<int>((Func<CancellationToken, Task<int>>[])[
            _ => { calls.Add("com"); throw new InvalidOperationException("Sem COM"); },
            _ => { calls.Add("cli"); return Task.FromResult(9); },
            _ => { calls.Add("winprovision"); return Task.FromResult(7); }
        ], n => n > 0, _ => { }, default);
        Assert.Equal(9, result);
        Assert.Equal(["com", "cli"], calls);
    }

    [Fact]
    public async Task EmptyComAndCliResultsReachWinProvisionCatalog()
    {
        var calls = new List<string>();
        var result = await PackageQueryFallback.RunAsync<int>((Func<CancellationToken, Task<int>>[])[
            _ => { calls.Add("com"); throw new NotSupportedException(); },
            _ => { calls.Add("cli"); return Task.FromResult(0); },
            _ => { calls.Add("winprovision"); return Task.FromResult(7); }
        ], n => n > 0, _ => { }, default);
        Assert.Equal(7, result);
        Assert.Equal(["com", "cli", "winprovision"], calls);
    }

    [Fact]
    public async Task IntegrityFailureDoesNotFallBack()
    {
        bool cliCalled = false;
        await Assert.ThrowsAsync<InvalidDataException>(() => PackageQueryFallback.RunAsync<int>((Func<CancellationToken, Task<int>>[])[
            _ => throw new InvalidDataException("Hash divergente"),
            _ => { cliCalled = true; return Task.FromResult(1); }
        ], _ => true, _ => { }, default));
        Assert.False(cliCalled);
    }

    [Fact]
    public async Task CancellationStopsBeforeAnotherProvider()
    {
        using var cancellation = new CancellationTokenSource();
        bool cliCalled = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PackageQueryFallback.RunAsync<int>((Func<CancellationToken, Task<int>>[])[
            _ => { cancellation.Cancel(); return Task.FromResult(0); },
            _ => { cliCalled = true; return Task.FromResult(1); }
        ], _ => false, _ => { }, cancellation.Token));
        Assert.False(cliCalled);
    }
}
