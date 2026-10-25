using System.Diagnostics;
using System.Text;

namespace WinProvision.Core.Services;

/// <summary>Runs explicitly enabled, user-authored package hooks in the current user context.</summary>
public static class PackageInstallHookRunner
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);
    private const int MaxScriptLength = 32_768;

    public static async Task<bool> RunAsync(string? script, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(script)) return true;
        if (Encoding.UTF8.GetByteCount(script) > MaxScriptLength) return false;
        cancellationToken.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "-" }
        };

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }

        // Drain output without retaining it: hook output can contain secrets and is never
        // copied into operation logs or history.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            Task drainOutput = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, 1024);
            Task drainError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, 1024);
            await process.StandardInput.WriteAsync(($"$ErrorActionPreference = 'Stop';{Environment.NewLine}").AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.WriteAsync(script.AsMemory(), timeout.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(drainOutput, drainError).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception waitException) when (waitException is InvalidOperationException or TimeoutException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return false;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception cleanupException) when (cleanupException is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (Exception waitException) when (waitException is InvalidOperationException or TimeoutException) { }
            return false;
        }
    }

    public static WingetExecutionResult Failure(string stage) => new()
    {
        Success = false,
        ExitCode = 1,
        InstallHookFailed = true,
        InstallerStarted = stage == "pós-instalação",
        Output = stage == "pré-instalação"
            ? "O script de pré-instalação falhou ou excedeu o limite de cinco minutos; o instalador não foi iniciado."
            : "A instalação terminou, mas o script de pós-instalação falhou ou excedeu o limite de cinco minutos. A instalação não será repetida automaticamente."
    };
}
