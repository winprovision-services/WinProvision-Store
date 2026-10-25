using WinProvision.Core.Models;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class UpdateDiscoveryMergeTests
{
    [Fact]
    public async Task UnresponsiveNativeSourceCannotBlockTheNextSource()
    {
        var pending = new TaskCompletionSource<List<UpgradablePackage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var scan = WinGetService.RunUpdateQueryAsync(_ => pending.Task,
                TimeSpan.FromMilliseconds(100), CancellationToken.None);
            var error = await Assert.ThrowsAsync<TimeoutException>(() => scan.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Contains("fonte de atualizações", error.Message);

            var expected = new UpgradablePackage { Id = "Contoso.App", Name = "Contoso App", AvailableVersion = "2.0" };
            var fallback = await WinGetService.RunUpdateQueryAsync(_ => Task.FromResult(new List<UpgradablePackage> { expected }),
                TimeSpan.FromSeconds(1), CancellationToken.None);
            Assert.Same(expected, Assert.Single(fallback));
        }
        finally { pending.TrySetResult([]); }
    }

    [Fact]
    public async Task UserCancellationStopsWaitingEvenWhenNativeSourceIgnoresItsToken()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<List<UpgradablePackage>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var scan = WinGetService.RunUpdateQueryAsync(_ =>
            {
                entered.TrySetResult();
                return pending.Task;
            }, TimeSpan.FromSeconds(30), cancellation.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        finally { pending.TrySetResult([]); }
    }

    [Fact]
    public void NewerCliOfferReplacesStaleComTargetAndKeepsMetadata()
    {
        var cli = new UpgradablePackage
        {
            Id = "Contoso.App", Name = "Contoso App", Source = "winget",
            CurrentVersion = "1.0", AvailableVersion = "3.0"
        };
        var com = new UpgradablePackage
        {
            Id = "contoso.app", Name = "Contoso App", Source = "winget",
            CurrentVersion = "1.0", AvailableVersion = "2.0", Publisher = "Contoso"
        };

        var merged = WinGetService.MergeUpdates(new[] { com }, new[] { cli });

        var offer = Assert.Single(merged);
        Assert.Equal("3.0", offer.AvailableVersion);
        Assert.Equal("Contoso", offer.Publisher);
    }

    [Fact]
    public void CliAddsMissingPackagesAndNewerOffers()
    {
        var com = new UpgradablePackage
        {
            Id = "Contoso.App", Name = "Contoso App", Source = "winget",
            CurrentVersion = "1.0", AvailableVersion = "2.0"
        };
        var cli = new UpgradablePackage
        {
            Id = "Contoso.App", Name = "Contoso App", Source = "winget",
            CurrentVersion = "1.0", AvailableVersion = "3.0"
        };
        var cliOnly = new UpgradablePackage
        {
            Id = "Fabrikam.Tool", Name = "Fabrikam Tool", Source = "winget",
            CurrentVersion = "4.0", AvailableVersion = "5.0"
        };

        var merged = WinGetService.MergeUpdates(new[] { com }, new[] { cli, cliOnly });

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, package => package.Id == "Contoso.App" && package.AvailableVersion == "3.0");
        Assert.Contains(merged, package => package.Id == "Fabrikam.Tool" && package.AvailableVersion == "5.0");
    }

    [Theory]
    [InlineData("1.0", "1.5")]
    [InlineData("9.0", "3.0")]
    [InlineData("1.0", "3.0-preview")]
    public void OlderOpaqueOrConflictingCliOfferCannotReplaceCom(string installed, string available)
    {
        var com = new UpgradablePackage { Id = "Acme.App", Name = "App", Source = "winget", CurrentVersion = "1.0", AvailableVersion = "2.0" };
        var cli = new UpgradablePackage { Id = "Acme.App", Name = "App", Source = "winget", CurrentVersion = installed, AvailableVersion = available };
        Assert.Same(com, Assert.Single(WinGetService.MergeUpdates(new[] { com }, new[] { cli })));
    }
}
