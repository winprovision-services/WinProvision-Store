using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Executa o setup.exe do ODT (Office Deployment Tool) com configuração XML,
/// resolvendo caminhos absolutos e definindo explicitamente o WorkingDirectory
/// para evitar problemas com o diretório de trabalho do processo pai.
/// </summary>
public static class OdtProcessRunner
{
    /// <summary>Executa setup.exe /download e aguarda o ODT concluir a preparação local.</summary>
    public static async Task<int> RunDownloadAsync(
        string setupExePath, string configurationXmlPath, Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var absoluteSetupPath = Path.GetFullPath(setupExePath);
        var absoluteXmlPath = Path.GetFullPath(configurationXmlPath);
        if (!File.Exists(absoluteSetupPath))
            throw new FileNotFoundException("setup.exe não encontrado.", absoluteSetupPath);
        if (!File.Exists(absoluteXmlPath))
            throw new FileNotFoundException("configuration.xml não encontrado.", absoluteXmlPath);

        var startInfo = new ProcessStartInfo(absoluteSetupPath, $"/download \"{absoluteXmlPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(absoluteSetupPath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        ct.ThrowIfCancellationRequested();
        Process? started;
        try { started = Process.Start(startInfo); }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 740)
        {
            onStatus?.Invoke("O ODT solicita permissão administrativa para preparar os arquivos.");
            var elevated = await RunElevatedAsync(absoluteSetupPath, absoluteXmlPath, "/download", ct).ConfigureAwait(false);
            if (elevated.ElevationCanceled) throw new OfficeElevationCanceledException(ct);
            return elevated.ExitCode;
        }
        using var process = started ?? throw new InvalidOperationException("Falha ao iniciar o download do Office Deployment Tool.");
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onStatus?.Invoke(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) onStatus?.Invoke(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                CoreLog.Warning(exception, "OdtProcessRunner");
            }
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Executa setup.exe /configure com o configuration.xml especificado.
    /// Usa caminhos absolutos e define o WorkingDirectory para a pasta do setup.exe,
    /// eliminando dependência do cwd do processo pai e resolvendo problemas de
    /// elevação que resetam o cwd para System32.
    /// </summary>
    public static async Task<int> RunConfigureAsync(
        string setupExePath, string configurationXmlPath, CancellationToken ct = default)
    {
        var absoluteSetupPath = Path.GetFullPath(setupExePath);
        var absoluteXmlPath = Path.GetFullPath(configurationXmlPath);

        if (!File.Exists(absoluteSetupPath))
            throw new FileNotFoundException("setup.exe não encontrado.", absoluteSetupPath);

        if (!File.Exists(absoluteXmlPath))
            throw new FileNotFoundException("configuration.xml não encontrado.", absoluteXmlPath);

        var startInfo = new ProcessStartInfo(absoluteSetupPath, $"/configure \"{absoluteXmlPath}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(absoluteSetupPath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        ct.ThrowIfCancellationRequested();
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Falha ao iniciar o setup.exe.");

        // Once configuration starts, observe completion rather than abandon an installer.
        await process.WaitForExitAsync().ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// Executa setup.exe /configure pelo WinProvision Elevator, sem elevar a Store.
    /// </summary>
    public static Task<OdtProcessResult> RunConfigureElevatedAsync(
        string setupExePath, string configurationXmlPath, CancellationToken ct = default)
        => RunElevatedAsync(setupExePath, configurationXmlPath, "/configure", ct);

    private static async Task<OdtProcessResult> RunElevatedAsync(
        string setupExePath, string configurationXmlPath, string mode, CancellationToken ct)
    {
        var absoluteSetupPath = Path.GetFullPath(setupExePath);
        var absoluteXmlPath = Path.GetFullPath(configurationXmlPath);
        var workingDir = Path.GetDirectoryName(absoluteSetupPath) ?? string.Empty;

        if (!File.Exists(absoluteSetupPath))
            return new OdtProcessResult
            {
                Success = false,
                ExitCode = -1,
                Output = "setup.exe não encontrado.",
                ProcessStarted = false,
            };

        if (!File.Exists(absoluteXmlPath))
            return new OdtProcessResult
            {
                Success = false,
                ExitCode = -1,
                Output = "configuration.xml não encontrado.",
                ProcessStarted = false,
            };

        try
        {
            ct.ThrowIfCancellationRequested();
            int code = await GsudoElevationService.RunElevatedAsync(absoluteSetupPath,
                $"{mode} \"{absoluteXmlPath}\"", ct, workingDirectory: workingDir).ConfigureAwait(false);
            if (mode == "/download") ct.ThrowIfCancellationRequested();
            return new OdtProcessResult
            {
                Success = InstallerExitCodes.IsSuccess(code), ExitCode = code, ProcessStarted = true,
                Output = InstallerExitCodes.IsSuccess(code)
                    ? string.Empty : $"{InstallerExitCodes.Describe(code)} Consulte os logs do Office Deployment Tool."
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // ERROR_CANCELLED — usuário clicou "Não" no UAC.
        {
            return new OdtProcessResult
            {
                Success = false,
                ExitCode = 1223,
                Output = "Elevação cancelada pelo usuário.",
                ElevationCanceled = true,
                ProcessStarted = false,
            };
        }
        catch (Win32Exception ex)
        {
            // Política bloqueando, helper ausente/adulterado, sem área de trabalho interativa ou UAC sem resposta:
            // o setup.exe não foi iniciado, então a falha é reportada em vez de propagar a exceção.
            return new OdtProcessResult
            {
                Success = false,
                ExitCode = ex.NativeErrorCode,
                Output = ex.Message,
                ProcessStarted = false,
            };
        }
    }


}

public sealed class OfficeElevationCanceledException(CancellationToken token)
    : OperationCanceledException("Preparação do Office cancelada no UAC.", token) { }

/// <summary>
/// Resultado da execução do setup.exe do ODT.
/// </summary>
public record OdtProcessResult
{
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public bool RebootRequired => InstallerExitCodes.RequiresReboot(ExitCode);
    public string Output { get; init; } = string.Empty;
    public bool ElevationCanceled { get; init; }
    public bool ProcessStarted { get; init; }
}
