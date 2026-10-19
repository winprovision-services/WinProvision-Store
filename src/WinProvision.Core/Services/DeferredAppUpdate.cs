using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WinProvision.Core.Services;

internal static class DeferredAppUpdate
{
    private static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
    internal static string InstallerScript(string setup, string sha256, string ready, string abort, int parentPid,
        string? installDirectory = null, bool machineInstall = false)
        => $$"""
        $ErrorActionPreference = 'Stop'
        $env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
        $setup = {{Literal(setup)}}
        $ready = {{Literal(ready)}}
        $abort = {{Literal(abort)}}
        if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne {{Literal(sha256)}}) { exit 20 }
        [IO.File]::WriteAllText($ready, 'ready')
        $deadline = [DateTime]::UtcNow.AddMinutes(2)
        $parent = Get-Process -Id {{parentPid}} -ErrorAction SilentlyContinue
        while ($parent -and -not $parent.WaitForExit(500)) {
            if ((Test-Path -LiteralPath $abort) -or [DateTime]::UtcNow -gt $deadline) { exit 21 }
        }
        if (Test-Path -LiteralPath $abort) { exit 21 }
        if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne {{Literal(sha256)}}) { exit 20 }
        $arguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',{{Literal(machineInstall ? "/ALLUSERS" : "/CURRENTUSER")}})
        {{(installDirectory is null ? string.Empty : "$arguments += " + Literal("/DIR=\"" + installDirectory + "\""))}}
        $installer = Start-Process -FilePath $setup -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru
        exit $installer.ExitCode
        """;

    public static async Task PrepareAsync(string setupPath, string applicationName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(setupPath)) throw new FileNotFoundException("Instalador não encontrado.", setupPath);
        if (string.IsNullOrWhiteSpace(applicationName) || Path.GetFileName(applicationName) != applicationName)
            throw new ArgumentException("Nome do executável inválido.", nameof(applicationName));
        setupPath = Path.GetFullPath(setupPath);
        string application = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, applicationName);
        bool machineInstall = IsMachineInstallDirectory(Path.GetDirectoryName(application)!);
        if (machineInstall) GsudoElevationService.EnsureElevationAllowed();
        string root = Path.Combine(Path.GetTempPath(), "WinProvisionUpdate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string helper = Path.Combine(root, "WinProvision Elevator.exe");
        // A distinct verified path also isolates its gsudo pipe/cache from the app being replaced.
        GsudoElevationService.VerifyHelper(GsudoElevationService.HelperPath, GsudoBinary.Sha256);
        File.Copy(GsudoElevationService.HelperPath, helper);
        GsudoElevationService.VerifyHelper(helper, GsudoBinary.Sha256);
        string ready = Path.Combine(root, "ready");
        string abort = Path.Combine(root, "abort");
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        string sha256;
        await using (var stream = File.OpenRead(setupPath))
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
        string inner = Convert.ToBase64String(Encoding.Unicode.GetBytes(InstallerScript(setupPath, sha256, ready, abort,
            Environment.ProcessId, Path.GetDirectoryName(application), machineInstall)));
        string launch = machineInstall
            ? $"& {Literal(helper)} --direct --loglevel Error {Literal(powershell)} -NoProfile -NonInteractive -EncodedCommand {Literal(inner)}"
            : $"& {Literal(powershell)} -NoProfile -NonInteractive -EncodedCommand {Literal(inner)}";
        // Keep the coordinator at the initiating user's integrity. Only the installer payload is elevated.
        string outer = $$"""
            $ErrorActionPreference = 'Stop'
            $env:PSModulePath = [IO.Path]::Combine($PSHOME, 'Modules')
            try {
                {{launch}}
                $result = $LASTEXITCODE
                if ($result -in @(0, 3010)) { Start-Process -FilePath {{Literal(application)}} }
                exit $result
            } finally {
                if (Test-Path -LiteralPath {{Literal(ready)}}) { Remove-Item -LiteralPath {{Literal(setupPath)}} -Force -ErrorAction SilentlyContinue }
                Remove-Item -LiteralPath {{Literal(helper)}} -Force -ErrorAction SilentlyContinue
            }
            """;
        var start = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(outer)));
        using var process = Process.Start(start) ?? throw new IOException("Não foi possível preparar a atualização.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            while (!File.Exists(ready))
            {
                if (process.HasExited) throw new IOException("Atualização não autorizada ou não preparada pelo WinProvision Elevator. O aplicativo permanecerá aberto.");
                await Task.Delay(100, timeout.Token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
        }
        catch
        {
            await File.WriteAllTextAsync(abort, "abort", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsMachineInstallDirectory(string directory)
    {
        string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            if (!string.IsNullOrEmpty(root) && normalized.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using var hive = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{71B03A9D-2C64-4E81-BF6D-3A90257F1C49}_is1");
            if (key?.GetValue("InstallLocation") is string installed && string.Equals(normalized,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(installed)), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
