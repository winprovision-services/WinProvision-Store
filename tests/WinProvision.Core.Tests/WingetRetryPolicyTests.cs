using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WingetRetryPolicyTests
{
    [Fact]
    public void DownloadFailureCanBeRetriedOnlyWithinBoundedBudget()
    {
        var failure = DownloadFailure();

        Assert.True(WingetAutoRetryPolicy.CanRetry(failure, 0, 0, forceElevation: false, cancellationRequested: false));
        Assert.True(WingetAutoRetryPolicy.CanRetry(failure, 5, 5, forceElevation: false, cancellationRequested: false));
        Assert.False(WingetAutoRetryPolicy.CanRetry(failure, 6, 5, forceElevation: false, cancellationRequested: false));
        Assert.False(WingetAutoRetryPolicy.CanRetry(failure, 5, 6, forceElevation: false, cancellationRequested: false));
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150011), WingetFailureReason.InstallerHashMismatch)]
    [InlineData(unchecked((int)0x8A150010), WingetFailureReason.NoApplicableInstallers)]
    [InlineData(unchecked((int)0x8A150019), WingetFailureReason.ElevationRequired)]
    [InlineData(unchecked((int)0x80073D28), WingetFailureReason.ElevationRequired)]
    [InlineData(unchecked((int)0x8A150005), WingetFailureReason.OperationCanceled)]
    [InlineData(unchecked((int)0x8A15010C), WingetFailureReason.OperationCanceled)]
    [InlineData(unchecked((int)0x8A150077), WingetFailureReason.OperationCanceled)]
    [InlineData(unchecked((int)0x8A15002B), WingetFailureReason.NoApplicableUpgrade)]
    [InlineData(unchecked((int)0x8A150002), WingetFailureReason.InternalError)]
    [InlineData(unchecked((int)0x8A150108), WingetFailureReason.InstallError)]
    [InlineData(unchecked((int)0x8A15001B), WingetFailureReason.BlockedByPolicy)]
    [InlineData(unchecked((int)0x8A150109), WingetFailureReason.Unknown)] // sucesso com reinicialização
    [InlineData(unchecked((int)0x8A15010B), WingetFailureReason.Unknown)] // sucesso e reinicialização iniciada
    public void SpecificWingetCodesKeepTheirMeaningAndAreNotRetried(int exitCode, WingetFailureReason expectedReason)
    {
        var failure = new WingetExecutionResult { ExitCode = unchecked((int)exitCode) };

        Assert.Equal(expectedReason, WingetErrorTranslator.Classify(exitCode, failure.Output));
        Assert.False(WingetAutoRetryPolicy.CanRetry(failure, 0, 0, forceElevation: false, cancellationRequested: false));
    }

    [Fact]
    public void AmbiguousNoApplicationsCodeUsesOutputAndUnknownCodesStayUnknown()
    {
        Assert.Equal(WingetFailureReason.NoPackageFound,
            WingetErrorTranslator.Classify(unchecked((int)0x8A150014), "No installed package found matching input criteria."));
        Assert.Equal(WingetFailureReason.PackageNotInCatalog,
            WingetErrorTranslator.Classify(unchecked((int)0x8A150014), string.Empty));
        Assert.Equal(WingetFailureReason.Unknown,
            InstallerExitCodes.Classify(unchecked((int)0x8A150012)));
        Assert.Equal(WingetFailureReason.Unknown,
            WingetErrorTranslator.Classify(unchecked((int)0x8A150109), string.Empty));
    }

    [Fact]
    public void DownloadRetryIsBlockedAfterInstallerStartOrElevation()
    {
        var installerStarted = DownloadFailure();
        installerStarted.InstallerStarted = true;
        var elevated = DownloadFailure();
        elevated.WasElevated = true;

        Assert.False(WingetAutoRetryPolicy.CanRetry(installerStarted, 0, 0, forceElevation: false, cancellationRequested: false));
        Assert.False(WingetAutoRetryPolicy.CanRetry(elevated, 0, 0, forceElevation: false, cancellationRequested: false));
        Assert.False(WingetAutoRetryPolicy.CanRetry(DownloadFailure(), 0, 0, forceElevation: true, cancellationRequested: false));
    }

    [Fact]
    public void CancellationAndSuccessfulResultsBlockRetry()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var success = DownloadFailure();
        success.Success = true;

        Assert.False(WingetAutoRetryPolicy.CanRetry(DownloadFailure(), 0, 0, forceElevation: false, cancellationRequested: canceled.IsCancellationRequested));
        Assert.False(WingetAutoRetryPolicy.CanRetry(success, 0, 0, forceElevation: false, cancellationRequested: false));
    }

    [Fact]
    public void RetryDelayIsShortAndIncreasesWithinBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), WingetAutoRetryPolicy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), WingetAutoRetryPolicy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(30), WingetAutoRetryPolicy.GetDelay(6));
    }

    private static WingetExecutionResult DownloadFailure() => new()
    {
        ExitCode = unchecked((int)0x8A150008),
        FailureReason = WingetFailureReason.DownloadError
    };
}
