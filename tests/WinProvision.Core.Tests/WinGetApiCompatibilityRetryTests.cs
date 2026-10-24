using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinGetApiCompatibilityRetryTests
{
    [Fact]
    public void RetriesWithoutArchitectureAndScopeOnlyForPreDispatchCompatibilityFailure()
    {
        var result = new WinProvisionInstallResult(WinProvisionInstallOutcome.NoCompatibleInstaller);

        Assert.True(WinGetService.ShouldRetryApiWithoutInstallerConstraints(
            result, hasConstraints: true, alreadyRetried: false, token: CancellationToken.None));
        Assert.False(WinGetService.ShouldRetryApiWithoutInstallerConstraints(
            result, hasConstraints: false, alreadyRetried: false, token: CancellationToken.None));
        Assert.False(WinGetService.ShouldRetryApiWithoutInstallerConstraints(
            result, hasConstraints: true, alreadyRetried: true, token: CancellationToken.None));
    }

    [Theory]
    [InlineData(WinProvisionInstallOutcome.InstallProcessFailed, false)]
    [InlineData(WinProvisionInstallOutcome.NoCompatibleInstaller, true)]
    [InlineData(WinProvisionInstallOutcome.ElevationCanceled, false)]
    public void DoesNotRetrySemanticFailureOrAnyStartedInstaller(
        WinProvisionInstallOutcome outcome, bool installerStarted)
    {
        var result = new WinProvisionInstallResult(outcome, InstallerStarted: installerStarted);

        Assert.False(WinGetService.ShouldRetryApiWithoutInstallerConstraints(
            result, hasConstraints: true, alreadyRetried: false, token: CancellationToken.None));
    }

    [Fact]
    public void DoesNotRetryWhenCallerCanceled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.False(WinGetService.ShouldRetryApiWithoutInstallerConstraints(
            new WinProvisionInstallResult(WinProvisionInstallOutcome.NoCompatibleInstaller),
            hasConstraints: true, alreadyRetried: false, token: cancellation.Token));
    }
}
