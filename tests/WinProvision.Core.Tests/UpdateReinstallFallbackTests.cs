using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class UpdateReinstallFallbackTests
{
    [Theory]
    [InlineData(unchecked((int)0x8A150010), "winget", "2.7.14", false, true)]
    [InlineData(unchecked((int)0x8A15002B), "winget", "2.7.14", false, true)]
    [InlineData(unchecked((int)0x8A15004F), "winget", "2.7.14", false, true)]
    [InlineData(unchecked((int)0x8A150011), "winget", "2.7.14", false, false)]
    [InlineData(1603, "winget", "2.7.14", false, false)]
    [InlineData(unchecked((int)0x8A150010), "msstore", "2.7.14", false, false)]
    [InlineData(unchecked((int)0x8A150010), "winget", null, false, false)]
    [InlineData(unchecked((int)0x8A150010), "winget", "2.7.14", true, false)]
    public void ReinstallOnlyFollowsPreExecutionWingetUpgradeFailure(
        int code, string source, string? target, bool canceled, bool expected)
    {
        var failure = new WingetExecutionResult { ExitCode = code };
        Assert.Equal(expected, WingetExecutor.CanRepairUpdateWithInstall(failure, source, target, canceled));
        failure.InstallerStarted = true;
        Assert.False(WingetExecutor.CanRepairUpdateWithInstall(failure, source, target, canceled));
    }

    [Fact]
    public void ReinstallRequiresAnInstalledVersionBelowTarget()
    {
        var installed = new[]
        {
            new UpgradablePackage { Id = "Microsoft.WSL", Name = "WSL", CurrentVersion = "2.7.13.0" },
            new UpgradablePackage { Id = "Microsoft.PowerShell", Name = "PowerShell", CurrentVersion = "7.6.8.0" }
        };
        Assert.True(WingetExecutor.HasOlderInstalledVersion(installed, "Microsoft.WSL", "2.7.14"));
        Assert.False(WingetExecutor.HasOlderInstalledVersion(installed, "Microsoft.WSL", "2.7.13.0"));
        Assert.False(WingetExecutor.HasOlderInstalledVersion(installed, "Microsoft.PowerShell", "7.6.8"));
        Assert.False(WingetExecutor.HasOlderInstalledVersion(installed, "Missing.Package", "2.7.14"));
        Assert.False(WingetExecutor.HasOlderInstalledVersion(
            (UpgradablePackage[])[.. installed, new UpgradablePackage { Id = "Microsoft.WSL", Name = "WSL antigo", CurrentVersion = "2.7.12" }],
            "Microsoft.WSL", "2.7.14"));
    }
}
