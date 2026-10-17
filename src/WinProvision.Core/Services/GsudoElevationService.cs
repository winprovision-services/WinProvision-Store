using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WinProvision.Core.Services;

/// <summary>Host integration for the pinned WinProvision fork of gsudo.</summary>
public static class GsudoElevationService
{
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static bool _cacheStarted;
    public const int CacheMinutes = 5;
    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Tool", "gsudo", "WinProvision Elevator.exe");
    public static bool IsBundled => File.Exists(HelperPath);

    public static void VerifyHelper(string path, string expectedSha256)
    {
        using var file = File.OpenRead(path);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("O helper de elevação diverge da versão distribuída. Reinstale o WinProvision Store.");
    }

    private static void VerifyBundledHelper() => VerifyHelper(HelperPath, GsudoBinary.Sha256);

    public static async Task AuthorizeCacheAsync(CancellationToken token)
    {
        await EnsureCacheAsync(token).ConfigureAwait(false);
        WinProvisionElevationState.MarkAuthorized();
    }

    private static async Task EnsureCacheAsync(CancellationToken token)
    {
        await CacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int minutes = CacheMinutes;
            if (_cacheStarted)
            {
                var status = await RunControlAsync(["status", "CacheAvailable"], token).ConfigureAwait(false);
                if (status.ExitCode == 0 && status.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) return;
                _cacheStarted = false;
            }
            var result = await RunControlAsync(["cache", "on", "--pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                "--duration", TimeSpan.FromMinutes(minutes).ToString("c", CultureInfo.InvariantCulture)], token).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                WinProvisionElevationState.MarkFailed();
                throw new Win32Exception(1223, "A autorização do cache de elevação não foi concluída.");
            }
            _cacheStarted = true;
        }
        finally { CacheGate.Release(); }
    }

    public static async Task StopCacheAsync()
    {
        await CacheGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_cacheStarted) return;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await RunControlAsync(["cache", "off", "--pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)], timeout.Token).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new IOException("Não foi possível encerrar o cache de elevação.");
            _cacheStarted = false;
        }
        finally { CacheGate.Release(); }
    }

    public static async Task<int> RunElevatedAsync(string fileName, string arguments, CancellationToken token,
        Action? onStarted = null, Action<string>? onLog = null, string? workingDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        if (WinProvisionElevationState.HasFailedThisSession)
            throw new Win32Exception(1223, "A elevação anterior não foi concluída. Autorize a sessão novamente nas Configurações ou inicie uma nova execução da CLI.");
        VerifyBundledHelper();
        await EnsureCacheAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var start = CreateStartInfo();
        start.WorkingDirectory = workingDirectory ?? (Path.IsPathFullyQualified(fileName)
            ? Path.GetDirectoryName(fileName)! : Environment.CurrentDirectory);
        // Existing installer/WinGet builders supply a Windows command line. --direct
        // avoids interpreting it through PowerShell or cmd.exe.
        if (fileName.Contains('"') || fileName.Any(char.IsControl)) throw new ArgumentException("Executável inválido.", nameof(fileName));
        start.Arguments = $"--direct --loglevel Error \"{fileName}\" {arguments}";
        using var process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o helper de elevação.");
        onStarted?.Invoke();
        Task stdout = DrainAsync(process.StandardOutput, onLog);
        Task stderr = DrainAsync(process.StandardError, onLog);
        // Once dispatched, observe the real exit. Never cancel the wait and then
        // delete a running installer's files or dispatch a second backend.
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        if (process.ExitCode == 999) WinProvisionElevationState.MarkFailed();
        return process.ExitCode;
    }

    private static async Task DrainAsync(StreamReader reader, Action<string>? log)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            try { log?.Invoke(SensitiveDataRedactor.Redact(line)); }
            catch (Exception ex) { CoreLog.Warning(ex, "GsudoOutputCallback"); }
        }
    }

    private static ProcessStartInfo CreateStartInfo() => new(HelperPath)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
    };

    private static async Task<(int ExitCode, string Output)> RunControlAsync(string[] arguments, CancellationToken token)
    {
        VerifyBundledHelper();
        var start = CreateStartInfo();
        start.ArgumentList.Add("--loglevel"); start.ArgumentList.Add("Error");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar o helper de elevação.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill();
            await process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            throw;
        }
        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }
}
