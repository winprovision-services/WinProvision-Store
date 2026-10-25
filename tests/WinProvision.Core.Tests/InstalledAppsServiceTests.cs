using Microsoft.Win32;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstalledAppsServiceTests
{
    [Theory]
    [InlineData(false, "uninstall.exe", null)]
    [InlineData(true, "uninstall.exe", "msi")]
    [InlineData(false, "MsiExec.exe /X{product-code}", "msi")]
    public void InventoryKeepsUnknownPublisherAndInstallerTechnologyAbsent(bool windowsInstaller, string uninstall, string? expectedType)
    {
        var app = InstalledAppsService.ToUpdateInventoryEntry(new InstalledAppDetail
        {
            Id = "registry-key", DisplayName = "App", DisplayVersion = "23.10", Scope = "user",
            UninstallString = uninstall, IsWindowsInstaller = windowsInstaller
        });
        Assert.Empty(app.Publisher);
        Assert.Equal(expectedType, app.InstalledInstallerType);
        Assert.Equal("registry-key", app.InstalledRegistryId);
        Assert.Equal("user", app.InstalledScope);
    }

    [Fact]
    public void RegistryLocationsCoverBothHivesAndViewsWithoutHardcodedWow6432Node()
    {
        var locations = InstalledAppsService.GetRegistryLocations();

        Assert.Equal(4, locations.Length);
        Assert.Contains(locations, location => location.Hive == RegistryHive.LocalMachine && location.View == RegistryView.Registry64);
        Assert.Contains(locations, location => location.Hive == RegistryHive.LocalMachine && location.View == RegistryView.Registry32);
        Assert.Contains(locations, location => location.Hive == RegistryHive.CurrentUser && location.View == RegistryView.Registry64);
        Assert.Contains(locations, location => location.Hive == RegistryHive.CurrentUser && location.View == RegistryView.Registry32);
        Assert.All(locations, location => Assert.Equal(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", location.Path));
    }

    [Fact]
    public void IsInstalledUsesCompleteWingetListIdsAndRejectsConflictingSameNameIdentity()
    {
        var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Vendor.Real" };
        var idsByName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["Example App"] = new(StringComparer.OrdinalIgnoreCase) { "Vendor.Real" }
        };
        var registryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Example App" };

        Assert.True(InstalledAppsService.IsInstalledByIdentity("Example App", "Vendor.Real", installedIds, idsByName, registryNames));
        Assert.False(InstalledAppsService.IsInstalledByIdentity("Example App", "Vendor.Other", installedIds, idsByName, registryNames));
        Assert.True(InstalledAppsService.IsInstalledByIdentity("Example App", "Vendor.Unlisted", new HashSet<string>(),
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase), registryNames));
    }

    [Fact]
    public void IsInstalledRecognizesUniGetUiMicrosoftStoreIdForWingetCatalogEntry()
    {
        var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "XPFFTQ032PTPHF" };
        var idsByName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["UniGetUI"] = new(StringComparer.OrdinalIgnoreCase) { "XPFFTQ032PTPHF" }
        };

        Assert.True(InstalledAppsService.IsInstalledByIdentity(
            "UniGetUI", "Devolutions.UniGetUI", installedIds, idsByName,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void PackageIdentityRequiresBothIdAndSource()
    {
        Assert.True(PackageIdentityMatcher.SamePackage("Vendor.App", "winget", "vendor.app", "WinGet"));
        Assert.False(PackageIdentityMatcher.SamePackage("Vendor.App", "winget", "Vendor.App", "msstore"));
        Assert.False(PackageIdentityMatcher.SamePackage("Vendor.App", "winget", "Vendor.Other", "winget"));
    }
}
