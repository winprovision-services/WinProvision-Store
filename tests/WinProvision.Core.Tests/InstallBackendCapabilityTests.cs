using WinProvision.Core.Models;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstallBackendCapabilityTests
{
    [Fact]
    public void AutomaticUsesCliForOptionsComCannotRepresent()
    {
        Assert.True(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(Version: "1.2.3")));
        Assert.True(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(Architecture: "x64")));
        Assert.True(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(CustomArguments: "/quiet")));
        Assert.True(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(IgnoreSecurityHash: true)));
    }

    [Fact]
    public void OptionsSupportedByComDoNotForceCli()
    {
        Assert.False(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(
            Interactive: true, Scope: "machine", InstallLocation: @"C:\Apps", RequiresElevation: true)));
    }

    [Fact]
    public void ApiFallbackUsesCliWhenItCannotPreserveInstallOptions()
    {
        Assert.True(WinGetService.RequiresWingetCliForApiInstallOptions(new PackageInstallOptions(Interactive: true)));
        Assert.True(WinGetService.RequiresWingetCliForApiInstallOptions(new PackageInstallOptions(InstallLocation: @"C:\Apps")));
        Assert.True(WinGetService.RequiresWingetCliForApiInstallOptions(new PackageInstallOptions(CustomArguments: "/quiet")));
        Assert.True(WinGetService.RequiresWingetCliForApiInstallOptions(new PackageInstallOptions(IgnoreSecurityHash: true)));
        Assert.False(WinGetService.RequiresWingetCliForApiInstallOptions(new PackageInstallOptions(
            Scope: "machine", Architecture: "x64", Version: "1.2.3", RequiresElevation: true)));
    }
}
