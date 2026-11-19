using System.Diagnostics;
using WinProvision.Core.Models;
using WinProvision.Elevation;

namespace WinProvision.Core.Services;

public interface IInstallerProcessStarter
{
    Task<int> StartDirectAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token, Action? onStarted = null);
    Task<int> StartElevatedAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token,
        Action? onStarted = null, Action<string>? onLog = null, string? expectedSha256 = null);
}

public sealed class InstallerProcessStarter : IInstallerProcessStarter
{
    public async Task<int> StartDirectAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token, Action? onStarted = null)
    {
        token.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo { FileName = fileName, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) psi.ArgumentList.Add(argument);
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Não foi possível iniciar o instalador.");
        onStarted?.Invoke();
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    public async Task<int> StartElevatedAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken token,
        Action? onStarted = null, Action<string>? onLog = null, string? expectedSha256 = null)
    {
        onLog?.Invoke("A elevação deste instalador será feita pelo WinProvision Elevator.");
        WingetExecutionResult result = await ElevatedProcessRunner.RunElevatedAsync(fileName, arguments, token,
            ElevationCategory.DownloadedInstaller, onLogReceived: onLog, onStarted: onStarted,
            expectedSha256: expectedSha256).ConfigureAwait(false);
        if (!result.Success && !result.InstallerStarted)
            throw new System.ComponentModel.Win32Exception(result.ExitCode, result.Output);
        return result.ExitCode;
    }
}
