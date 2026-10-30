using System;

namespace WinProvision.Core.Services;

/// <summary>
/// Motivo "canônico" de falha do gerenciador de pacotes, independente do idioma em que o cliente de pacotes
/// decidiu imprimir a mensagem daquela vez (a cobertura de localização do gerenciador de pacotes-cli é
/// incompleta/inconsistente: algumas strings de erro têm tradução pt-BR, outras não, então
/// a MESMA operação pode falhar em inglês numa tentativa e em português noutra).
/// </summary>
public enum WingetFailureReason
{
    Unknown,
    InstallationInProgress,
    FilesInUse,
    DiskFull,
    RebootRequiredBeforeInstall,

    /// <summary>
    /// "The package installed for user scope cannot be uninstalled when running with
    /// administrator privileges." — só deve aparecer se a Store for lançada elevada por
    /// fora (ex.: "Executar como administrador" no menu de contexto), já que por padrão
    /// (ver app.manifest) ela roda sem elevação.
    /// </summary>
    UserScopeElevationConflict,

    /// <summary>"No installed package found matching input criteria."</summary>
    NoPackageFound,

    /// <summary>Falhou por falta de privilégio (ex.: pacote em escopo machine) — candidato a retry elevado.</summary>
    ElevationRequired,

    /// <summary>O instalador explicitamente não permite execução elevada (0x8A150056).</summary>
    ElevationProhibited,

    /// <summary>Falha no comando de desinstalação do instalador (0x8A150030); candidata a uma tentativa elevada.</summary>
    UninstallCommandFailed,

    /// <summary>Usuário recusou o prompt de UAC no retry elevado.</summary>
    ElevationCanceled,
    /// <summary>O usuário cancelou o comando gerenciador de pacotes.</summary>
    OperationCanceled,
    BlockedByPolicy,
    NoApplicableInstallers,
    NoApplicableUpgrade,
    PackageAgreementsNotAccepted,
    DownloadError,
    InstallerHashMismatch,
    InstallError,
    CatalogError,
    InternalError,

    /// <summary>
    /// "No package found matching input criteria." — o ID não existe no catálogo consultado
    /// (gerenciador de pacotes install/update/show). Diferente de <see cref="NoPackageFound"/>, que é o caso
    /// do uninstall/list ("No installed package found...", pacote não está INSTALADO).
    /// Exit code do gerenciador de pacotes: 0x8A150014 (APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND).
    /// </summary>
    PackageNotInCatalog,
}

/// <summary>
/// Classifica a saída bruta (stdout/stderr) do cliente de pacotes em um <see cref="WingetFailureReason"/>
/// e devolve uma mensagem curta e fixa em pt-BR pra exibir na UI — em vez de repassar o texto
/// cru do gerenciador de pacotes (que ora sai em inglês, ora em português, dependendo da string específica).
/// </summary>
public static class WingetErrorTranslator
{
    // HRESULTs do gerenciador de pacotes-cli (src/AppInstallerSharedLib/Public/AppInstallerErrors.h). O exit
    // code NÃO depende do idioma em que o gerenciador de pacotes imprime o texto, então é a fonte mais
    // confiável — o texto só serve pra desempatar o que o exit code sozinho não distingue
    // (ex.: 0x8A150014 vale tanto pra "pacote não existe no catálogo" quanto pra "pacote
    // não está instalado").
    private const int NoApplicationsFound = unchecked((int)0x8A150014);

    /// <summary>
    /// Classifica usando o texto E o exit code do gerenciador de pacotes. Prefira esta sobrecarga: o texto
    /// (<see cref="Classify(string?)"/>) vem no idioma do sistema, o exit code não.
    /// </summary>
    public static WingetFailureReason Classify(int exitCode, string? output)
    {
        if (InstallerExitCodes.IsWingetSuccess(exitCode))
            return WingetFailureReason.Unknown;

        // O mesmo código é usado quando o pacote não existe no catálogo e
        // quando nenhum pacote instalado corresponde ao critério.
        if (exitCode == NoApplicationsFound)
            return Classify(output) == WingetFailureReason.NoPackageFound
                ? WingetFailureReason.NoPackageFound : WingetFailureReason.PackageNotInCatalog;

        WingetFailureReason mapped = InstallerExitCodes.Classify(exitCode);
        if (mapped != WingetFailureReason.Unknown)
            return mapped;

        return Classify(output);
    }

    public static WingetFailureReason Classify(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return WingetFailureReason.Unknown;
        }

