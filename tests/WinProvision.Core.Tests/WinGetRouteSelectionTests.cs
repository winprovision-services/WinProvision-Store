using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using WinProvision.Providers.WinGet.Com.Contracts;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinGetRouteSelectionTests
{
    [Theory]
    [InlineData(WinGetEnginePreference.Automatic, PackageInstallMethod.Automatic, PackageUpdateMethod.Automatic)]
    [InlineData(WinGetEnginePreference.ComApi, PackageInstallMethod.ComApi, PackageUpdateMethod.ComApi)]
    [InlineData(WinGetEnginePreference.WingetCli, PackageInstallMethod.WingetCli, PackageUpdateMethod.WingetCli)]
    public void EnginePreferenceMapsToLegacyOperationEnumsForCompatibility(
        WinGetEnginePreference preference, PackageInstallMethod expectedInstall, PackageUpdateMethod expectedUpdate)
    {
        Assert.Equal(expectedInstall, preference.ToInstallMethod());
        Assert.Equal(expectedUpdate, preference.ToUpdateMethod());
    }

    [Theory]
    [InlineData("install", false, false, true)]
    [InlineData("install", true, false, false)]
    [InlineData("update", false, false, true)]
    [InlineData("update", true, false, false)]
    [InlineData("install", false, true, false)]
    [InlineData("update", false, true, false)]
    public void InstallAndUpdateFallbackRequiresProvenPreDispatchFailure(
        string operation, bool installerStarted, bool forceComOnly, bool expected)
    {
        Assert.Contains(operation, new[] { "install", "update" }); // Both operation kinds share the guarded COM path.
        var failure = new WinProvision.Providers.WinGet.Com.Contracts.WinGetComOperationResult(
            WinGetComStatus.CatalogOrSourceFailed);
        Assert.Equal(expected && WinGetFactoryHelper.CliFallbackAllowed,
            WinGetComOperationService.CanFallback(failure, installerStarted, forceComOnly, CancellationToken.None));
    }

}
