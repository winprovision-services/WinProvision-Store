using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WingetRetryPolicyTests
{
    [Fact]
    public void DownloadFailureCanBeRetriedOnlyWithinBoundedBudget()
    {
        var failure = DownloadFailure();

        Assert.True(WingetExecutor.ShouldRetryTransientDownload(failure, 1, forceElevation: false, cancellationToken: default));
        Assert.True(WingetExecutor.ShouldRetryTransientDownload(failure, 2, forceElevation: false, cancellationToken: default));
        Assert.False(WingetExecutor.ShouldRetryTransientDownload(failure, 3, forceElevation: false, cancellationToken: default));
    }

    [Theory]
    [InlineData(unchecked((int)0x8A150011))] // hash mismatch
    [InlineData(unchecked((int)0x8A150010))] // no applicable installer
    [InlineData(unchecked((int)0x8A150019))] // elevation required
    public void NonTransientFailuresAreNotRetried(int exitCode)
    {
        var failure = new WingetExecutionResult { ExitCode = unchecked((int)exitCode) };

        Assert.False(WingetExecutor.ShouldRetryTransientDownload(failure, 1, forceElevation: false, cancellationToken: default));
    }

    [Fact]
    public void DownloadRetryIsBlockedAfterInstallerStartOrElevation()
    {
        var installerStarted = DownloadFailure();
        installerStarted.InstallerStarted = true;
        var elevated = DownloadFailure();
        elevated.WasElevated = true;

        Assert.False(WingetExecutor.ShouldRetryTransientDownload(installerStarted, 1, forceElevation: false, cancellationToken: default));
        Assert.False(WingetExecutor.ShouldRetryTransientDownload(elevated, 1, forceElevation: false, cancellationToken: default));
        Assert.False(WingetExecutor.ShouldRetryTransientDownload(DownloadFailure(), 1, forceElevation: true, cancellationToken: default));
    }

    [Fact]
    public void CancellationAndSuccessfulResultsBlockRetry()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var success = DownloadFailure();
        success.Success = true;

        Assert.False(WingetExecutor.ShouldRetryTransientDownload(DownloadFailure(), 1, forceElevation: false, cancellationToken: canceled.Token));
        Assert.False(WingetExecutor.ShouldRetryTransientDownload(success, 1, forceElevation: false, cancellationToken: default));
    }

    [Fact]
    public void RetryDelayIsShortAndIncreasesWithinBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), WingetExecutor.GetTransientDownloadRetryDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), WingetExecutor.GetTransientDownloadRetryDelay(2));
    }

    private static WingetExecutionResult DownloadFailure() => new()
    {
        ExitCode = unchecked((int)0x8A150008),
        FailureReason = WingetFailureReason.DownloadError
    };
}
