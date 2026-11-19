using System.Runtime.Versioning;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
#if WINDOWS
using System.Security.Principal;
#endif
using WinProvision.Core.Models;
using WinProvision.Elevation;
using WinProvision.Providers.WinGet.Cli.Client;
using WinProvision.Providers.WinGet.Cli.Contracts;
using WinProvision.Providers.WinGet.Cli.Parsing;
using WinProvision.WinGet.Contracts;

namespace WinProvision.Core.Services;

public class WingetExecutionResult
{
    public bool Success { get; set; }
    [JsonIgnore]
    public OperationVerificationStatus VerificationStatus { get; set; }
    [JsonIgnore]
    public string? EffectiveSource { get; set; }
    public int ExitCode { get; set; }
    private bool _rebootRequired;
    public bool RebootRequired { get => _rebootRequired || InstallerExitCodes.RequiresReboot(ExitCode); set => _rebootRequired = value; }
    public bool RebootInitiated => InstallerExitCodes.RebootInitiated(ExitCode);
    public string Output { get; set; } = string.Empty;
    [JsonIgnore] public string StandardOutput { get; set; } = string.Empty;
    [JsonIgnore] public string StandardError { get; set; } = string.Empty;
    public bool WasElevated { get; set; }
    public bool WingetUnavailable { get; set; }
    [JsonIgnore] public bool ProcessStarted { get; set; }
    [JsonIgnore] public bool InstallerMayHaveStarted { get; set; }
    [JsonIgnore] public WinGetCliStatus? CliStatus { get; set; }
    /// <summary>Indica que o instalador iniciou ou que o estado ficou incerto; evita repetir a operação automaticamente.</summary>
    public bool InstallerStarted { get; set; }
    public bool VariantConfirmed { get; set; }
    public string[] VariantFixedFields { get; set; } = [];
    public string[] VariantUnfixedFields { get; set; } = [];
    /// <summary>True when an opt-in pre/post installation hook failed.</summary>
    public bool InstallHookFailed { get; set; }
    /// <summary>Versão que a rota WinProvision confirmou e instalou; metadado interno para verificação.</summary>
    [JsonIgnore]
    public string? ApiUpdateTargetVersion { get; set; }
    /// <summary>ProductCode do instalador validado, para conferir migrações que substituem a chave antiga.</summary>
    [JsonIgnore]
    public string? ApiUpdateProductCode { get; set; }
    /// <summary>O ProductCode foi lido do MSI executado após validar seu hash e versão.</summary>
    [JsonIgnore]
    public bool ApiUpdateProductCodeFromInstaller { get; set; }
    /// <summary>A conferência desta operação já confirmou a versão instalada.</summary>
    [JsonIgnore]
    public bool UpdateVersionVerified { get; set; }
    /// <summary>Uma consulta nova comprovou que o alvo já está satisfeito, sem executar instalador.</summary>
    [JsonIgnore]
    public bool UpdateAlreadySatisfied { get; set; }
    [JsonIgnore]
    public bool InstallationVerified { get; set; }
    [JsonIgnore]
    public bool IsUnconfirmed => VerificationStatus == OperationVerificationStatus.Unconfirmed;

    /// <summary>
    /// Motivo canônico da falha (ver <see cref="WingetErrorTranslator"/>), já classificado a
    /// partir de <see cref="Output"/> — permite que a UI mostre sempre a mesma mensagem em
    /// pt-BR em vez de repassar o texto cru do gerenciador de pacotes, cuja cobertura de tradução é
    /// inconsistente (a mesma falha às vezes sai em inglês, às vezes em português).
    /// Só é preenchido quando <see cref="Success"/> é false.
    /// </summary>
    public WingetFailureReason FailureReason { get; set; } = WingetFailureReason.Unknown;
}

[SupportedOSPlatform("windows")]
public class WingetExecutor
{
    private readonly WingetBootstrapper? _bootstrapper;
    private readonly OperationPreferencesService _operationPreferences;
    private readonly AppLogTaskExecutor? _taskExecutor;
    private readonly IWinGetCliClient _cliClient;

    /// <param name="bootstrapper">
    /// Opcional — quando presente (via injeção de dependência; ver App.xaml.cs), garante o
    /// gerenciador de pacotes disponível antes da PRIMEIRA instalação da sessão (ver
    /// <see cref="EnsureWingetBootstrappedOnceAsync"/>). Null mantém o comportamento sem
    /// checagem prévia, chamando cliente de pacotes diretamente.
    /// </param>
    public WingetExecutor(WingetBootstrapper? bootstrapper = null, OperationPreferencesService? operationPreferences = null,
        AppLogTaskExecutor? taskExecutor = null, IWinGetCliClient? cliClient = null)
    {
        _bootstrapper = bootstrapper;
        _operationPreferences = operationPreferences ?? new OperationPreferencesService();
        _taskExecutor = taskExecutor;
        _cliClient = cliClient ?? new WinGetCliClient();
    }

