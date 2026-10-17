using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace WinProvision.Core.Services;

/// <summary>Executa o programa diretamente via UAC e observa sua conclusão.</summary>
public static class ElevatedProcessRunner
{
    public static async Task<WingetExecutionResult> RunElevatedAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken,
        bool useWingetExitCodes = false)
    {
        var auditTimer = WingetCliAudit.Launch($"{fileName} (elevado)", arguments);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var captured = new System.Text.StringBuilder();
            int code = await GsudoElevationService.RunElevatedAsync(fileName, arguments, cancellationToken,
                onLog: line => { lock (captured) captured.AppendLine(line); }).ConfigureAwait(false);
            bool ok = useWingetExitCodes ? InstallerExitCodes.IsWingetSuccess(code) : code == 0;
            string details = captured.ToString();
            if (code == 999) details = "Elevação não concluída pelo helper. " + details;
            WingetCliAudit.Result($"{fileName} (gsudo)", code, ok, auditTimer, arguments);
            return new WingetExecutionResult
            {
                Success = ok, ExitCode = code, Output = details, WasElevated = true, InstallerStarted = true,
                FailureReason = code == 999 ? WingetFailureReason.ElevationCanceled
                    : ok ? WingetFailureReason.Unknown : WingetErrorTranslator.Classify(code, details)
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED — usuário clicou "Não" no UAC.
        {
            // Marca a sessão: próximas instalações (ver WinGetService.InstallAsync) pulam a
            // API COM e não tentam pedir UAC de novo — vão direto pra API própria da
            // WinProvision Store, já que o usuário demonstrou que não vai aprovar o prompt.
            WinProvisionElevationState.MarkFailed();
            WingetCliAudit.Result($"{fileName} (elevado)", 1223, success: false, auditTimer, arguments);
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = 1223,
                Output = "Elevação cancelada pelo usuário.",
                FailureReason = WingetFailureReason.ElevationCanceled,
                WasElevated = true,
            };
        }
        catch (Win32Exception ex)
        {
            CoreLog.Warning(ex, "StartElevatedProcess");
            return new WingetExecutionResult
            {
                ExitCode = ex.NativeErrorCode,
                Output = ex.Message,
                WasElevated = true,
                FailureReason = InstallerExitCodes.Classify(ex.NativeErrorCode),
            };
        }
    }
}
