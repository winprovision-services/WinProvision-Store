using WinProvision.Core.Services;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class LocalIdentifierRetryTests
{
    [Fact]
    public void MissingInstalledPackageMayUseDistinctLocalIdentifierBeforeDispatch()
    {
        var result = new WingetExecutionResult { FailureReason = WingetFailureReason.NoPackageFound };
        Assert.True(WinGetService.CanRetryLocalIdentifier(result, "Contoso.App", "{A1B2C3}", default));
        Assert.False(WinGetService.CanRetryLocalIdentifier(result, "Contoso.App", "Contoso.App", default));
    }

    [Fact]
    public void InstallerStartElevationAndCancellationStopLocalRetry()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = new WingetExecutionResult { FailureReason = WingetFailureReason.PackageNotInCatalog };
        Assert.False(WinGetService.CanRetryLocalIdentifier(result, "Contoso.App", "local-id", cancellation.Token));
        result.InstallerStarted = true;
        Assert.False(WinGetService.CanRetryLocalIdentifier(result, "Contoso.App", "local-id", default));
        result.InstallerStarted = false;
        result.WasElevated = true;
        Assert.False(WinGetService.CanRetryLocalIdentifier(result, "Contoso.App", "local-id", default));
    }

    [Fact]
    public void UnrelatedFailureAndUnsafeIdentifierDoNotRetry()
    {
        var result = new WingetExecutionResult { FailureReason = WingetFailureReason.InstallError };
        Assert.False(WinGetService.CanRetryLocalIdentifier(result, "Contoso.App", "local-id", default));
        result.FailureReason = WingetFailureReason.NoPackageFound;
        Assert.False(WinGetService.CanRetryLocalIdentifier(result, "Contoso.App", "bad\nlocal", default));
    }
}
