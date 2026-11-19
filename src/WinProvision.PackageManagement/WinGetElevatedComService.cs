using System.Diagnostics;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Elevation;
using WinProvision.WinGet.Contracts;

namespace WinProvision.PackageManagement;

internal interface IWinGetComElevator
{
    Task<WingetExecutionResult> RunAsync(ComOperationKind operation, string packageId, string? installLocation,
        WinGetVariantSelectionOptions selection, Action<string>? log, Action<InstallProgressUpdate>? progress,
        CancellationToken token);
}

internal sealed class WinGetComElevatorAdapter : IWinGetComElevator
{
    public Task<WingetExecutionResult> RunAsync(ComOperationKind operation, string packageId, string? installLocation,
        WinGetVariantSelectionOptions selection, Action<string>? log, Action<InstallProgressUpdate>? progress,
        CancellationToken token) => WinGetElevatedComService.RunElevatedComWorkerAsync(operation, packageId,
            installLocation, log, progress, token, new ComAttemptState(), selection);
}

internal static class WinGetElevatedComService
{
    internal static async Task<WingetExecutionResult> RunElevatedComWorkerAsync(ComOperationKind operation,
        string packageId, string? installLocation, Action<string>? log, Action<InstallProgressUpdate>? progress,
        CancellationToken token, ComAttemptState state, WinGetVariantSelectionOptions? selection = null)
    {
        string worker = Path.Combine(AppContext.BaseDirectory, "cli", "winprovision.exe");
        if (!File.Exists(worker)) worker = Path.Combine(AppContext.BaseDirectory, "winprovision.exe");
        if (!File.Exists(worker)) throw new FileNotFoundException("Worker COM ausente. Reinstale o WinProvision Store.", worker);
        if (!await WorkerSupportsProtocolAsync(worker, token).ConfigureAwait(false))
            return new WingetExecutionResult
            {
                Success = false, ExitCode = -1, FailureReason = WingetFailureReason.InternalError,
                Output = "A CLI auxiliar não suporta o protocolo COM desta versão. Recompile ou reinstale o WinProvision Store."
            };
        string request = ElevatedComProtocol.EncodeRequest(new(operation switch { ComOperationKind.Install => "install", ComOperationKind.Upgrade => "update", _ => "uninstall" },
            packageId, installLocation, selection is null ? null : new(selection.Version, selection.Scope,
                selection.Architecture, selection.InstallerType, selection.Locale)));
        WingetExecutionResult? result = null;
        Exception? protocolError = null;
        int code;
        try
        {
            WingetExecutionResult elevated = await ElevatedProcessRunner.RunElevatedAsync(worker,
                [ElevatedComProtocol.Command, request], token, ElevationCategory.ComHost,
                onLogReceived: line =>
                {
                    try
                    {
                        if (line == ElevatedComProtocol.ReadyMarker)
                            state.Started = true;
                        else if (line.StartsWith(ElevatedComProtocol.ResultPrefix, StringComparison.Ordinal))
                            result = result is null ? ElevatedComProtocol.DecodeResult(line)
                                : throw new InvalidDataException("Resposta COM duplicada.");
                        else if (line.StartsWith(ElevatedComProtocol.ProgressPrefix, StringComparison.Ordinal))
                            progress?.Invoke(ElevatedComProtocol.DecodeProgress(line));
                        else log?.Invoke(line);
                    }
                    catch (Exception ex) { protocolError = ex; }
                }).ConfigureAwait(false);
            code = elevated.ExitCode;
            if (elevated.FailureReason == WingetFailureReason.ElevationCanceled)
            {
                return new WingetExecutionResult
                {
                    Success = false, ExitCode = 1223, WasElevated = true, InstallerStarted = state.Started,
                    FailureReason = state.Started ? WingetFailureReason.InternalError : WingetFailureReason.ElevationCanceled,
                    Output = state.Started ? "A conexão com o worker COM foi interrompida após inicializar; o resultado é incerto e não haverá repetição automática."
                        : "Elevação não concluída pelo WinProvision Elevator."
                };
            }
            if (!elevated.Success && !state.Started)
            {
                PackageDiagnostics.Write($"ELEVATED COM worker não iniciado: {elevated.FailureReason} {elevated.Output}");
                return new WingetExecutionResult
                {
                    Success = false, ExitCode = code, WasElevated = true, InstallerStarted = false,
                    FailureReason = elevated.FailureReason == WingetFailureReason.BlockedByPolicy ? WingetFailureReason.BlockedByPolicy : WingetFailureReason.InternalError,
                    Output = elevated.Output
                };
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // UAC recusado/cancelado é terminal. Após READY, o worker pode já ter despachado.
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = ex.NativeErrorCode,
                WasElevated = true,
                InstallerStarted = state.Started,
                FailureReason = state.Started ? WingetFailureReason.InternalError : WingetFailureReason.ElevationCanceled,
                Output = state.Started
                    ? "A conexão com o worker COM foi interrompida após inicializar; o resultado é incerto e não haverá repetição automática."
                    : "Elevação não concluída pelo WinProvision Elevator."
            };
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Política bloqueando, helper ausente/adulterado, sem área de trabalho interativa ou UAC sem resposta.
            // A falha do worker elevado é terminal, mesmo quando o pacote não foi despachado.
            PackageDiagnostics.Write($"ELEVATED COM worker não iniciado: 0x{ex.NativeErrorCode:X} {ex.Message}");
            return new WingetExecutionResult
            {
                Success = false, ExitCode = ex.NativeErrorCode, WasElevated = true, InstallerStarted = state.Started,
                FailureReason = ex.NativeErrorCode == 1625 ? WingetFailureReason.BlockedByPolicy : WingetFailureReason.InternalError,
                Output = state.Started
                    ? "A conexão com o worker COM foi interrompida após inicializar; o resultado é incerto e não haverá repetição automática."
                    : ex.Message
            };
        }
        if (code == 0 && result is not null && protocolError is null && state.Started)
        {
            result.WasElevated = true;
            state.InstallerStarted = result.InstallerStarted;
            return result;
        }
        PackageDiagnostics.Write($"ELEVATED COM worker exit={code} ready={state.Started} result={result is not null}");
        // The elevated worker may have dispatched the installer; never start another backend.
        return new WingetExecutionResult
        {
            Success = false, ExitCode = code == 0 ? -1 : code, WasElevated = true, InstallerStarted = state.Started,
            FailureReason = WingetFailureReason.InternalError,
            Output = state.Started
                ? "O worker COM não retornou um resultado válido após inicializar; a operação não será repetida automaticamente."
                : "O worker COM falhou antes de inicializar."
        };
    }

