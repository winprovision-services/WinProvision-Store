using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using WinProvision.Providers.WinGet.Com.Contracts;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class UninstallFallbackTests
{
    [Theory]
    [InlineData(0, false, true)]
    [InlineData(3010, true, true)]
    [InlineData(1641, true, true)]
    [InlineData(3010, false, false)]
    [InlineData(1605, true, false)]
    [InlineData(1602, true, false)]
    [InlineData(1603, true, false)]
    public void UninstallExitCodesUseMsiSemanticsOnlyForMsi(int code, bool isMsi, bool success)
    {
        Assert.Equal(success, UninstallerEngineService.IsRegisteredUninstallSuccess(code, isMsi));
    }

    [Fact]
    public async Task RegisteredFallbackRejectsMissingExecutableWithoutStartingProcess()
    {
        var engine = new UninstallerEngineService();
        var result = await engine.RunRegisteredUninstallAsync(new InstalledAppDetail
        {
            QuietUninstallString = @"C:\WinProvision-Missing\uninstall.exe /quiet"
        });
        Assert.False(result.Success);
        Assert.False(result.InstallerStarted);
    }

    [Theory]
    [InlineData(WinGetComStatus.InvalidOptions, 0, false)]
    [InlineData(WinGetComStatus.CatalogOrSourceFailed, 0, false)]
    [InlineData(WinGetComStatus.ManifestFailed, 0, false)]
    [InlineData(WinGetComStatus.BlockedByPolicy, 0, false)]
    [InlineData(WinGetComStatus.InstallerFailed, unchecked((int)0x8A15002F), false)]
    [InlineData(WinGetComStatus.InstallerFailed, unchecked((int)0x8A150030), true)]
    [InlineData(WinGetComStatus.Success, 0, true)]
    [InlineData(WinGetComStatus.UnexpectedError, 0, false)]
    public void ComStatusMarksOnlyProvenPreExecutionFailuresAsNotStarted(
        WinGetComStatus status, int hresult, bool expected)
    {
        Assert.Equal(expected, WinGetComOperationService.WasComUninstallerDispatched(status, hresult));
    }

    [Theory]
    [InlineData(1603u, unchecked((int)0x8A15002F), 1603)]
    [InlineData(0u, unchecked((int)0x8A15002F), unchecked((int)0x8A15002F))]
    public void ComFailureCodePrefersUninstallerCodeAndFallsBackToExtendedHresult(
        uint uninstallerErrorCode, int extendedErrorCode, int expected)
    {
        Assert.Equal(expected, WinGetComOperationService.GetComUninstallFailureCode(uninstallerErrorCode, extendedErrorCode));
    }
}
