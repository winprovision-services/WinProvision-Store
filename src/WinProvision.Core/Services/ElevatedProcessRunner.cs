using System.Diagnostics;
using WinProvision.Core.Models;
using WinProvision.Elevation;

namespace WinProvision.Core.Services;

/// <summary>Adapta o contrato único de elevação ao resultado usado pelos fluxos de pacotes.</summary>
public static class ElevatedProcessRunner
{
    public static async Task<WingetExecutionResult> RunElevatedAsync(string fileName,
        IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        ElevationCategory category = ElevationCategory.Other, bool useWingetExitCodes = false,
        bool affectWingetElevationState = true, Action<string>? onLogReceived = null,
        Action? onStarted = null, Action<string, string>? onOutput = null,
        string? workingDirectory = null, string? expectedSha256 = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        var progress = new CallbackProgress(onLogReceived, onStarted, onOutput);
        ElevatedCommand command = new(fileName, arguments, category, workingDirectory, expectedSha256);
        string displayArguments = CommandLineDisplayFormatter.FormatPowerShellArguments(arguments);
        Stopwatch auditTimer = WingetCliAudit.Launch($"{fileName} (elevado)", displayArguments);
        ElevationResult result = await ElevationRuntime.Current.RunAsync(command, progress, cancellationToken).ConfigureAwait(false);
        bool wasDispatched = progress.Started;
        int? code = result.ExitCode;
        bool success = result.Status == ElevationStatus.Success
            && (useWingetExitCodes ? InstallerExitCodes.IsWingetSuccess(code ?? 0) : code == 0);

        if (result.Status == ElevationStatus.UacCancelled && affectWingetElevationState)
            WinProvisionElevationState.MarkFailed();
        if (result.Status is ElevationStatus.UacCancelled or ElevationStatus.InvalidCredentials or ElevationStatus.HelperError
            && affectWingetElevationState)
            WinProvisionElevationState.MarkFailed();

        int failureCode = result.Status switch
        {
            ElevationStatus.UacCancelled => 1223,
            ElevationStatus.InvalidCredentials => 1326,
            ElevationStatus.Rejected => 1625,
            ElevationStatus.Cancelled => 1223,
            ElevationStatus.HelperError => 1627,
            _ => code ?? 1
        };
        string output = JoinOutput(result.StandardOutput, result.StandardError, result.Error);
        WingetCliAudit.Result($"{fileName} (elevado)", code ?? failureCode, success, auditTimer, displayArguments);
        return new WingetExecutionResult
        {
            Success = success,
            ExitCode = code ?? failureCode,
            Output = output,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            WasElevated = !ElevationRunner.IsCurrentProcessElevated(),
            InstallerStarted = wasDispatched,
            FailureReason = success ? WingetFailureReason.Unknown : result.Status switch
            {
                ElevationStatus.UacCancelled or ElevationStatus.Cancelled => WingetFailureReason.ElevationCanceled,
                ElevationStatus.Rejected => WingetFailureReason.BlockedByPolicy,
                ElevationStatus.InvalidCredentials or ElevationStatus.HelperError => WingetFailureReason.InternalError,
                _ => WingetErrorTranslator.Classify(code ?? failureCode, output)
            }
        };
    }

    private static string JoinOutput(string stdout, string stderr, string? error)
    {
        var parts = new[] { stdout, stderr, error ?? string.Empty }.Where(value => !string.IsNullOrWhiteSpace(value));
        return string.Join(Environment.NewLine, parts);
    }

    private sealed class CallbackProgress(Action<string>? log, Action? started, Action<string, string>? output)
        : IElevationDispatchProgress
    {
        private int _started;
        public bool Started => Volatile.Read(ref _started) != 0;
        public void OnStarted()
        {
            Interlocked.Exchange(ref _started, 1);
            try { started?.Invoke(); } catch { }
        }
        public void Report(string value)
        {
            try { log?.Invoke(value); } catch { }
            if (value.StartsWith("[stderr] ", StringComparison.Ordinal))
                output?.Invoke("stderr", value[9..]);
            else output?.Invoke("stdout", value);
        }
    }
}