    /// <summary>
    /// Instala um pacote do gerenciador de pacotes e reporta o progresso em tempo real. Por padrão mantém
    /// a instalação silenciosa existente; installLocation é opcional.
    /// </summary>
    public async Task<WingetExecutionResult> InstallAppAsync(
        string appId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string? installLocation = null,
        string source = "winget",
        string? scope = null,
        string? architecture = null,
        bool requiresElevation = false,
        bool elevationProhibited = false,
        bool interactive = false,
        bool ignoreSecurityHash = false,
        string? version = null,
        string? customArguments = null,
        string? overrideArguments = null,
        bool force = false,
        bool uninstallPrevious = false,
        string? installerType = null,
        string? locale = null,
        WinGetVariantSelectionOptions? selection = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selection?.HasRequestedInstallScopeMismatch == true)
        {
            const string mismatchMessage = "O escopo solicitado não corresponde ao escopo disponível para este instalador. A operação não foi iniciada.";
            onLogReceived?.Invoke(mismatchMessage);
            var mismatch = new WingetExecutionResult { Success = false, ExitCode = -1, Output = mismatchMessage,
                FailureReason = WingetFailureReason.NoApplicableInstallers };
            ApplyVariantSelection(mismatch, selection);
            LogVariantSelection(mismatch, appId, selection, onLogReceived);
            return mismatch;
        }
        // Garante a resolução do gerenciador de pacotes para o
        // caminho da UI (usuário clicando "Instalar" no executável, sem CLI): a primeira
        // instalação da sessão confirma que o gerenciador de pacotes está funcional (e baixa/instala o que
        // faltar, se não estiver) ANTES de tentar rodar o comando — evita que o usuário veja
        // "cliente de pacotes não encontrado" logo após o primeiro logon, quando o Windows ainda pode
        // estar terminando de provisionar os pacotes APPX de sistema. Instalações seguintes
        // reaproveitam o resultado já checado (ver EnsureWingetBootstrappedOnceAsync) — não
        // roda de novo a cada clique em "Instalar".
        var bootstrapResult = await EnsureWingetBootstrappedOnceAsync(onLogReceived);
        if (!bootstrapResult.IsUsable)
        {
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = $"Winget não está disponível e não foi possível deixá-lo funcional: {bootstrapResult.ErrorMessage}",
                WingetUnavailable = true
            };
        }

        // --source fixo (default "gerenciador de pacotes"): sem isso, o gerenciador de pacotes precisa resolver o --id
        // consultando TODAS as sources configuradas (gerenciador de pacotes + msstore) antes de decidir
        // qual usar. Em ambientes sem acesso íntegro aos serviços da catálogo visual (ex.:
        // Windows Sandbox, redes restritas), a pesquisa na source "msstore" falha ("Falha
        // na pesquisa da origem: msstore") e derruba o comando inteiro mesmo quando o
        // pacote existe na source "gerenciador de pacotes". Pra apps com AppEntry.Source == "msstore" (ver
        // MsStoreCatalogService), quem chama passa esse valor aqui pra fixar a source
        // correta em vez do padrão. UpdateAppAsync e o script exportado (PackagesPage) já
        // fixam a source por esse mesmo motivo — faltava só aqui. --disable-interactivity
        // evita qualquer prompt de confirmação de source ficar esperando input que nunca
        // chega (stdin não é redirecionado nesse Process).
        bool useInteractive = interactive || source.Equals("msstore", StringComparison.OrdinalIgnoreCase);
        string? normalizedScope = NormalizeScope(selection?.Scope ?? scope);
        string? normalizedArchitecture = NormalizeArchitecture(selection?.Architecture ?? architecture);
        var commandOptions = new PackageInstallOptions(Interactive: useInteractive, Scope: selection?.Scope ?? normalizedScope,
            Architecture: normalizedArchitecture, IgnoreSecurityHash: ignoreSecurityHash,
            InstallLocation: installLocation, RequiresElevation: requiresElevation, Version: selection?.Version ?? version,
            CustomArguments: customArguments, OverrideArguments: overrideArguments, UninstallPrevious: uninstallPrevious,
            InstallerType: selection?.InstallerType ?? installerType, Locale: selection?.Locale ?? locale);
        string args = WingetCommandLineBuilder.BuildArguments("install", appId, source, commandOptions, force: force);
        var result = await ExecuteInstallWithRecoveryAsync(
            args, onLogReceived, cancellationToken, normalizedScope, normalizedArchitecture,
            requiresElevation,
            preserveScopeOnRetry: WindowsIdentityContext.IsRunningAsSystem(),
            preserveVariantSelection: selection is not null);

        result = NormalizeInstallOutcome(result, isUpgrade: false);
        ApplyVariantSelection(result, selection);
        LogVariantSelection(result, appId, selection, onLogReceived);
        return result;
    }

    /// <summary>
    /// Desinstala um pacote silenciosamente.
    /// </summary>
    public async Task<WingetExecutionResult> UninstallAppAsync(
        string appId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string? source = null,
        string? installedVersion = null,
        bool interactive = false,
        bool requiresElevation = false,
        string? customArguments = null)
    {
        // Mesma garantia do InstallAppAsync (ver comentário lá) — faltava aqui. Sem essa
        // checagem, se a desinstalação for a primeira operação da sessão (gerenciador de pacotes ainda não
        // provisionado, ex.: logo após o primeiro logon), o processo "cliente de pacotes" nem existe
        // no PATH e o comando abaixo falha sempre, pra qualquer app — não é um problema de
        // pacote específico, é cliente de pacotes simplesmente não estar disponível ainda.
        var bootstrapResult = await EnsureWingetBootstrappedOnceAsync(onLogReceived);
        if (!bootstrapResult.IsUsable)
        {
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = $"Winget não está disponível e não foi possível deixá-lo funcional: {bootstrapResult.ErrorMessage}"
            };
        }

        // SEM --source gerenciador de pacotes aqui, de propósito (diferente do Install/Update): esse é um
        // bug conhecido do próprio gerenciador de pacotes-cli (ex.: plataforma do sistema/gerenciador de pacotes-cli#5683, #4875,
        // #3690) — "uninstall --id X --exact --source gerenciador de pacotes" falha quase sempre com "No
        // installed package found matching input criteria", mesmo com o pacote instalado
        // e visível em "gerenciador de pacotes list", porque a correlação interna entre pacote instalado e
        // a source de origem não é confiável. Pra uninstall/update, o gerenciador de pacotes já resolve o
        // --exact contra a lista local de instalados sem precisar de --source; restringir
        // a source só quebra esse casamento. --accept-source-agreements continua aqui por
        // segurança (algumas builds ainda consultam a source pra outros metadados).
        //
        // ESCOPO: a Store não roda mais elevada por padrão (ver app.manifest) — rodar
        // elevada fazia o gerenciador de pacotes se recusar a desinstalar pacotes em escopo "user" (a
        // maioria, já que é o padrão de instalação), mesmo estando visíveis em "gerenciador de pacotes
        // list". Sem --scope, deixamos o gerenciador de pacotes decidir primeiro (cobre o caso comum);
        // falhando, força "user"; falhando ainda, tenta "machine" (cobre quem foi
        // instalado nesse escopo explicitamente). Cada tentativa é sequencial e para no
        // primeiro sucesso.
        if (!IsSafeUninstallValue(appId))
        {
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = "O identificador do pacote contém caracteres inválidos."
            };
        }

        bool hasVersion = !string.IsNullOrWhiteSpace(installedVersion)
            && !installedVersion.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && IsSafeUninstallValue(installedVersion);
        bool hasSource = !string.IsNullOrWhiteSpace(source)
            && !source.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
            && IsSafeUninstallValue(source);
        string? effectiveVersion = hasVersion ? installedVersion : null;
        var uninstallOptions = new PackageInstallOptions(Interactive: interactive,
            RequiresElevation: requiresElevation, UninstallArguments: customArguments);
        string baseArgs = WingetCommandLineBuilder.BuildArguments("uninstall", appId,
            hasSource ? source : null, uninstallOptions, effectiveVersion);
        string sourceIndependentArgs = WingetCommandLineBuilder.BuildArguments("uninstall", appId,
            null, uninstallOptions, effectiveVersion, includeSource: false);

        // An unelevated gerenciador de pacotes may invoke an uninstall.exe that raises its own UAC
        // prompt. Start desktop removals through our elevator so the consent is
        // attributed to WinProvision. Store removals retain their user context.
        bool preferElevation = requiresElevation || !string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase);
        bool retryAsUser = false;
        async Task<WingetExecutionResult> RunUninstallAttemptAsync(string arguments, bool userScope = false)
        {
            var attempt = await ExecuteWithElevationFallbackAsync(arguments, onLogReceived,
                cancellationToken, forceElevation: preferElevation && !userScope);
            if (attempt.FailureReason == WingetFailureReason.Unknown)
                attempt.FailureReason = WingetErrorTranslator.Classify(attempt.ExitCode, attempt.Output);
            if (attempt.FailureReason == WingetFailureReason.UserScopeElevationConflict
                && preferElevation && !requiresElevation && !userScope
                && !cancellationToken.IsCancellationRequested)
            {
                preferElevation = false;
                retryAsUser = true;
                attempt = await ExecuteWithElevationFallbackAsync(arguments, onLogReceived, cancellationToken);
                if (attempt.FailureReason == WingetFailureReason.Unknown)
                    attempt.FailureReason = WingetErrorTranslator.Classify(attempt.ExitCode, attempt.Output);
            }
            return attempt;
        }

        var result = await RunUninstallAttemptAsync(baseArgs);
        if (result.Success || cancellationToken.IsCancellationRequested)
        {
            return result;
        }
        if (result.FailureReason == WingetFailureReason.Unknown)
        {
            result.FailureReason = WingetErrorTranslator.Classify(result.ExitCode, result.Output);
        }

        // Faz uma única tentativa elevada quando o comando de uninstall falha com
        // APPINSTALLER_CLI_ERROR_EXEC_UNINSTALL_COMMAND_FAILED. Esse código é genérico,
        // portanto não pedimos UAC em cada fallback: só testamos uma vez e seguimos com
        // as demais estratégias se o comando elevado falhar.
        if (result.FailureReason == WingetFailureReason.UninstallCommandFailed && !preferElevation && !retryAsUser
            && !string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase) && !IsCurrentProcessElevated())
        {
            onLogReceived?.Invoke("A remoção exige uma tentativa com privilégios elevados...");
            var elevatedRetry = await ExecuteCliAsync(WindowsArgumentParser.Parse(baseArgs), onLogReceived,
                cancellationToken, WinGetCliExecutionMode.Elevated);
            if (elevatedRetry.Success || elevatedRetry.FailureReason == WingetFailureReason.ElevationCanceled
                || cancellationToken.IsCancellationRequested)
                return elevatedRetry;
            if (elevatedRetry.FailureReason == WingetFailureReason.Unknown)
                elevatedRetry.FailureReason = WingetErrorTranslator.Classify(elevatedRetry.ExitCode, elevatedRetry.Output);
            result = elevatedRetry;
        }

        if (IsElevationTerminal(result.FailureReason))
            return result;

        // UserScopeElevationConflict só deve aparecer se a Store for lançada elevada por
        // fora (ex.: "Executar como administrador" no menu de contexto) — por padrão ela
        // não roda mais assim (ver app.manifest). Quando acontece, é um bloqueio do gerenciador de pacotes
        // baseado no nível de privilégio do PROCESSO chamador, não no --scope do comando:
        // repetir com --scope user, --scope machine ou pelo Name (abaixo) bate na mesma
        // restrição pra esse pacote, só atrasando a resposta. Sai já
        // com o motivo classificado pra UI mostrar uma mensagem fixa e acionável em pt-BR.
        if (result.FailureReason == WingetFailureReason.UserScopeElevationConflict)
        {
            return result;
        }

        // A listagem instalada pode conhecer um identificador diferente do catálogo
        // (por exemplo, quando o manifesto foi renomeado). Consulta o ID local e repete
        // a remoção com ele antes de recorrer a heurísticas pelo nome.
        // Se não houver versão correspondente no manifesto, repete sem fixar a versão.
        if (hasVersion && unchecked((uint)result.ExitCode) == 0x8A150017)
        {
            string noVersionArgs = BuildUninstallArguments(appId, true, null, interactive: interactive);
            var noVersionResult = await RunUninstallAttemptAsync(noVersionArgs);
            if (noVersionResult.Success || cancellationToken.IsCancellationRequested || IsElevationTerminal(noVersionResult.FailureReason))
                return noVersionResult;
            result = noVersionResult;
            sourceIndependentArgs = BuildUninstallArguments(appId, true, null, interactive: interactive);
        }

        UpgradablePackage? localPackage = null;
        if (result.FailureReason == WingetFailureReason.NoPackageFound)
        {
            var listResult = await ExecuteWingetCommandAsync(
                $"list --id \"{appId}\" --exact --disable-interactivity --accept-source-agreements",
                onLogReceived: null,
                cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return result;
            if (WingetUpgradeListParser.TryParse(
                    listResult.Output,
                    out var localPackages,
                    requireAvailableVersion: false))
                localPackage = localPackages.FirstOrDefault();
        }

        if (localPackage is not null)
        {
            bool localIdDiffers = !string.IsNullOrWhiteSpace(localPackage.Id)
                && !localPackage.Id.Equals(appId, StringComparison.OrdinalIgnoreCase)
                && IsSafeUninstallValue(localPackage.Id);
            if (localIdDiffers)
            {
                string localSource = IsSafeUninstallValue(localPackage.Source) ? localPackage.Source : string.Empty;
                string localIdArgs = BuildUninstallArguments(localPackage.Id, true, null,
                    localSource.Length > 0 ? localSource : null, interactive);
                var localIdResult = await RunUninstallAttemptAsync(localIdArgs);
                if (localIdResult.Success || cancellationToken.IsCancellationRequested || IsElevationTerminal(localIdResult.FailureReason))
                    return localIdResult;
                result = localIdResult;
            }

        }

        // gerenciador de pacotes can fail to correlate a source for installed packages, so retry without
        // it before changing
        // scope or falling back to the installed display name.
        if (hasSource)
        {
            var sourceIndependentResult = await RunUninstallAttemptAsync(sourceIndependentArgs);
            if (sourceIndependentResult.Success || cancellationToken.IsCancellationRequested || IsElevationTerminal(sourceIndependentResult.FailureReason))
                return sourceIndependentResult;
            result = sourceIndependentResult;
        }

        foreach (string scope in new[] { "user", "machine" })
        {
            var scopedResult = await RunUninstallAttemptAsync($"{sourceIndependentArgs} --scope {scope}", userScope: scope == "user");
            if (scopedResult.Success || cancellationToken.IsCancellationRequested || IsElevationTerminal(scopedResult.FailureReason))
            {
                return scopedResult;
            }

            scopedResult.FailureReason = WingetErrorTranslator.Classify(scopedResult.ExitCode, scopedResult.Output);
            if (scopedResult.FailureReason == WingetFailureReason.UserScopeElevationConflict)
            {
                return scopedResult;
            }

            result = scopedResult;
        }

        // Último recurso: casar por --id, mesmo com --exact e os 3 escopos, ainda pode
        // falhar com "No installed package found matching input criteria" mesmo com o
        // pacote instalado e visível em "gerenciador de pacotes list" — bug real do gerenciador de pacotes-cli (ex.:
        // plataforma do sistema/gerenciador de pacotes-cli#1711, #3093, #4066): a correlação entre o Id do manifesto e
        // a entrada de Apps & Features local às vezes se perde (o Id do manifesto mudou,
        // ou o pacote nunca foi corretamente indexado por Id). Nesses relatos, desinstalar
        // pelo NOME exibido em "gerenciador de pacotes list" (em vez do Id) segue funcionando, porque o
        // Name vem direto da própria entrada local, sem depender dessa correlação. Por
        // isso, buscamos o Name local via "gerenciador de pacotes list --id X --exact" e tentamos de novo
        // com ele.
        string? localName = localPackage?.Name;
        if (string.IsNullOrWhiteSpace(localName) || localName == appId)
        {
            return result;
        }

        string nameArgs = BuildUninstallArguments(localName, false, null,
            hasSource ? source : localPackage?.Source, interactive);
        var byNameResult = await RunUninstallAttemptAsync(nameArgs);
        if (byNameResult.Success)
        {
            return byNameResult;
        }

        if (byNameResult.FailureReason == WingetFailureReason.Unknown)
        {
            byNameResult.FailureReason = WingetErrorTranslator.Classify(byNameResult.ExitCode, byNameResult.Output);
        }

        return IsElevationTerminal(byNameResult.FailureReason)
            ? byNameResult
            : result;
    }

    private static bool IsElevationTerminal(WingetFailureReason reason) => reason is
        WingetFailureReason.UserScopeElevationConflict or
        WingetFailureReason.ElevationCanceled or
        WingetFailureReason.ElevationProhibited;

    private static bool IsCurrentProcessElevated()
    {
#if WINDOWS
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
#else
        return false;
#endif
    }

    private static string BuildUninstallArguments(string selector, bool byId, string? version = null, string? source = null, bool interactive = false)
    {
        string args = byId
            ? $"uninstall --id \"{selector}\" --exact"
            : $"uninstall \"{selector}\" --exact";
        if (!string.IsNullOrWhiteSpace(source))
            args += $" --source \"{source}\"";
        if (!string.IsNullOrWhiteSpace(version))
            args += $" --version \"{version}\"";
        return args + (interactive ? " --interactive" : " --silent --disable-interactivity") + " --accept-source-agreements";
    }

    /// <summary>Baixa o instalador de um pacote e aguarda o resultado real do gerenciador de pacotes.</summary>
    public async Task<WingetExecutionResult> DownloadInstallerAsync(
        string appId,
        string destinationFolder,
        string source = "winget",
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(destinationFolder))
            return new WingetExecutionResult { Success = false, ExitCode = -1, Output = "Pacote ou pasta de destino inválidos.", FailureReason = WingetFailureReason.InternalError };

        var bootstrap = await EnsureWingetBootstrappedOnceAsync(onLogReceived).ConfigureAwait(false);
        if (!bootstrap.IsUsable)
            return new WingetExecutionResult { Success = false, ExitCode = -1, Output = bootstrap.ErrorMessage ?? "WinGet indisponível.", FailureReason = WingetFailureReason.InternalError };
        return await DownloadPackageAsync(appId, source, destinationFolder, onLogReceived, cancellationToken).ConfigureAwait(false);
    }

    internal static string QuoteArgument(string value)
    {
        // Windows doubles backslashes before quotes and before the closing delimiter.
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static bool IsSafeUninstallValue(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 256
        && value.IndexOfAny(['"', '\'', ';', '|', '&', '<', '>', '`', '\n', '\r', '$']) < 0;

    /// <summary>
    /// Atualiza um pacote específico via "gerenciador de pacotes update" (alias de "gerenciador de pacotes upgrade"),
    /// silenciosamente e sem interação — mesmos argumentos usados no InstallAppAsync,
    /// mais --include-unknown (necessário pro gerenciador de pacotes atualizar apps cuja versão instalada
    /// ele não consegue detectar com certeza) e --force. O hash só é ignorado quando
    /// solicitado explicitamente para esta operação.
    /// </summary>
    public async Task<WingetExecutionResult> UpdateAppAsync(
        string appId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string source = "winget",
        string? scope = null,
        string? architecture = null,
        bool requiresElevation = false,
        bool elevationProhibited = false,
        bool interactive = false,
        bool ignoreSecurityHash = false,
        string? version = null,
        string? customArguments = null,
        string? overrideArguments = null,
        string? installLocation = null,
        string? installerType = null,
        string? locale = null,
        WinGetVariantSelectionOptions? selection = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Mesma garantia do InstallAppAsync (ver comentário lá) — cobre quem chega direto
        // na tela Atualizações antes de qualquer instalação ter disparado o bootstrap.
        var bootstrapResult = await EnsureWingetBootstrappedOnceAsync(onLogReceived);
        if (!bootstrapResult.IsUsable)
        {
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = $"Winget não está disponível e não foi possível deixá-lo funcional: {bootstrapResult.ErrorMessage}"
            };
        }

        bool useInteractive = interactive || source.Equals("msstore", StringComparison.OrdinalIgnoreCase);
        string? normalizedScope = NormalizeScope(selection?.Scope ?? scope);
        string? normalizedArchitecture = NormalizeArchitecture(selection?.Architecture ?? architecture);
        var commandOptions = new PackageInstallOptions(Interactive: useInteractive, Scope: selection?.Scope ?? normalizedScope,
            Architecture: normalizedArchitecture, IgnoreSecurityHash: ignoreSecurityHash,
            InstallLocation: installLocation, RequiresElevation: requiresElevation, Version: selection?.Version ?? version,
            UpdateArguments: customArguments, OverrideArguments: overrideArguments,
            InstallerType: selection?.InstallerType ?? installerType, Locale: selection?.Locale ?? locale);
        string args = WingetCommandLineBuilder.BuildArguments("upgrade", appId, source, commandOptions);
        var result = await ExecuteInstallWithRecoveryAsync(
            args, onLogReceived, cancellationToken, normalizedScope, normalizedArchitecture,
            requiresElevation,
            preserveScopeOnRetry: WindowsIdentityContext.IsRunningAsSystem(),
            preserveVariantSelection: selection is not null);

        if (CanRepairUpdateWithInstall(result, source, version, cancellationToken.IsCancellationRequested))
        {
            try
            {
                var installed = await ListInstalledPackagesAsync(cancellationToken, source).ConfigureAwait(false);
                if (HasOlderInstalledVersion(installed, appId, version!))
                {
                    onLogReceived?.Invoke("O WinGet não encontrou uma atualização aplicável antes de iniciar o instalador; tentando uma reinstalação única da versão-alvo.");
                    var repair = await InstallAppAsync(appId, onLogReceived, cancellationToken,
                        source: source, scope: normalizedScope, architecture: normalizedArchitecture,
                        requiresElevation: requiresElevation, elevationProhibited: elevationProhibited,
                        interactive: interactive, ignoreSecurityHash: ignoreSecurityHash,
                        version: version, force: true, installerType: selection?.InstallerType,
                        locale: selection?.Locale, selection: selection).ConfigureAwait(false);
                    repair.InstallerStarted = true;
                    repair.Output = repair.Success
                        ? "Reinstalação de reparo executada pelo WinGet; aguardando conferência da versão instalada."
                        : $"A atualização falhou antes do instalador e a reinstalação de reparo também falhou: {repair.Output}";
                    return repair;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException)
            {
                onLogReceived?.Invoke($"Não foi possível conferir a instalação antes do reparo ({exception.GetType().Name}); a reinstalação automática foi omitida.");
            }
        }

        result = NormalizeInstallOutcome(result, isUpgrade: true);
        ApplyVariantSelection(result, selection);
        LogVariantSelection(result, appId, selection, onLogReceived);
        return result;
    }

    internal static bool CanRepairUpdateWithInstall(WingetExecutionResult result, string source,
        string? targetVersion, bool canceled) =>
        !result.Success && !result.InstallerStarted && !result.WasElevated && !canceled
        && string.Equals(source, "winget", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(targetVersion)
        && unchecked((uint)result.ExitCode) is 0x8A150010 or 0x8A15002B or 0x8A15004F;

    internal static bool HasOlderInstalledVersion(IEnumerable<UpgradablePackage> installed,
        string packageId, string targetVersion)
    {
        var matches = installed.Where(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1
            && TryParseNumericVersion(matches[0].CurrentVersion, out var current)
            && TryParseNumericVersion(targetVersion, out var target)
            && current.CompareTo(target) < 0;
    }

    private static bool TryParseNumericVersion(string? value, out Version version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] segments = value.Trim().TrimStart('v', 'V').Split('.');
        if (segments.Length is < 2 or > 4 || segments.Any(segment => segment.Length == 0 || !segment.All(char.IsAsciiDigit)))
            return false;
        if (!Version.TryParse(string.Join('.', segments.Concat(Enumerable.Repeat("0", 4 - segments.Length))), out var parsed))
            return false;
        version = parsed;
        return true;
    }

    /// <summary>
    /// Executa o comando WinGet; quando os metadados já resolveram elevação, inicia o host
    /// pelo Elevator antes do despacho. Falhas retornadas depois do início não são repetidas.
    /// </summary>
    private async Task<WingetExecutionResult> ExecuteWithElevationFallbackAsync(
        string arguments,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken,
        bool forceElevation = false)
    {
        if (forceElevation && !IsCurrentProcessElevated())
        {
            onLogReceived?.Invoke("O instalador requer privilégios de administrador; solicitando autorização...");
            var forcedResult = await ExecuteCliAsync(WindowsArgumentParser.Parse(arguments), onLogReceived,
                cancellationToken, WinGetCliExecutionMode.Elevated);
            if (!forcedResult.Success && forcedResult.FailureReason == WingetFailureReason.Unknown)
                forcedResult.FailureReason = WingetErrorTranslator.Classify(forcedResult.ExitCode, forcedResult.Output);
            return forcedResult;
        }

        var result = await ExecuteWingetCommandAsync(arguments, onLogReceived, cancellationToken);
        if (result.Success || cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        result.FailureReason = WingetErrorTranslator.Classify(result.ExitCode, result.Output);
        return result;
    }

    private async Task<WingetExecutionResult> ExecuteInstallWithRecoveryAsync(
        string arguments,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken,
        string? scope,
        string? architecture,
        bool forceElevation,
        bool preserveScopeOnRetry = false,
        bool preserveVariantSelection = false)
    {
        string effectiveArguments = arguments;
        var result = await ExecuteWithElevationFallbackAsync(
            effectiveArguments, onLogReceived, cancellationToken, forceElevation);

        if (!forceElevation && IsPreStartElevation740(result))
        {
            onLogReceived?.Invoke("O WinGet não iniciou por Win32Exception 740; tentando uma única vez pelo WinProvision Elevator.");
            result = await ExecuteCliAsync(WindowsArgumentParser.Parse(effectiveArguments), onLogReceived,
                cancellationToken, WinGetCliExecutionMode.Elevated).ConfigureAwait(false);
        }

        bool canRelaxScope = scope is not null && !preserveScopeOnRetry && !preserveVariantSelection;
        if (!result.Success && result.FailureReason == WingetFailureReason.NoApplicableInstallers
            && !result.WasElevated && !forceElevation
            && (canRelaxScope || architecture is not null))
        {
            onLogReceived?.Invoke(preserveScopeOnRetry
                ? "A arquitetura escolhida não se aplica; tentando outra seleção sem alterar o escopo de máquina..."
                : "O escopo ou a arquitetura escolhidos não se aplicam; tentando a seleção automática do WinGet...");
            effectiveArguments = arguments;
            if (canRelaxScope)
                effectiveArguments = effectiveArguments.Replace($" --scope {scope}", string.Empty, StringComparison.Ordinal);
            if (architecture is not null)
                effectiveArguments = effectiveArguments.Replace($" --architecture {architecture}", string.Empty, StringComparison.Ordinal);
            result = await ExecuteWithElevationFallbackAsync(
                effectiveArguments, onLogReceived, cancellationToken, forceElevation);
        }

        // Falha de download ocorre antes de iniciar o instalador. Limita a repetição a
        // falhas classificadas como DownloadError: erros de hash, política, elevação ou
        // instalação podem deixar efeitos parciais e não são repetidos automaticamente.
        // O teto explícito evita qualquer ciclo de auto retry; cada volta exige o mesmo
        // código explicitamente elegível, aplica orçamentos total e por código e respeita o cancelamento.
        int totalRetries = 0;
        var retriesByErrorCode = new System.Collections.Generic.Dictionary<int, int>();
        while (WingetAutoRetryPolicy.CanRetry(
            result,
            totalRetries,
            retriesByErrorCode.GetValueOrDefault(result.ExitCode),
            forceElevation,
            cancellationToken.IsCancellationRequested))
        {
            int errorCode = result.ExitCode;
            int retryNumber = totalRetries + 1;
            onLogReceived?.Invoke($"O WinGet informou uma falha transitória de download; nova tentativa {retryNumber} de {WingetAutoRetryPolicy.MaxRetriesPerOperation}...");
            await Task.Delay(WingetAutoRetryPolicy.GetDelay(retryNumber), cancellationToken).ConfigureAwait(false);
            totalRetries++;
            retriesByErrorCode[errorCode] = retriesByErrorCode.GetValueOrDefault(errorCode) + 1;
            result = await ExecuteWithElevationFallbackAsync(
                effectiveArguments, onLogReceived, cancellationToken, forceElevation: false);
        }

        return result;
    }

    internal static bool IsPreStartElevation740(WingetExecutionResult result) =>
        !result.ProcessStarted && !result.InstallerMayHaveStarted && !result.InstallerStarted
        && unchecked((uint)result.ExitCode) == 0x800702E4;

    private static void LogVariantSelection(WingetExecutionResult result, string id,
        WinGetVariantSelectionOptions? selection, Action<string>? log)
    {
        if (selection is null) return;
        string message = $"id={id}; version={selection.Version}; variantConfirmed=false; fixed={string.Join(',', result.VariantFixedFields)}; unfixed={string.Join(',', result.VariantUnfixedFields)}";
        CoreLog.Information("WinGetVariantSelection", message);
        log?.Invoke($"Seleção WinGet não confirmada: variantConfirmed=false; fixados={string.Join(',', result.VariantFixedFields)}; não fixados={string.Join(',', result.VariantUnfixedFields)}.");
    }

    private static WingetExecutionResult NormalizeInstallOutcome(WingetExecutionResult result, bool isUpgrade)
    {
        if (result.Success)
            return result;

        uint code = unchecked((uint)result.ExitCode);
        if (code is 0x8A150109 or 0x8A15010B || (isUpgrade && code == 0x8A15004F))
        {
            result.Success = true;
            result.FailureReason = WingetFailureReason.Unknown;
            result.Output = code switch
            {
                0x8A150109 => "Instalação concluída; é necessário reiniciar o computador.",
                0x8A15010B => "A instalação foi concluída e o computador está reiniciando.",
                _ => "A versão instalada já é igual ou mais recente."
            };
            return result;
        }

        if (code is 0x8A150005 or 0x8A15010C)
            result.FailureReason = WingetFailureReason.OperationCanceled;
        else if (result.FailureReason == WingetFailureReason.Unknown)
            result.FailureReason = WingetErrorTranslator.Classify(result.ExitCode, result.Output);
        return result;
    }

    private static string? NormalizeScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "user" => "user",
        "machine" or "system" => "machine",
        _ => null
    };

    private static void ApplyVariantSelection(WingetExecutionResult result, WinGetVariantSelectionOptions? selection)
    {
        if (selection is null) return;
        result.VariantConfirmed = false;
        result.VariantFixedFields = selection.FixedFields.ToArray();
        result.VariantUnfixedFields = selection.UnfixedFields.ToArray();
    }

    private static string? NormalizeArchitecture(string? architecture) => architecture?.Trim().ToLowerInvariant() switch
    {
        "x86" => "x86",
        "x64" => "x64",
        "arm64" => "arm64",
        "arm" => "arm",
        _ => null
    };

    /// <summary>
    /// Lista os pacotes com atualização pendente ("gerenciador de pacotes upgrade"). Diferente de
    /// GetInstalledPackageIdsAsync (que usa "gerenciador de pacotes export", com saída JSON), esse comando
    /// não tem opção de saída estruturada — a resposta é a tabela de texto padrão do
    /// console, parseada por WingetUpgradeListParser. --include-unknown inclui pacotes cuja
    /// versão instalada o gerenciador de pacotes não consegue confirmar (comuns em apps instalados fora do
    /// gerenciador de pacotes), pra não esconder atualizações reais só por causa disso.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// O gerenciador de pacotes não está disponível e o bootstrap automático não conseguiu deixá-lo
    /// funcional (ver <see cref="WingetBootstrapResult.ErrorMessage"/>). Lançar aqui (em vez
    /// de devolver lista vazia) evita que "nenhuma atualização" seja confundido com "tudo em
    /// dia" quando na verdade o gerenciador de pacotes nem rodou — quem chama já trata isso num try/catch
    /// (ver UpdatesPage.CheckUpdatesButton_Click).
    /// </exception>
    public Task<List<UpgradablePackage>> SearchPackagesAsync(string query, CancellationToken cancellationToken = default)
        => SearchPackagesAsync(query, "winget", false, false, cancellationToken);

    public async Task<List<UpgradablePackage>> SearchPackagesAsync(string query, string source, bool exact, bool byId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (source is not ("winget" or "msstore")) throw new ArgumentException("Fonte inválida.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        var result = await ExecuteWingetCommandAsync(
            $"search {(byId ? "--id" : "--query")} {QuoteArgument(query)} --source {source}" +
            (exact ? " --exact" : "") + " --accept-source-agreements --disable-interactivity", null, cancellationToken);
        // APPINSTALLER_CLI_ERROR_NO_APPLICATIONS_FOUND
        if (unchecked((uint)result.ExitCode) == 0x8A150014) return [];
        if (!result.Success) throw new InvalidOperationException($"Não foi possível pesquisar pelo WinGet: {result.Output}");
        if (!WingetUpgradeListParser.TryParse(result.Output, out var packages, false, true))
            throw new FormatException("O WinGet retornou um formato de pesquisa não reconhecido.");
        if (packages.Any(package => package.Id.Contains('…') || package.Id.EndsWith("...", StringComparison.Ordinal)))
            throw new FormatException("O WinGet truncou identificadores da pesquisa. Refine o nome pesquisado.");
        return packages.Select(package => new UpgradablePackage
        {
            Id = package.Id, Name = package.Name, Source = source, CurrentVersion = package.CurrentVersion
        }).ToList();
    }

    public async Task<string> GetPackageDetailsAsync(string id, string source = "winget", CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (source is not ("winget" or "msstore")) throw new ArgumentException("Fonte inválida.", nameof(source));
        var result = await ExecuteWingetCommandAsync(
            $"show --id {QuoteArgument(id)} --exact --source {source} --accept-source-agreements --disable-interactivity", null, cancellationToken);
        if (!result.Success) throw new InvalidOperationException($"Não foi possível consultar os detalhes: {result.Output}");
        return result.Output;
    }

    public async Task<List<UpgradablePackage>> GetUpgradablePackagesAsync(Action<string>? onLogReceived = null, CancellationToken cancellationToken = default, bool includeMicrosoftStore = true)
    {
        var bootstrapResult = await EnsureWingetBootstrappedOnceAsync(onLogReceived);
        if (!bootstrapResult.IsUsable)
        {
            throw new InvalidOperationException(
                $"Winget não está disponível e não foi possível deixá-lo funcional: {bootstrapResult.ErrorMessage}");
        }

        string args = "upgrade --include-unknown --accept-source-agreements --disable-interactivity";
        if (!includeMicrosoftStore) args += " --source winget";
        // onLogReceived não é passado pra execução do comando em si — a saída aqui é a
        // tabela padrão do console (cabeçalhos, colunas), não faz sentido linha a linha
        // como "log" de progresso; o parsing estruturado é feito por WingetUpgradeListParser
        // logo abaixo. onLogReceived serve só pro bootstrap (chamado acima).
        var result = await ExecuteWingetCommandAsync(args, onLogReceived: null, cancellationToken);

        if (!result.Success)
        {
            string detail = string.IsNullOrWhiteSpace(result.Output)
                ? $"código {result.ExitCode}"
                : result.Output.Trim();
            throw new InvalidOperationException($"Não foi possível consultar atualizações pelo WinGet: {detail}");
        }

        if (!WingetUpgradeListParser.TryParse(result.Output, out var packages))
        {
            throw new InvalidOperationException(
                "O WinGet concluiu a consulta, mas a saída de atualizações estava vazia ou em um formato não reconhecido.");
        }

        return packages;
    }

    /// <summary>
    /// Obtém o tamanho do instalador sem baixar o pacote — usado só como fallback ao
    /// vivo para pacotes que não têm InstallerSizeBytes já calculado no catálogo (ver
    /// AppEntry.InstallerSizeBytes, preenchido pelo Indexer na sincronização diária a
    /// partir da própria InstallerUrl do manifesto catálogo de pacotes — não existe um campo
    /// de tamanho no schema oficial do gerenciador de pacotes, então essa é a única fonte confiável).
    /// Consulta apenas os headers HTTP (HEAD/Range) das URLs de instalador reportadas
    /// por "gerenciador de pacotes show", sem baixar o instalador inteiro.
    /// </summary>
    public async Task<long?> GetPackageInstallerSizeAsync(string appId, CancellationToken cancellationToken = default)
    {
        var bootstrapResult = await EnsureWingetBootstrappedOnceAsync(null);
        if (!bootstrapResult.IsUsable)
            return null;

        string args = $"show --id \"{appId}\" --exact --source winget --accept-source-agreements --disable-interactivity";
        var result = await ExecuteWingetCommandAsync(args, null, cancellationToken);
        if (string.IsNullOrWhiteSpace(result.Output))
            return null;

        // Limitado às 2 primeiras URLs (ex.: pacotes com várias arquiteturas/instaladores
        // como o VLC chegam a ter 3-4). Cada URL pode disparar até duas requisições HTTP
        // (HEAD + fallback via Range GET), então testar todas as URLs de um pacote com
        // muitos instaladores podia levar bem mais de um minuto e deixar a tela presa em
        // "Calculando…" por tempo demais — a primeira URL que responder já é suficiente.
        foreach (string url in WinGetCliTableParser.ParseInstallerUrls(result.Output).Take(2))
        {
            long? remoteSize = await InstallerSizeResolver.TryGetRemoteContentLengthAsync(url, cancellationToken);
            if (remoteSize is > 0)
                return remoteSize;
        }

        return null;
    }

    /// <summary>
    /// Lista os Package Ids atualmente instalados via "gerenciador de pacotes export" (JSON estruturado
    /// gerado pelo próprio gerenciador de pacotes), em vez de parsear a saída de texto do console —
    /// evita quebra por locale/versão do gerenciador de pacotes. Usado pela reconciliação de perfis
    /// e pela contagem de updates.
    /// </summary>
    public async Task<List<UpgradablePackage>> ListInstalledPackagesAsync(CancellationToken cancellationToken = default, string? source = null)
    {
        if (source is not (null or "msstore" or "winget")) throw new ArgumentException("Fonte inválida.", nameof(source));
        string arguments = "list --accept-source-agreements --disable-interactivity" + (source is null ? "" : " --source " + source);
        var result = await ExecuteWingetCommandAsync(arguments, null, cancellationToken);
        if (!result.Success || !WingetUpgradeListParser.TryParse(result.Output, out var packages,
            requireAvailableVersion: false, allowMissingAvailableColumn: true, sourceIfMissing: source))
            throw new InvalidOperationException("Não foi possível interpretar o inventário WinGet: " + result.Output);
        return packages;
    }

    public async Task<List<string>> GetInstalledPackageIdsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await TryGetInstalledPackageIdsAsync(cancellationToken);
        return snapshot.PackageIds;
    }

    /// <summary>
    /// Reads the installed package snapshot from gerenciador de pacotes export and distinguishes a valid
    /// empty inventory from a failed export, so callers can safely fall back to another source.
    /// </summary>
    public async Task<(bool Succeeded, List<string> PackageIds)> TryGetInstalledPackageIdsAsync(
        CancellationToken cancellationToken = default)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"winprovision-export-{Guid.NewGuid():N}.json");

        try
        {
            string args = $"export -o \"{tempFile}\" --accept-source-agreements --include-versions";
            var result = await ExecuteWingetCommandAsync(args, onLogReceived: null, cancellationToken);

            if (!result.Success || !File.Exists(tempFile))
                return (false, []);

            string json = await File.ReadAllTextAsync(tempFile, cancellationToken);
            return WinGetCliExportParser.TryParseInstalledPackageIds(json, out IReadOnlyList<string> ids)
                ? (true, ids.ToList()) : (false, []);
        }
        catch (JsonException)
        {
            return (false, []);
        }
        finally
        {
            TryDeleteFile(tempFile);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception recoveryException)
        {
            WinProvision.Core.Services.CoreLog.Warning(recoveryException, "WingetExecutor.TryDeleteFile");

            // Arquivo temporário órfão não é crítico; melhor esforço de limpeza.
        }
    }

    /// <summary>
    /// Passa pela porta única de provisionamento (<see cref="WingetBootstrapper.EnsureOnceAsync"/>),
    /// compartilhada com WinGetService, App e /auto: o gerenciador de pacotes é provisionado uma única vez
    /// por sessão (sucesso em cache; falha reavaliada depois de um tempo), e quem chega
    /// depois reaproveita o MESMO resultado em vez de rodar "gerenciador de pacotes --version" a cada clique.
    /// O trabalho em si não é cancelado se a PRIMEIRA operação que o disparou for cancelada.
    /// </summary>
    private Task<WingetBootstrapResult> EnsureWingetBootstrappedOnceAsync(Action<string>? onLogReceived)
    {
        if (_bootstrapper is null)
        {
            // Sem WingetBootstrapper injetado, tenta rodar o cliente de pacotes diretamente.
            return Task.FromResult(new WingetBootstrapResult(WingetBootstrapStatus.AlreadyAvailable));
        }

        return _bootstrapper.EnsureOnceAsync(onLogReceived);
    }

    public async Task<WingetExecutionResult> DownloadPackageAsync(string id, string source, string? directory, Action<string>? log, CancellationToken token, string? version = null)
    {
        using var slot = await OperationLimits.Downloads.EnterAsync(token).ConfigureAwait(false);
        string folder = InstallerDownloadPolicy.ResolveDirectory(directory, _operationPreferences);
        Directory.CreateDirectory(folder);
        string staging = Path.Combine(folder, ".winprovision-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var args = new List<string> { "download", "--id", id, "--exact", "--source", source,
                "--accept-source-agreements", "--accept-package-agreements", "--disable-interactivity", "--download-directory", staging };
            if (version is not null) args.AddRange(["--version", version]);
            var result = await ExecuteWingetCommandAsync("", log, token, args).ConfigureAwait(false);
            if (!result.Success) return result;
            var options = _operationPreferences.Load();
            var files = Directory.EnumerateFiles(staging, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToArray();
            foreach (string file in files)
            {
                token.ThrowIfCancellationRequested();
                string original = Path.GetFileName(file);
                bool metadata = Path.GetExtension(file).ToLowerInvariant() is ".yaml" or ".yml" or ".json";
                // Native names contain the resolved version/architecture; preserve them when these are unknown.
                string name = metadata ? original : InstallerDownloadPolicy.FileName(options.InstallerNames, id, version, null, original);
                string published = InstallerDownloadPolicy.Publish(file, folder, name);
                log?.Invoke($"Arquivo baixado: {published}");
            }
            return result;
        }
        finally
        {
            try { Directory.Delete(staging, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLog.Warning(ex, "Downloads.Cleanup"); }
        }
    }

    public Task<WingetExecutionResult> ExportPackagesAsync(string path, Action<string>? log, CancellationToken token)
        => ExecuteWingetCommandAsync("", log, token, ["export", "--output", Path.GetFullPath(path), "--accept-source-agreements", "--disable-interactivity"]);

    // CLI removal stays exact: no fallback to another ID or display name.
    public Task<WingetExecutionResult> UninstallExactPackageAsync(string id, string? source, Action<string>? log, CancellationToken token)
    {
        var args = new List<string> { "uninstall", "--id", id, "--exact", "--silent", "--accept-source-agreements", "--disable-interactivity" };
        if (source is not null) args.AddRange(["--source", source]);
        return ExecuteWingetCommandAsync("", log, token, args);
    }

    private async Task<WingetExecutionResult> ExecuteWingetCommandAsync(string arguments, Action<string>? onLogReceived, CancellationToken cancellationToken, IReadOnlyList<string>? argumentList = null)
    {
        IReadOnlyList<string> tokens = argumentList ?? WindowsArgumentParser.Parse(arguments);
        return await ExecuteCliAsync(tokens, onLogReceived, cancellationToken, WinGetCliExecutionMode.CurrentUser).ConfigureAwait(false);
    }

    public async Task<WingetExecutionResult> ExecuteCliAsync(IReadOnlyList<string> tokens, Action<string>? onLogReceived,
        CancellationToken cancellationToken, WinGetCliExecutionMode mode = WinGetCliExecutionMode.CurrentUser)
    {
        if (tokens.Count == 0) throw new ArgumentException("Informe uma operação WinGet.", nameof(tokens));
        string verb = tokens[0].ToLowerInvariant();
        WinGetCliOperation operation = verb switch
        {
            "--version" => WinGetCliOperation.Probe,
            "search" or "find" => WinGetCliOperation.Search,
            "list" or "ls" => WinGetCliOperation.List,
            "show" or "view" => WinGetCliOperation.Show,
            "upgrade" or "update" when tokens.Count == 1 || tokens.Contains("--all") => WinGetCliOperation.UpgradeList,
            "upgrade" or "update" => WinGetCliOperation.Upgrade,
            "install" or "add" => WinGetCliOperation.Install,
            "uninstall" or "remove" or "rm" => WinGetCliOperation.Uninstall,
            "download" => WinGetCliOperation.Download,
            "export" => WinGetCliOperation.Export,
            "source" => WinGetCliOperation.Source,
            _ => throw new ArgumentOutOfRangeException(nameof(tokens), $"Operação WinGet não suportada pelo módulo: {verb}.")
        };
        bool mutation = operation is WinGetCliOperation.Install or WinGetCliOperation.Upgrade or WinGetCliOperation.Uninstall;
        var invocation = new WinGetCliInvocation(operation, tokens.Skip(1).ToArray(), mode, mutation);
        IProgress<WinGetCliEvent>? progress = onLogReceived is null ? null : new CallbackCliProgress(onLogReceived);
        string preferredExecutable = WingetLocator.ExecutablePath;
        if (_cliClient is WinGetCliClient concrete && Path.IsPathFullyQualified(preferredExecutable)) concrete.SetPreferredPath(preferredExecutable);
        var timer = WingetCliAudit.Launch(preferredExecutable, string.Join(" ", tokens));
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        WinGetCliResult result = operation switch
        {
            WinGetCliOperation.Probe => await _cliClient.ProbeAsync(cancellationToken).ConfigureAwait(false),
            WinGetCliOperation.Search => await _cliClient.SearchAsync(invocation, progress, cancellationToken).ConfigureAwait(false),
            WinGetCliOperation.List => await _cliClient.ListInstalledAsync(invocation, progress, cancellationToken).ConfigureAwait(false),
            WinGetCliOperation.Show => await _cliClient.ShowAsync(invocation, progress, cancellationToken).ConfigureAwait(false),
            WinGetCliOperation.UpgradeList => await _cliClient.DiscoverUpdatesAsync(invocation, progress, cancellationToken).ConfigureAwait(false),
            WinGetCliOperation.Install => await _cliClient.InstallAsync(invocation, progress, cancellationToken).ConfigureAwait(false),
            WinGetCliOperation.Upgrade => await _cliClient.UpgradeAsync(invocation, progress, cancellationToken).ConfigureAwait(false),
            WinGetCliOperation.Uninstall => await _cliClient.UninstallAsync(invocation, progress, cancellationToken).ConfigureAwait(false),
            WinGetCliOperation.Download => await _cliClient.DownloadAsync(invocation, progress, cancellationToken).ConfigureAwait(false),
            WinGetCliOperation.Export => await _cliClient.ExportAsync(invocation, progress, cancellationToken).ConfigureAwait(false),
            WinGetCliOperation.Source => await _cliClient.ManageSourcesAsync(invocation, progress, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(tokens))
        };
        DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
        string output = string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }.Where(value => !string.IsNullOrWhiteSpace(value)));
        if (!string.IsNullOrWhiteSpace(output) && onLogReceived is not null && progress is null) onLogReceived(output);
        int code = result.ExitCode ?? result.HResult ?? (result.Status switch
        {
            WinGetCliStatus.UacCancelled or WinGetCliStatus.Cancelled => 1223,
            WinGetCliStatus.ElevationRejected => 1625,
            WinGetCliStatus.InvalidCredentials => 1326,
            WinGetCliStatus.HelperError => 1627,
            _ => 1
        });
        WingetCliAudit.Result(preferredExecutable, code, result.Success, timer, string.Join(" ", tokens));
        if (_taskExecutor is not null)
            await _taskExecutor.RecordExternalProcessResultAsync(new AppLogTaskDescriptor(PackageManagerLogCategories.Winget,
                    "WinGet", ClassifyTaskType(tokens, string.Empty), "WinGet"), preferredExecutable,
                CommandLineDisplayFormatter.FormatPowerShellArguments(tokens), startedAt, finishedAt, code,
                result.StandardOutput, result.StandardError, cancellationToken).ConfigureAwait(false);
        return new WingetExecutionResult
        {
            Success = result.Success,
            ExitCode = code,
            Output = output.Length == 0 ? result.TechnicalMessage ?? result.Diagnostic ?? string.Empty : output,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            WasElevated = mode == WinGetCliExecutionMode.Elevated,
            WingetUnavailable = result.Status == WinGetCliStatus.Unavailable,
            ProcessStarted = result.ProcessStarted,
            InstallerMayHaveStarted = result.InstallerMayHaveStarted,
            InstallerStarted = result.InstallerMayHaveStarted,
            CliStatus = result.Status,
            RebootRequired = result.RebootRequired,
            FailureReason = result.Status switch
            {
                WinGetCliStatus.UacCancelled or WinGetCliStatus.Cancelled => WingetFailureReason.ElevationCanceled,
                WinGetCliStatus.ElevationRejected => WingetFailureReason.BlockedByPolicy,
                WinGetCliStatus.InvalidCredentials or WinGetCliStatus.HelperError => WingetFailureReason.InternalError,
                WinGetCliStatus.PackageNotFound => WingetFailureReason.CatalogError,
                WinGetCliStatus.NoUpdates => WingetFailureReason.NoApplicableUpgrade,
                WinGetCliStatus.HashMismatch => WingetFailureReason.InstallerHashMismatch,
                WinGetCliStatus.SourceFailed => WingetFailureReason.CatalogError,
                _ => WingetFailureReason.Unknown
            }
        };
    }

    private sealed class CallbackCliProgress(Action<string> callback) : IProgress<WinGetCliEvent>
    {
        public void Report(WinGetCliEvent value) { if (value.Line.Length > 0) callback(value.Line); }
    }

    private static string ClassifyTaskType(IReadOnlyList<string>? args, string raw) =>
        (args?.FirstOrDefault() ?? raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())?.ToLowerInvariant() switch
        {
            "source" when args?.Skip(1).FirstOrDefault()?.Equals("list", StringComparison.OrdinalIgnoreCase) == true => "ListSources",
            "source" => "UpdateSources",
            "search" or "find" => "Search",
            "list" when args?.Any(argument => argument.Equals("--upgrade-available", StringComparison.OrdinalIgnoreCase)) == true => "ListUpdates",
            "list" => "ListInstalled",
            "install" => "Instalar",
            "upgrade" => "Atualizar",
            "uninstall" => "Desinstalar",
            _ => "Other"
        };


    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            CoreLog.Warning(exception, "ProcessCleanup");
        }
    }
}
