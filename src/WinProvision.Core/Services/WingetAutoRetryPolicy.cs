using System;
using System.Collections.Generic;

namespace WinProvision.Core.Services;

/// <summary>Política conservadora de repetição automática para falhas transitórias do WinGet.</summary>
internal static class WingetAutoRetryPolicy
{
    public const int MaxRetriesPerOperation = 6;
    public const int MaxRetriesPerErrorCode = 6;
    private const int DownloadFailed = unchecked((int)0x8A150008);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Só permite repetir 0x8A150008 (APPINSTALLER_CLI_ERROR_DOWNLOAD_FAILED), antes do início
    /// do instalador e sem elevação, sucesso ou cancelamento. Os demais códigos já têm motivos
    /// específicos para a interface e exigem ação diferente: 0x8A150011 hash divergente;
    /// 0x8A150010 sem instalador aplicável; 0x8A150019/0x80073D28 elevação; 0x8A150005/0x8A15010C
    /// cancelamento; 0x8A15001B bloqueio por política; 0x8A150109/0x8A15010B reinicialização.
    /// Repeti-los cegamente pode ignorar a intenção do usuário, a política de segurança ou efeitos
    /// do instalador. Hash, falhas permanentes e falhas de catálogo sem garantia de transitoriedade
    /// não entram na lista elegível.
    /// </summary>
    public static bool CanRetry(
        WingetExecutionResult result,
        int totalRetries,
        int retriesForErrorCode,
        bool forceElevation,
        bool cancellationRequested)
    {
        ArgumentNullException.ThrowIfNull(result);
        return totalRetries < MaxRetriesPerOperation
            && retriesForErrorCode < MaxRetriesPerErrorCode
            && !result.Success
            && !result.WasElevated
            && !result.InstallerStarted
            && !forceElevation
            && !cancellationRequested
            && result.ExitCode == DownloadFailed
            && WingetErrorTranslator.Classify(result.ExitCode, result.Output) == WingetFailureReason.DownloadError;
    }

    /// <summary>Backoff exponencial curto, limitado a 30 segundos por tentativa.</summary>
    public static TimeSpan GetDelay(int retryNumber)
    {
        if (retryNumber is < 1 or > MaxRetriesPerOperation)
            throw new ArgumentOutOfRangeException(nameof(retryNumber));
        int seconds = Math.Min(1 << (retryNumber - 1), (int)MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(seconds);
    }
}
