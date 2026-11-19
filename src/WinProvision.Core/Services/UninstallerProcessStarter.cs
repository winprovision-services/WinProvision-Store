using System.Diagnostics;
using WinProvision.Core.Models;
using WinProvision.Elevation;

namespace WinProvision.Core.Services;

public interface IUninstallerProcessStarter
{
    Task<int> StartDirectAsync(string fileName, string arguments, CancellationToken token, Action? onStarted = null,
        bool createNoWindow = false, bool hidden = false);
    Task<int> StartElevatedAsync(string fileName, string arguments, CancellationToken token, Action? onStarted = null);
}

public sealed class UninstallerProcessStarter : IUninstallerProcessStarter
{
    public async Task<int> StartDirectAsync(string fileName, string arguments, CancellationToken token, Action? onStarted = null,
        bool createNoWindow = false, bool hidden = false)
    {
        var start = new ProcessStartInfo { FileName = fileName, Arguments = arguments, UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(fileName)!, CreateNoWindow = createNoWindow,
            WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Process.Start retornou null");
        onStarted?.Invoke();
        await process.WaitForExitAsync(token).ConfigureAwait(false);
        return process.ExitCode;
    }

    public async Task<int> StartElevatedAsync(string fileName, string arguments, CancellationToken token, Action? onStarted = null)
    {
        var result = await ElevatedProcessRunner.RunElevatedAsync(fileName, WindowsArgumentParser.Parse(arguments), token,
            ElevationCategory.Uninstall, onStarted: onStarted).ConfigureAwait(false);
        if (!result.InstallerStarted) throw new System.ComponentModel.Win32Exception(result.ExitCode, result.Output);
        return result.ExitCode;
    }
}
