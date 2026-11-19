using System.ComponentModel;
using System.Diagnostics;

namespace WinProvision.Core.Services;

public sealed record LanWorkerSelfTestResult(
    bool Success,
    int ExitCode,
    string StandardOutput,
    string StandardError);

public static class LanWorkerSelfTest
{
    public const string SuccessMarker = "WINPROVISION_SELFTEST_OK";
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task<LanWorkerSelfTestResult> RunAsync(
        string workerPath,
        CancellationToken cancellationToken = default,
        TimeSpan? timeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerPath);
        string fullPath = Path.GetFullPath(workerPath);
        if (!File.Exists(fullPath))
            return new(false, 2, string.Empty, $"O worker não foi encontrado: {fullPath}");

        var startInfo = new ProcessStartInfo(fullPath)
        {
            WorkingDirectory = Path.GetDirectoryName(fullPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("--selftest");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start())
                return new(false, 1, string.Empty, "O Windows não iniciou o worker.");
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            int exitCode = exception is Win32Exception { NativeErrorCode: 193 }
                ? unchecked((int)0x800700C1)
                : exception.HResult;
            return new(false, exitCode, string.Empty, exception.ToString());
        }

        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string timeoutOutput = string.Empty;
            string timeoutError = string.Empty;
            if (TryKill(process))
            {
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    await Task.WhenAll(standardOutput, standardError).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    timeoutOutput = standardOutput.Result;
                    timeoutError = standardError.Result;
                }
                catch (TimeoutException exception)
                {
                    CoreLog.Warning(exception, "LanWorkerSelfTest.Timeout");
                }
            }
            return new(false, 1460, timeoutOutput,
                $"{timeoutError}{Environment.NewLine}O teste de inicialização excedeu o tempo limite.");
        }
        catch (OperationCanceledException)
        {
            if (TryKill(process))
            {
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    CoreLog.Warning(exception, "LanWorkerSelfTest.Cancel");
                }
            }
            throw;
        }

        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        string output = standardOutput.Result;
        string error = standardError.Result;
        bool success = process.ExitCode == 0 &&
            output.Contains(SuccessMarker, StringComparison.Ordinal);
        if (process.ExitCode == 0 && !success)
            error = $"{error}{Environment.NewLine}O worker não confirmou o teste de inicialização.";
        return new(success, process.ExitCode, output, error);
    }

    private static bool TryKill(Process process)
    {
        try
        {
            if (process.HasExited)
                return true;
            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            CoreLog.Warning(exception, "LanWorkerSelfTest.Kill");
            return false;
        }
    }
}
