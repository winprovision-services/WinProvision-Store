using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class DeferredAppUpdateTests
{
    [Fact]
    public async Task ChangedInstallerIsRejectedByTheCoordinatorHashCheck()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string setup = Path.Combine(root, "setup '& nome.exe");
        try
        {
            await File.WriteAllTextAsync(setup, "not-an-installer");
            Assert.Throws<InvalidDataException>(() => DeferredAppUpdate.VerifySetupIntegrity(setup, new string('0', 64)));
            Assert.True(File.Exists(setup));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InstallerArgumentsPreserveModeAndQuotedInstallDirectoryAsOneArgument()
    {
        IReadOnlyList<string> arguments = DeferredAppUpdate.BuildInstallerArguments(@"C:\Program Files\WinProvision", machineInstall: true);
        Assert.Contains("/ALLUSERS", arguments);
        Assert.Contains("/DIR=C:\\Program Files\\WinProvision", arguments);
        Assert.DoesNotContain("/CURRENTUSER", arguments);
    }

    [Fact]
    public async Task CanceledUpdateCannotStartCoordinatorOrRequestElevation()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppUpdateService.ScheduleSilentInstallAndRestartAsync("missing.exe", cancellationToken: new(true)));
}
