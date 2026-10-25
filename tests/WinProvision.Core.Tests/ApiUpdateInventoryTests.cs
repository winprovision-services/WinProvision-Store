using WinProvision.Core.Models;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ApiUpdateInventoryTests
{
    [Fact]
    public void CorrelatedWingetIdentityReplacesRegistryAliasAndKeepsInstallMetadata()
    {
        var identified = new UpgradablePackage
        {
            Id = "Acme.App", Name = "Acme App", Source = "winget", CurrentVersion = "1.0"
        };
        var registry = new UpgradablePackage
        {
            Id = "{registry-key}", Name = "Acme App (64-bit)", Source = "winget", CurrentVersion = "1.0",
            InstalledScope = "user", InstalledArchitecture = "x64", InstalledInstallerType = "msi"
        };

        var item = Assert.Single(WinGetService.MergeUpdateInventory((UpgradablePackage[])[identified], (UpgradablePackage[])[registry]));
        Assert.Equal("Acme.App", item.Id);
        Assert.Equal("user", item.InstalledScope);
        Assert.Equal("x64", item.InstalledArchitecture);
        Assert.Equal("msi", item.InstalledInstallerType);
    }

    [Fact]
    public void RegistryInventorySurvivesUnavailableWinget()
    {
        var registry = new UpgradablePackage { Id = "Acme.App", Name = "Acme App", Source = "winget" };
        Assert.Same(registry, Assert.Single(WinGetService.MergeUpdateInventory(Array.Empty<UpgradablePackage>(), (UpgradablePackage[])[registry])));
    }

    [Fact]
    public void SameNameInTwoScopesIsNotCollapsedIntoOneInstallation()
    {
        var identified = new UpgradablePackage
        {
            Id = "Acme.App", Name = "Acme App", Source = "winget", CurrentVersion = "1.0"
        };
        var user = new UpgradablePackage
        {
            Id = "user-key", Name = "Acme App", Source = "winget", CurrentVersion = "1.0", InstalledScope = "user"
        };
        var machine = new UpgradablePackage
        {
            Id = "machine-key", Name = "Acme App", Source = "winget", CurrentVersion = "1.0", InstalledScope = "machine"
        };
        Assert.Equal(3, WinGetService.MergeUpdateInventory((UpgradablePackage[])[identified], (UpgradablePackage[])[user, machine]).Count);
        Assert.Null(identified.InstalledScope);
    }

    [Fact]
    public void DistinctInstalledVersionsRemainVisible()
    {
        var identified = new UpgradablePackage
        {
            Id = "Acme.App", Name = "Acme App", Source = "winget", CurrentVersion = "2.0"
        };
        var registry = new UpgradablePackage
        {
            Id = "{registry-key}", Name = "Acme App", Source = "winget", CurrentVersion = "1.0"
        };
        Assert.Equal(2, WinGetService.MergeUpdateInventory((UpgradablePackage[])[identified], (UpgradablePackage[])[registry]).Count);
    }
}
