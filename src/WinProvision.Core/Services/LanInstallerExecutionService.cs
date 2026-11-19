using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.ComponentModel;
using WinProvision.Core.Models;
using WinProvision.Elevation;

namespace WinProvision.Core.Services;

public sealed record LanInstallerSecurityInfo(string Sha256, string Signature, bool HasZoneIdentifier);

public sealed record LanInstallerInstallRequest(
    string LocalPath,
    string File,
    string PackageId,
    string Version,
    string ExpectedSha256,
    string InstallerType,
    string? Scope,
    string SilentArguments,
    bool HasZoneIdentifier,
    bool RemoveZoneIdentifier,
    int TimeoutSeconds = 7200);

public enum LanInstallerExecutionStage
{
    Validating,
    VerifyingIntegrity,
    RemovingZoneIdentifier,
    RequestingElevation,
    StartingWorker,
    Executing,
    Completed
}

public enum LanInstallerOutcome
{
    Installed,
    RestartPending,
    Canceled,
    Failed
}

public sealed record LanInstallerExecutionProgress(
    string File,
    LanInstallerExecutionStage Stage,
    string Message);

public sealed record LanInstallerProcessResult(bool Success, int ExitCode, string Output, bool InstallerStarted, bool WasElevated);

public sealed record LanInstallerExecutionResult(
    bool Success,
    int ExitCode,
    LanInstallerExecutionStage Stage,
    string Message,
    string? ExceptionDetails = null,
    LanInstallerOutcome Outcome = LanInstallerOutcome.Failed);

public static class LanInstallerExitCodePolicy
{
    public static LanInstallerOutcome Classify(string file, int exitCode)
    {
        bool isMsi = Path.GetExtension(file).Equals(".msi", StringComparison.OrdinalIgnoreCase);
        if (exitCode == 0)
            return LanInstallerOutcome.Installed;
        if (isMsi && exitCode is 3010 or 1641)
            return LanInstallerOutcome.RestartPending;
        if (isMsi && exitCode == 1602 || exitCode == 1223)
            return LanInstallerOutcome.Canceled;
        return LanInstallerOutcome.Failed;
    }

    public static string Describe(LanInstallerOutcome outcome, int exitCode) => outcome switch
    {
        LanInstallerOutcome.Installed => "Instalado",
        LanInstallerOutcome.RestartPending => "Instalado, reinício pendente",
        LanInstallerOutcome.Canceled => "Cancelado",
        _ => $"Falhou (código {exitCode})"
    };
}

public interface ILanInstallerProcessRunner
{
    Task<LanInstallerProcessResult> RunAsync(
        string executable,
        string arguments,
        string workingDirectory,
        bool elevated,
        TimeSpan timeout,
        Action<string>? onOutput,
        CancellationToken token);
}

public sealed class LanInstallerProcessRunner : ILanInstallerProcessRunner
{
    public async Task<LanInstallerProcessResult> RunAsync(
        string executable,
        string arguments,
        string workingDirectory,
        bool elevated,
        TimeSpan timeout,
        Action<string>? onOutput,
        CancellationToken token)
    {
        if (elevated)
        {
            WingetExecutionResult result = await ElevatedProcessRunner.RunElevatedAsync(
                executable, WindowsArgumentParser.Parse(arguments), token, ElevationCategory.LanClient,
                affectWingetElevationState: false, onLogReceived: onOutput).ConfigureAwait(false);
            return new(result.Success, result.ExitCode, result.Output, result.InstallerStarted, result.WasElevated);
        }

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            },
            EnableRaisingEvents = true
        };
        try
        {
            if (!process.Start())
                throw new InvalidOperationException($"Não foi possível iniciar o instalador '{executable}'.");
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(token);
            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutSource.Token);
            try
            {
                await process.WaitForExitAsync(linkedSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linkedSource.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
                if (timeoutSource.IsCancellationRequested && !token.IsCancellationRequested)
                    throw new TimeoutException($"O instalador excedeu o limite de {timeout.TotalSeconds:N0} segundos.");
                throw;
            }

            string output = (await standardOutput.ConfigureAwait(false)) + Environment.NewLine
                + (await standardError.ConfigureAwait(false));
            if (!string.IsNullOrWhiteSpace(output))
                onOutput?.Invoke(output);
            return new(process.ExitCode == 0, process.ExitCode, output, true, false);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"Não foi possível executar o instalador '{executable}': {exception.Message}", exception);
        }
    }
}

public sealed class LanInstallerExecutionService
{
    private readonly ILanInstallerProcessRunner _processRunner;

    public LanInstallerExecutionService(ILanInstallerProcessRunner? processRunner = null) =>
        _processRunner = processRunner ?? new LanInstallerProcessRunner();

