using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class PackageInstallHookRunnerTests
{
    [Fact]
    public async Task ExplicitHookReportsSuccessAndFailureWithoutLoggingOutput()
    {
        Assert.True(await PackageInstallHookRunner.RunAsync("Write-Output 'private output'; exit 0", CancellationToken.None));
        Assert.False(await PackageInstallHookRunner.RunAsync("exit 7", CancellationToken.None));
    }

    [Fact]
    public async Task CancellationStopsHook()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PackageInstallHookRunner.RunAsync("Start-Sleep -Seconds 30", cancellation.Token));
    }
}
