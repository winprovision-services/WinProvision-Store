using WinProvision.Core.Models;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class DynamicComElevationTests
{
    private static bool Eligible(bool required = true, bool started = false, bool policyBlocked = false,
        string source = "winget", PackageInstallOptions? options = null, bool elevated = false,
        bool inProcess = false, CancellationToken token = default) =>
        WinGetService.CanRetryComWithElevation(required, started, policyBlocked, source,
            options ?? new PackageInstallOptions(), elevated, inProcess, token);

    [Fact]
    public void RetriesPermissionFailureBeforeInstallerStarts() => Assert.True(Eligible());

    [Fact]
    public void DoesNotRetryAfterInstallerStartsOrPolicyBlocks()
    {
        Assert.False(Eligible(started: true));
        Assert.False(Eligible(policyBlocked: true));
        Assert.False(Eligible(required: false));
    }

    [Fact]
    public void KeepsUnsupportedWorkerOptionsAndSourcesOutOfRetry()
    {
        Assert.False(Eligible(source: "msstore"));
        Assert.False(Eligible(options: new PackageInstallOptions(Version: "1.2.3")));
        Assert.False(Eligible(elevated: true));
        Assert.False(Eligible(inProcess: true));
    }

    [Fact]
    public void CancellationStopsRetry()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.False(Eligible(token: source.Token));
    }
}
