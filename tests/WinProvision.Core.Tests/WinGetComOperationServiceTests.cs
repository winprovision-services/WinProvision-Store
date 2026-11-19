using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using WinProvision.Providers.WinGet.Com.Contracts;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinGetComOperationServiceTests
{
    [Theory]
    [InlineData(WinGetComStatus.PackageNotFound)]
    [InlineData(WinGetComStatus.CatalogOrSourceFailed)]
    [InlineData(WinGetComStatus.DownloadFailed)]
    [InlineData(WinGetComStatus.NoApplicableInstallers)]
    public void ProvenPreExecutionFailuresPermitFallback(WinGetComStatus status)
    {
        Assert.Equal(WinGetFactoryHelper.CliFallbackAllowed,
            WinGetComOperationService.CanFallback(new(status), installerStarted: false, forceComOnly: false, CancellationToken.None));
    }

    [Theory]
    [InlineData(WinGetComStatus.InstallerFailed)]
    [InlineData(WinGetComStatus.BlockedByPolicy)]
    [InlineData(WinGetComStatus.OutcomeUnknown)]
    [InlineData(WinGetComStatus.AccessDenied)]
    public void AmbiguousInstallerPolicyAndPermissionFailuresAreTerminal(WinGetComStatus status)
    {
        Assert.False(WinGetComOperationService.CanFallback(new(status), false, false, CancellationToken.None));
    }

    [Fact]
    public void DispatchEvidenceAndUnsafeCodesPreventFallback()
    {
        Assert.False(WinGetComOperationService.CanFallback(new(WinGetComStatus.DownloadFailed), true, false, CancellationToken.None));
        Assert.False(WinGetComOperationService.CanFallback(new(WinGetComStatus.NoApplicableInstallers, InstallerExitCode: 1603), false, false, CancellationToken.None));
        Assert.False(WinGetComOperationService.CanFallback(new(WinGetComStatus.NoApplicableInstallers, HResult: unchecked((int)0x800704C7)), false, false, CancellationToken.None));
        Assert.False(WinGetComOperationService.CanFallback(new(WinGetComStatus.DownloadFailed), false, true, CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.False(WinGetComOperationService.CanFallback(new(WinGetComStatus.DownloadFailed), false, false, cancellation.Token));
    }

    [Fact]
    public void InvalidOptionsAndDocumentedElevationCodesAllowPreExecutionFallback()
    {
        Assert.True(WinGetComOperationService.CanFallback(new(WinGetComStatus.InvalidOptions, HResult: unchecked((int)0x8A150002)), false, false, CancellationToken.None));
        Assert.Equal(WinGetFactoryHelper.CliFallbackAllowed, WinGetComOperationService.CanFallback(
            new(WinGetComStatus.InstallerFailed, InstallerExitCode: 740), false, false, CancellationToken.None));
        Assert.Equal(WinGetFactoryHelper.CliFallbackAllowed, WinGetComOperationService.CanFallback(
            new(WinGetComStatus.InstallerFailed, HResult: unchecked((int)0x800702E4)), false, false, CancellationToken.None));
        Assert.False(WinGetComOperationService.CanFallback(new(WinGetComStatus.InstallerFailed,
            InstallerExitCode: 740, ExtendedHResult: 1603), false, false, CancellationToken.None));
    }

}
