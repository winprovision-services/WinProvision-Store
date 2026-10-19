using System.ComponentModel;
using System.Text.Json;
using WinProvision.Core.Services;

namespace WinProvision.Cli;

/// <summary>Fixed, short causes for the console; raw backend output stays in diagnostics.</summary>
public static class CliFailure
{
    public static string Describe(WingetExecutionResult result)
    {
        if (result.WingetUnavailable) return "O WinGet está indisponível.";
        var reason = result.FailureReason != WingetFailureReason.Unknown
            ? result.FailureReason : WingetErrorTranslator.Classify(result.ExitCode, result.Output);
        return Cause(reason) ?? $"A operação retornou o código {result.ExitCode}.";
    }

    public static string Describe(Exception exception)
    {
        // Classify types/codes without exposing paths, URLs, stack traces or backend output.
        return exception switch
        {
            UnauthorizedAccessException => "Acesso negado.",
            FileNotFoundException or DirectoryNotFoundException => "Arquivo ou pasta não encontrado.",
            HttpRequestException => "Falha ao acessar o serviço remoto.",
            TimeoutException => "O tempo limite da operação foi excedido.",
            JsonException => "O arquivo JSON é inválido.",
            InvalidDataException => "Os dados recebidos são inválidos ou falharam na verificação de integridade.",
            Win32Exception native when native.NativeErrorCode == 1223 => "A autorização administrativa não foi concluída.",
            Win32Exception native => Cause(InstallerExitCodes.Classify(native.NativeErrorCode)) ?? "Falha ao executar o processo.",
            IOException when unchecked((uint)exception.HResult) is 0x80070050 or 0x800700B7 => "O arquivo de destino já existe.",
            IOException => Cause(InstallerExitCodes.Classify(exception.HResult)) ?? "Falha ao ler ou gravar arquivos.",
            NotSupportedException => "A operação não é suportada pelo serviço disponível.",
            InvalidOperationException { InnerException: { } inner } => Describe(inner),
            _ => Cause(WingetErrorTranslator.Classify(exception.HResult, exception.Message)) ?? "Falha inesperada na operação."
        };
    }

    public static string Message(string cause, string? name = null)
        => $"{(name is null ? "" : name + " — ")}não foi possível concluir: {cause} Consulte o diagnóstico com winprovision logs.";

    private static string? Cause(WingetFailureReason reason) => reason switch
    {
        WingetFailureReason.InstallationInProgress => "Outra instalação está em andamento.",
        WingetFailureReason.FilesInUse => "Há arquivos em uso.",
        WingetFailureReason.DiskFull => "Não há espaço suficiente em disco.",
        WingetFailureReason.RebootRequiredBeforeInstall => "É necessário reiniciar o Windows antes de instalar.",
        WingetFailureReason.UserScopeElevationConflict => "O pacote por usuário não permite remoção em uma sessão elevada.",
        WingetFailureReason.NoPackageFound => "O aplicativo não está instalado.",
        WingetFailureReason.PackageNotInCatalog => "O pacote não foi encontrado no catálogo.",
        WingetFailureReason.ElevationRequired => "É necessária permissão de administrador.",
        WingetFailureReason.ElevationProhibited => "O instalador não permite execução como administrador.",
        WingetFailureReason.ElevationCanceled => "A autorização administrativa não foi concluída.",
        WingetFailureReason.OperationCanceled => "A operação foi cancelada pelo instalador ou pelo Windows.",
        WingetFailureReason.BlockedByPolicy => "A operação foi bloqueada por uma política do sistema.",
        WingetFailureReason.NoApplicableInstallers => "Não há instalador compatível com este computador.",
        WingetFailureReason.PackageAgreementsNotAccepted => "Os termos do pacote não foram aceitos.",
        WingetFailureReason.DownloadError => "Falha ao baixar o instalador.",
        WingetFailureReason.InstallerHashMismatch => "O hash do instalador não corresponde ao esperado.",
        WingetFailureReason.InstallError => "O instalador encontrou um erro.",
        WingetFailureReason.UninstallCommandFailed => "O comando de desinstalação falhou.",
        WingetFailureReason.CatalogError => "Falha ao acessar o catálogo.",
        WingetFailureReason.InternalError => "Falha interna do gerenciador de pacotes.",
        _ => null
    };
}
