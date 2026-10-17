using System.Diagnostics;
using System.Text;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class DeferredAppUpdateTests
{
    [Fact]
    public async Task ChangedInstallerCannotSignalReadyOrCloseTheApplication()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinProvision-update-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string setup = Path.Combine(root, "setup '& nome.exe");
        string ready = Path.Combine(root, "ready");
        try
        {
            await File.WriteAllTextAsync(setup, "not-an-installer");
            string script = DeferredAppUpdate.InstallerScript(setup, new string('0', 64), ready, Path.Combine(root, "abort"), Environment.ProcessId);
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell/v1.0/powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
            start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
            using var process = Process.Start(start)!;
            Task<string> error = process.StandardError.ReadToEndAsync();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 20, await error + await output);
            Assert.False(File.Exists(ready));
            Assert.True(File.Exists(setup));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task CanceledUpdateCannotStartCoordinatorOrRequestElevation()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppUpdateService.ScheduleSilentInstallAndRestartAsync("missing.exe", cancellationToken: new(true)));
}
