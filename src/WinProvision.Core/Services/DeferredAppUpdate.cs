using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using WinProvision.Elevation;

namespace WinProvision.Core.Services;

public static class DeferredAppUpdate
{
    private const string CoordinatorName = "winprovision.exe";
    private const string WorkerCommand = "--deferred-update-worker";

    public static async Task PrepareAsync(string setupPath, string applicationName, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(setupPath)) throw new FileNotFoundException("Instalador não encontrado.", setupPath);
        if (string.IsNullOrWhiteSpace(applicationName) || Path.GetFileName(applicationName) != applicationName)
            throw new ArgumentException("Nome do executável inválido.", nameof(applicationName));

        string appPath = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, applicationName);
        string appDirectory = Path.GetDirectoryName(Path.GetFullPath(appPath))!;
        bool machineInstall = IsMachineInstallDirectory(appDirectory);
        string sourceSetup = Path.GetFullPath(setupPath);
        string sha256 = await ComputeSha256Async(sourceSetup, token).ConfigureAwait(false);
        string root = Path.Combine(Path.GetTempPath(), "WinProvisionUpdate", Guid.NewGuid().ToString("N"));
        string ready = Path.Combine(root, "ready");
        string abort = Path.Combine(root, "abort");
        string stagedSetup = Path.Combine(root, "setup.exe");
        string coordinator = Path.Combine(root, "cli", CoordinatorName);
        Directory.CreateDirectory(root);
        try
        {
            CopyDirectory(Path.Combine(AppContext.BaseDirectory, "cli"), Path.GetDirectoryName(coordinator)!);
            if (!File.Exists(coordinator)) throw new FileNotFoundException("Coordenador CLI não encontrado.", coordinator);

            string helperSource = ElevationRuntime.CacheManager.HelperPath;
            ElevationRuntime.CacheManager.VerifyHelperIntegrity();
            string helperPath = Path.Combine(root, "Tool", "gsudo", "WinProvision Elevator.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(helperPath)!);
            File.Copy(helperSource, helperPath);
            ElevationIntegrityVerifier.VerifySha256(helperPath, GsudoRunnerOptions.BundledDefault().ExpectedSha256);
            File.Copy(sourceSetup, stagedSetup);
            VerifySetupIntegrity(stagedSetup, sha256);

            var start = new ProcessStartInfo(coordinator)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = root
            };
            start.ArgumentList.Add(WorkerCommand);
            start.ArgumentList.Add(stagedSetup);
            start.ArgumentList.Add(sha256);
            start.ArgumentList.Add(ready);
            start.ArgumentList.Add(abort);
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            start.ArgumentList.Add(appPath);
            start.ArgumentList.Add(sourceSetup);
            start.ArgumentList.Add(machineInstall ? "1" : "0");
            using var process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o coordenador da atualização.");

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromMinutes(3));
                while (!File.Exists(ready))
                {
                    if (process.HasExited)
                        throw new IOException($"O coordenador da atualização encerrou antes de autorizar o instalador (código {process.ExitCode}). O aplicativo permanecerá aberto.");
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
        catch
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { CoreLog.Warning(exception, "DeferredUpdate.Cleanup"); }
            throw;
        }
    }

    /// <summary>Runs inside the unprivileged CLI coordinator. Only the installer child is elevated.</summary>
    public static async Task<int> RunWorkerAsync(string[] args, CancellationToken token)
    {
        if (args.Length != 9 || args[0] != WorkerCommand || args[8] is not ("0" or "1"))
            throw new ArgumentException("Argumentos inválidos para o coordenador da atualização.");
        string setupPath = Path.GetFullPath(args[1]);
        string expectedSha256 = args[2];
        string ready = Path.GetFullPath(args[3]);
        string abort = Path.GetFullPath(args[4]);
        if (!int.TryParse(args[5], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int parentPid))
            throw new ArgumentException("PID inválido no coordenador da atualização.");
        string applicationPath = Path.GetFullPath(args[6]);
        string sourceSetup = Path.GetFullPath(args[7]);
        bool machineInstall = args[8] == "1";
        string root = Path.GetDirectoryName(ready)!;
        if (!IsDirectChild(setupPath, root) || !IsDirectChild(ready, root) || !IsDirectChild(abort, root)
            || !File.Exists(setupPath) || !File.Exists(applicationPath))
            throw new InvalidDataException("Os caminhos do coordenador da atualização não são válidos.");

        if (machineInstall)
        {
            ElevationResult authorization = await ElevationRuntime.CacheManager.AuthorizeAsync(token).ConfigureAwait(false);
            if (!authorization.Success)
                return authorization.Status switch
                {
                    ElevationStatus.UacCancelled => 1223,
                    ElevationStatus.Cancelled => 1223,
                    _ => 1627
                };
        }
        VerifySetupIntegrity(setupPath, expectedSha256);
        await File.WriteAllTextAsync(ready, "ready", token).ConfigureAwait(false);

        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            using var waitToken = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
            while (IsProcessAlive(parentPid))
            {
                if (File.Exists(abort)) return 21;
                await Task.Delay(300, waitToken.Token).ConfigureAwait(false);
            }
            if (File.Exists(abort)) return 21;

            VerifySetupIntegrity(setupPath, expectedSha256);
            IReadOnlyList<string> arguments = BuildInstallerArguments(Path.GetDirectoryName(applicationPath)!, machineInstall);
            int exitCode;
            if (machineInstall)
            {
                WingetExecutionResult result = await ElevatedProcessRunner.RunElevatedAsync(setupPath, arguments,
                    token, ElevationCategory.AppUpdate, affectWingetElevationState: false,
                    workingDirectory: root, expectedSha256: expectedSha256).ConfigureAwait(false);
                if (!result.InstallerStarted)
                    return result.FailureReason == WingetFailureReason.ElevationCanceled ? 1223 : result.ExitCode;
                exitCode = result.ExitCode;
            }
            else
            {
                VerifySetupIntegrity(setupPath, expectedSha256);
                var start = new ProcessStartInfo(setupPath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root };
                foreach (string argument in arguments) start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o instalador da atualização.");
                await process.WaitForExitAsync().ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            if (exitCode is 0 or 3010)
            {
                var restart = new ProcessStartInfo(applicationPath)
                {
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(applicationPath)!
                };
                _ = Process.Start(restart) ?? throw new IOException("Não foi possível reabrir o WinProvision Store após atualizar.");
            }
            return exitCode;
        }
        finally
        {
            TryDelete(setupPath);
            TryDelete(sourceSetup);
            ScheduleCoordinatorCleanup(root);
        }
    }

    internal static IReadOnlyList<string> BuildInstallerArguments(string installDirectory, bool machineInstall)
    {
        var arguments = new List<string> { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-",
            machineInstall ? "/ALLUSERS" : "/CURRENTUSER" };
        arguments.Add("/DIR=" + installDirectory);
        return arguments.AsReadOnly();
    }

    internal static void VerifySetupIntegrity(string setupPath, string expectedSha256)
    {
        if (expectedSha256.Length != 64 || !expectedSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("O SHA-256 do instalador de atualização é inválido.");
        ElevationIntegrityVerifier.VerifySha256(setupPath, expectedSha256);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using Process process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static bool IsDirectChild(string path, string parent)
        => string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)), StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string source, string destination)
    {
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException("Diretório CLI do coordenador ausente: " + source);
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { CoreLog.Warning(exception, "DeferredUpdate.Cleanup"); }
    }

    private static void ScheduleCoordinatorCleanup(string root)
    {
        try
        {
            string script = "Wait-Process -Id " + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "; Start-Sleep -Milliseconds 500; Remove-Item -LiteralPath '" + root.Replace("'", "''") + "' -Recurse -Force -ErrorAction SilentlyContinue";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var cleanup = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            cleanup.ArgumentList.Add("-NoProfile"); cleanup.ArgumentList.Add("-NonInteractive"); cleanup.ArgumentList.Add("-EncodedCommand"); cleanup.ArgumentList.Add(encoded);
            _ = Process.Start(cleanup);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { CoreLog.Warning(exception, "DeferredUpdate.CoordinatorCleanup"); }
    }
}
