using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using Microsoft.Management.Deployment;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class UninstallFallbackTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("winget", true)]
    [InlineData("msstore", false)]
    [InlineData("MSSTORE", false)]
    public void WinProvisionUninstallDoesNotHandleMicrosoftStorePackages(string? source, bool expected)
    {
        Assert.Equal(expected, ApplicationEngine.CanUseWinProvisionUninstall(source));
    }

    [Theory]
    [InlineData(false, false, WingetFailureReason.InternalError, true)]
    [InlineData(false, false, WingetFailureReason.ElevationCanceled, false)]
    [InlineData(false, false, WingetFailureReason.BlockedByPolicy, false)]
    [InlineData(false, false, WingetFailureReason.OperationCanceled, false)]
    [InlineData(false, true, WingetFailureReason.InternalError, false)]
    [InlineData(true, false, WingetFailureReason.Unknown, false)]
    public void ComFailureFallsBackOnlyBeforeUninstallerStarts(
        bool success, bool installerStarted, WingetFailureReason reason, bool expected)
    {
        var result = new WingetExecutionResult
        {
            Success = success,
            InstallerStarted = installerStarted,
            FailureReason = reason
        };

        Assert.Equal(expected, ApplicationEngine.CanFallbackAfterComUninstall(result));
    }

    [Theory]
    [InlineData(UninstallResultStatus.InvalidOptions, 0, false)]
    [InlineData(UninstallResultStatus.CatalogError, 0, false)]
    [InlineData(UninstallResultStatus.ManifestError, 0, false)]
    [InlineData(UninstallResultStatus.BlockedByPolicy, 0, false)]
    [InlineData(UninstallResultStatus.UninstallError, unchecked((int)0x8A15002F), false)]
    [InlineData(UninstallResultStatus.UninstallError, unchecked((int)0x8A150030), true)]
    [InlineData(UninstallResultStatus.Ok, 0, true)]
    [InlineData(UninstallResultStatus.InternalError, 0, true)]
    public void ComStatusMarksOnlyProvenPreExecutionFailuresAsNotStarted(
        UninstallResultStatus status, int hresult, bool expected)
    {
        Assert.Equal(expected, WinGetService.WasComUninstallerDispatched(status, hresult));
    }

    [Theory]
    [InlineData(1603u, unchecked((int)0x8A15002F), 1603)]
    [InlineData(0u, unchecked((int)0x8A15002F), unchecked((int)0x8A15002F))]
    public void ComFailureCodePrefersUninstallerCodeAndFallsBackToExtendedHresult(
        uint uninstallerErrorCode, int extendedErrorCode, int expected)
    {
        Assert.Equal(expected, WinGetService.GetComUninstallFailureCode(uninstallerErrorCode, extendedErrorCode));
    }
}
