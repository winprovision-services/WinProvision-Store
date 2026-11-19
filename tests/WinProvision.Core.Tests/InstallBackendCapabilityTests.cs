using WinProvision.Core.Models;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstallBackendCapabilityTests
{
    [Fact]
    public void AutomaticUsesCliForOptionsComCannotRepresent()
    {
        Assert.False(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(Version: "1.2.3")));
        Assert.True(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(Architecture: "x64")));
        Assert.True(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(InstallerType: "msi")));
        Assert.True(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(Locale: "en-US")));
        Assert.True(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(CustomArguments: "/quiet")));
        Assert.True(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(IgnoreSecurityHash: true)));
    }

    [Fact]
    public void OptionsSupportedByComDoNotForceCli()
    {
        Assert.False(WinGetService.RequiresWingetCliForInstallOptions(new PackageInstallOptions(
            Interactive: true, Scope: "machine", InstallLocation: @"C:\Apps", RequiresElevation: true)));
    }

}