    private static async Task<bool> WorkerSupportsProtocolAsync(string worker, CancellationToken token)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(worker)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        process.StartInfo.ArgumentList.Add(ElevatedComProtocol.ProbeCommand);
        Task<string>? output = null;
        Task<string>? error = null;
        try
        {
            if (!process.Start()) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0
                && (await output.ConfigureAwait(false)).Trim() == ElevatedComProtocol.ReadyMarker
                && (await error.ConfigureAwait(false)).Length == 0;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
            try { if (output is not null) await output.ConfigureAwait(false); } catch (OperationCanceledException) { }
            try { if (error is not null) await error.ConfigureAwait(false); } catch (OperationCanceledException) { }
            token.ThrowIfCancellationRequested();
            return false;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            PackageDiagnostics.Warning(ex, "ElevatedComWorkerProbe");
            return false;
        }
    }

    internal static bool CanRetryComWithElevation(bool requiresElevation, bool installerStarted,
        bool blockedByPolicy, string source, PackageInstallOptions options,
        bool currentProcessElevated, bool inProcessEnabled, CancellationToken token) =>
        requiresElevation && !installerStarted && !blockedByPolicy && !token.IsCancellationRequested
        && !currentProcessElevated && !inProcessEnabled
        && source.Equals("winget", StringComparison.OrdinalIgnoreCase)
        && !options.RequiresWingetCli;

}