        // Casa tanto a string original em inglês quanto a tradução pt-BR (quando existe),
        // porque não dá pra confiar em qual das duas o gerenciador de pacotes vai imprimir dessa vez.
        if (Contains(output, "cannot be uninstalled when running with administrator privileges") ||
            Contains(output, "não pode ser desinstalado quando executado com privilégios de administrador"))
        {
            return WingetFailureReason.UserScopeElevationConflict;
        }

        if (Contains(output, "No installed package found matching input criteria") ||
            Contains(output, "Nenhum pacote instalado encontrado correspondendo aos critérios de entrada"))
        {
            return WingetFailureReason.NoPackageFound;
        }

        // Install/update de um ID que não existe no catálogo. Note a diferença pra frase acima:
        // aqui NÃO tem a palavra "installed" ("No package found matching input criteria.").
        if (Contains(output, "No package found matching input criteria") ||
            Contains(output, "Nenhum pacote encontrado correspondendo aos critérios de entrada"))
        {
            return WingetFailureReason.PackageNotInCatalog;
        }

        if (Contains(output, "Access is denied") ||
            Contains(output, "Acesso negado") ||
            Contains(output, "0x80070005") ||
            Contains(output, "requires administrator") ||
            Contains(output, "requer privilégios de administrador") ||
            Contains(output, "elevated permissions are required"))
        {
            return WingetFailureReason.ElevationRequired;
        }

        if (Contains(output, "0x8A150056") || Contains(output, "prohibits elevation") || Contains(output, "proíbe elevação"))
            return WingetFailureReason.ElevationProhibited;

        return WingetFailureReason.Unknown;
    }

    /// <summary>
    /// Mensagem final em pt-BR pra mostrar ao usuário — curta e direta, sempre a mesma
    /// independente de como o gerenciador de pacotes formatou a própria mensagem dessa vez.
    /// </summary>
    public static string ToMessage(WingetFailureReason reason, string verb, string appName) => reason switch
    {
        WingetFailureReason.InstallationInProgress => "Outra instalação está em andamento. Aguarde sua conclusão.",
        WingetFailureReason.FilesInUse => "Há arquivos em uso. Feche o aplicativo e tente novamente.",
        WingetFailureReason.DiskFull => "Não há espaço suficiente em disco.",
        WingetFailureReason.RebootRequiredBeforeInstall => "Reinicie o Windows antes de tentar instalar novamente.",
        WingetFailureReason.UserScopeElevationConflict =>
            $"Não foi possível {verb} \"{appName}\". Feche o WinProvision e abra-o sem \"Executar como administrador\".",

        WingetFailureReason.NoPackageFound =>
            $"\"{appName}\" não está instalado.",

        WingetFailureReason.PackageNotInCatalog =>
            $"\"{appName}\" não foi encontrado no catálogo. Atualize a lista e tente novamente.",

        WingetFailureReason.ElevationRequired =>
            $"\"{appName}\" requer permissão de administrador.",

        WingetFailureReason.ElevationCanceled =>
            $"{Capitalize(verb)} de \"{appName}\" cancelada. Autorize no aviso do Windows para continuar.",

        WingetFailureReason.OperationCanceled =>
            $"{Capitalize(verb)} de \"{appName}\" cancelada.",

        WingetFailureReason.ElevationProhibited =>
            $"O instalador de \"{appName}\" não permite execução como administrador. Abra o WinProvision sem elevação.",

        WingetFailureReason.BlockedByPolicy =>
            $"O sistema bloqueou a operação para \"{appName}\".",

        WingetFailureReason.NoApplicableInstallers =>
            $"Não há instalador compatível com este computador para \"{appName}\".",

        WingetFailureReason.NoApplicableUpgrade =>
            $"A atualização disponível de \"{appName}\" não se aplica à instalação atual.",

        WingetFailureReason.PackageAgreementsNotAccepted =>
            $"Aceite os termos de \"{appName}\" e tente novamente.",

        WingetFailureReason.DownloadError =>
            $"Não foi possível baixar \"{appName}\". Tente novamente.",

        WingetFailureReason.InstallerHashMismatch =>
            $"A verificação de segurança de \"{appName}\" falhou. O instalador não foi executado.",

        WingetFailureReason.InstallError =>
            $"O instalador de \"{appName}\" encontrou um erro.",

        WingetFailureReason.CatalogError =>
            "Não foi possível acessar o catálogo. Tente novamente.",

        WingetFailureReason.InternalError =>
            $"Não foi possível {verb} \"{appName}\". Tente novamente.",

        _ => $"Não foi possível {verb} \"{appName}\". Confira os detalhes da operação.",
    };

    private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static bool Contains(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
