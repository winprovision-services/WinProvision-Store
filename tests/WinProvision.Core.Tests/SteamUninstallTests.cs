using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class SteamUninstallTests
{
    [Theory]
    [InlineData("\"C:\\Program Files (x86)\\Steam\\steam.exe\" steam://uninstall/3240220", true)]
    [InlineData("steam.exe -uninstall 3240220", true)]
    [InlineData("steam.exe steam://uninstall/3240220 & cmd.exe", false)]
    [InlineData("steam.exe steam://uninstall/0", false)]
    [InlineData("uninstall.exe /silent", false)]
    public void RecognizesOnlyGameRemovalCommands(string command, bool expected) =>
        Assert.Equal(expected, SteamUninstallService.IsGameUninstall(command));

    [Fact]
    public async Task CompletesOnlyAfterGameRegistrationDisappears()
    {
        int reads = 0;
        Assert.True(await SteamUninstallService.WaitForRemovalAsync(() => ++reads < 3,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), CancellationToken.None));
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task StillInstalledDoesNotReportSuccess()
    {
        Assert.False(await SteamUninstallService.WaitForRemovalAsync(() => true,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(1), CancellationToken.None));
    }

    [Fact]
    public async Task OperationCancelInterruptsMonitoring()
    {
        using var operation = new WinProvision.Core.Models.OperationItem("Jogo", WinProvision.Core.Models.OperationKind.Uninstall);
        var waiting = SteamUninstallService.WaitForRemovalAsync(() => true,
            TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(10), operation.CancellationTokenSource.Token);
        operation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task RegistryFailureDoesNotReportRemoval() =>
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => SteamUninstallService.WaitForRemovalAsync(
            () => throw new UnauthorizedAccessException(), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1), CancellationToken.None));
}
