using WinProvision.Core.Services;
using WinProvision.Core.Models;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinGetApiUpdateFallbackTests
{
    [Theory]
    [InlineData("msi", "msi", true)]
    [InlineData("wix", "msi", true)]
    [InlineData("msi", "exe", false)]
    [InlineData("portable", "portable", false)]
    public void OwnApiUpdateInstallerPreservesInstalledTechnology(
        string installedType, string targetType, bool expected)
    {
        var installer = new CatalogInstaller { Type = targetType, Architecture = "x64", Scope = "machine", SilentSupported = true };
        var manifest = new PackageManifest { Installers = [installer] };
        var installed = new UpgradablePackage
        {
            Id = "Contoso.App", Name = "Contoso App", InstalledInstallerType = installedType,
            InstalledScope = "machine", InstalledArchitecture = "x64"
        };
        Assert.Equal(expected, WinProvisionApiService.PickUpdateInstaller(manifest, installed) is not null);
    }

    [Theory]
    [InlineData("user", "x64", "machine", "x64", false)]
    [InlineData("machine", "x86", "machine", "x64", false)]
    [InlineData("user", "x86", "user", "x86", true)]
    public void OwnApiUpdatePreservesScopeAndArchitecture(string scope, string architecture,
        string targetScope, string targetArchitecture, bool expected)
    {
        var manifest = new PackageManifest { Installers = [new CatalogInstaller
        {
            Type = "msi", Scope = targetScope, Architecture = targetArchitecture, SilentSupported = true
        }] };
        var installed = new UpgradablePackage
        {
            Id = "Contoso.App", Name = "Contoso App", InstalledScope = scope,
            InstalledArchitecture = architecture, InstalledInstallerType = "msi"
        };
        Assert.Equal(expected, WinProvisionApiService.PickUpdateInstaller(manifest, installed) is not null);
    }

    [Fact]
    public void ComFailureUsesExtendedHresultWhenInstallerDidNotReturnAnExitCode()
    {
        Assert.Equal(unchecked((int)0x8A150002), WinGetService.GetComFailureExitCode(0, unchecked((int)0x8A150002)));
        Assert.Equal(1603, WinGetService.GetComFailureExitCode(1603, unchecked((int)0x8A150002)));
        Assert.Equal(0, WinGetService.GetComFailureExitCode(0, 0));
    }

    [Theory]
    [InlineData(0, unchecked((int)0x8A150002), true)]
    [InlineData(0, unchecked((int)0x8A150010), true)]
    [InlineData(1603, unchecked((int)0x8A150010), false)]
    [InlineData(0, unchecked((int)0x8A150049), false)]
    public void ComFallbackRequiresProvenPreExecutionFailure(
        int installerCode, int extendedCode, bool expected) =>
        Assert.Equal(expected, WinGetService.IsPreExecutionComFailure(installerCode, extendedCode));

    [Theory]
    [InlineData(WinProvisionInstallOutcome.DownloadFailed, false, true)]
    [InlineData(WinProvisionInstallOutcome.InstallProcessFailed, false, true)]
    [InlineData(WinProvisionInstallOutcome.InstallProcessFailed, true, false)]
    [InlineData(WinProvisionInstallOutcome.Success, false, false)]
    [InlineData(WinProvisionInstallOutcome.ElevationCanceled, false, false)]
    public void FallbackOnlyRunsWhenOwnApiDidNotStartInstaller(
        WinProvisionInstallOutcome outcome, bool installerStarted, bool expected)
    {
        var result = new WinProvisionInstallResult(outcome, InstallerStarted: installerStarted);

        Assert.Equal(expected, WinGetService.CanFallbackAfterApiUpdate(result));
    }
}