    public async Task<LanInstallerSecurityInfo> InspectAsync(LanCachedInstaller cached, CancellationToken token = default)
        => await InspectFileAsync(cached.LocalPath, cached.Availability.Installer.File,
            cached.Availability.Installer.Sha256, cached.Availability.Installer.InstallerType,
            progress: null, token).ConfigureAwait(false);

    private async Task<LanInstallerSecurityInfo> InspectFileAsync(
        string path,
        string file,
        string expectedSha256,
        string? expectedType,
        IProgress<LanInstallerExecutionProgress>? progress,
        CancellationToken token)
    {
        string stage = "recalcular SHA-256";
        try
        {
            CoreLog.Information("LanClient.Install", $"arquivo={path}; etapa={stage}");
            progress?.Report(new(file, LanInstallerExecutionStage.VerifyingIntegrity, "Recalculando SHA-256."));
            string actualHash = await HashFileAsync(path, token).ConfigureAwait(false);
            if (!actualHash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"O SHA-256 local de {file} não corresponde ao manifesto LAN.");

            stage = "redetectar tipo do instalador";
            CoreLog.Information("LanClient.Install", $"arquivo={path}; etapa={stage}");
            progress?.Report(new(file, LanInstallerExecutionStage.VerifyingIntegrity, "Redetectando o tipo do instalador."));
            LanInstallerDetection detected = LanInstallerTypeDetector.Detect(path);
            if (!string.Equals(detected.ModuleType, expectedType, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"O tipo do instalador '{file}' diverge: servidor '{expectedType}', arquivo local '{detected.DisplayName}'.");

            stage = "verificar assinatura digital";
            CoreLog.Information("LanClient.Install", $"arquivo={path}; etapa={stage}");
            progress?.Report(new(file, LanInstallerExecutionStage.VerifyingIntegrity, "Verificando assinatura digital."));
            string signature = GetSignatureSummary(path);

            stage = "verificar Zone.Identifier";
            CoreLog.Information("LanClient.Install", $"arquivo={path}; etapa={stage}");
            bool hasZoneIdentifier = File.Exists(path + ":Zone.Identifier");
            return new(actualHash, signature, hasZoneIdentifier);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
            or CryptographicException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            string inner = exception.InnerException is null ? string.Empty
                : $" Inner: {exception.InnerException.GetType().Name}: {exception.InnerException.Message} (HResult=0x{exception.InnerException.HResult:X8}).";
            throw new LanInstallerSyncException(
                $"Falhou na etapa '{stage}' para '{path}'. HResult=0x{exception.HResult:X8}; esperado SHA-256={expectedSha256}; motivo: {exception.Message}.{inner}",
                stage, exception);
        }
    }

    public async Task<LanInstallerExecutionResult> InstallAsync(
        LanInstallerInstallRequest request,
        IProgress<LanInstallerExecutionProgress>? progress = null,
        CancellationToken token = default,
        bool runningElevated = false)
    {
        LanInstallerExecutionStage stage = LanInstallerExecutionStage.Validating;
        try
        {
            token.ThrowIfCancellationRequested();
            Report(stage, "Validando argumentos e caminho.");
            if (!LanInstallerValidation.IsSafeFileName(request.File)
                || !string.Equals(Path.GetFileName(request.LocalPath), request.File, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O nome do instalador ou o caminho local é inválido.");
            if (!LanInstallerValidation.IsSafeSilentArguments(request.SilentArguments))
                throw new InvalidDataException("Os argumentos silenciosos são inválidos ou contêm metacaracteres não permitidos.");
            if (request.TimeoutSeconds is < 30 or > 21600)
                throw new InvalidDataException("O timeout precisa estar entre 30 segundos e 6 horas.");

            stage = LanInstallerExecutionStage.VerifyingIntegrity;
            Report(stage, "Verificando hash, tipo e assinatura.");
            LanInstallerSecurityInfo security = await InspectFileAsync(request.LocalPath, request.File,
                request.ExpectedSha256, request.InstallerType, progress, token).ConfigureAwait(false);
            if (security.HasZoneIdentifier)
            {
                stage = LanInstallerExecutionStage.RemovingZoneIdentifier;
                if (!request.RemoveZoneIdentifier)
                    throw new InvalidOperationException("O instalador contém Zone.Identifier. Confirme a remoção explícita antes de instalar.");
                Report(stage, "Removendo Zone.Identifier conforme solicitado.");
                File.Delete(request.LocalPath + ":Zone.Identifier");
            }

            bool isMsi = Path.GetExtension(request.LocalPath).Equals(".msi", StringComparison.OrdinalIgnoreCase);
            string executable = isMsi ? Path.Combine(Environment.SystemDirectory, "msiexec.exe") : request.LocalPath;
            string arguments = isMsi
                ? $"/i {QuoteArgument(request.LocalPath)} {request.SilentArguments}"
                : request.SilentArguments;
            string workingDirectory = Path.GetDirectoryName(request.LocalPath)!;
            bool requiresElevation = !runningElevated
                && string.Equals(request.Scope, "machine", StringComparison.OrdinalIgnoreCase);
            stage = requiresElevation ? LanInstallerExecutionStage.RequestingElevation : LanInstallerExecutionStage.Executing;
            if (requiresElevation)
            {
                Report(stage, "Solicitando elevação pelo WinProvision Elevator.");
            }
            else
                Report(stage, $"Executando {Path.GetFileName(executable)} para {request.PackageId} {request.Version}.");
            LanInstallerProcessResult processResult = await _processRunner.RunAsync(
                executable,
                arguments,
                workingDirectory,
                requiresElevation,
                TimeSpan.FromSeconds(request.TimeoutSeconds),
                output =>
                {
                    stage = LanInstallerExecutionStage.Executing;
                    Report(LanInstallerExecutionStage.Executing, output);
                },
                token).ConfigureAwait(false);
            stage = LanInstallerExecutionStage.Executing;
            if (!processResult.Success)
            {
                LanInstallerOutcome outcome = LanInstallerExitCodePolicy.Classify(request.File, processResult.ExitCode);
                string message = LanInstallerExitCodePolicy.Describe(outcome, processResult.ExitCode);
                Report(stage, message);
                return new(outcome is LanInstallerOutcome.Installed or LanInstallerOutcome.RestartPending,
                    processResult.ExitCode, stage, message, Outcome: outcome);
            }
            Report(LanInstallerExecutionStage.Completed, "Instalação concluída.");
            return new(true, processResult.ExitCode, LanInstallerExecutionStage.Completed, "Instalado",
                Outcome: LanInstallerOutcome.Installed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "LanClient.Install");
            string message = $"Falha na etapa '{StageLabel(stage)}': {exception.Message} (0x{exception.HResult:X8}).";
            LanInstallerOutcome outcome = exception is OperationCanceledException
                ? LanInstallerOutcome.Canceled
                : LanInstallerOutcome.Failed;
            return new(false, -1, stage, message, exception.ToString(), outcome);
        }

        void Report(LanInstallerExecutionStage progressStage, string message) =>
            progress?.Report(new(request.File, progressStage, message));
    }

    private static string StageLabel(LanInstallerExecutionStage stage) => stage switch
    {
        LanInstallerExecutionStage.Validating => "validação da seleção",
        LanInstallerExecutionStage.VerifyingIntegrity => "verificação de hash e assinatura",
        LanInstallerExecutionStage.RemovingZoneIdentifier => "remoção de Zone.Identifier",
        LanInstallerExecutionStage.RequestingElevation => "elevação",
        LanInstallerExecutionStage.Executing => "execução do instalador",
        LanInstallerExecutionStage.Completed => "conclusão",
        _ => stage.ToString()
    };

    private static string QuoteArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }

    private static string GetSignatureSummary(string path)
    {
        uint result = VerifyAuthenticodeSignature(path);
        if (result == 0)
            return "Assinatura Authenticode válida.";
        if (result == 0x800B0100)
            return "Sem assinatura digital reconhecida.";
        if (result == uint.MaxValue)
            return "Não foi possível verificar a assinatura digital neste sistema.";
        return $"Assinatura inválida ou não confiável (código 0x{result:X8}).";
    }

    private static uint VerifyAuthenticodeSignature(string path)
    {
        if (!OperatingSystem.IsWindows())
            return uint.MaxValue;

        using var fileInfo = new WinTrustFileInfo(path);
        var trustData = new WinTrustData(fileInfo.Pointer);
        Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        try
        {
            return WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
        }
        finally
        {
            trustData.StateAction = 2;
            _ = WinVerifyTrust(IntPtr.Zero, ref action, ref trustData);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern uint WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData trustData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class WinTrustFileInfo : IDisposable
    {
        public uint Size = (uint)Marshal.SizeOf<WinTrustFileInfoLayout>();
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
        public readonly IntPtr Pointer;

        public WinTrustFileInfo(string path)
        {
            FilePath = Marshal.StringToCoTaskMemUni(path);
            var value = new WinTrustFileInfoLayout { Size = Size, FilePath = FilePath };
            Pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfoLayout>());
            Marshal.StructureToPtr(value, Pointer, false);
        }

        public void Dispose()
        {
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeCoTaskMem(FilePath);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfoLayout
    {
        public uint Size;
        public IntPtr FilePath;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr FileInfo;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;

        public WinTrustData(IntPtr fileInfo)
        {
            Size = (uint)Marshal.SizeOf<WinTrustData>();
            PolicyCallbackData = IntPtr.Zero;
            SipClientData = IntPtr.Zero;
            UiChoice = 2;
            RevocationChecks = 0;
            UnionChoice = 1;
            FileInfo = fileInfo;
            StateAction = 1;
            StateData = IntPtr.Zero;
            UrlReference = IntPtr.Zero;
            ProviderFlags = 0x00001000;
            UiContext = 0;
        }
    }

}
