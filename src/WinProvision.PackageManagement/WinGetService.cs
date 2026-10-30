using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Management.Deployment;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

/// <summary>
/// Serviço para interação com o gerenciador de pacotes usando a API COM, com fallback para a CLI.
/// </summary>
public sealed partial class WinGetService
{
    // Tempo máximo até o PRIMEIRO callback de progresso (o vigia é cancelado no primeiro callback).
    // A primeira ativação do servidor COM + atualização de fonte pode passar de 1 minuto em máquina
    // recém-provisionada ou rede lenta; 60s cancelava a COM cedo demais e forçava o cliente de pacotes.
    private static readonly TimeSpan ComProgressTimeout = TimeSpan.FromSeconds(180);
    private const int MaxPreflightAttempts = 3;
    private static readonly TimeSpan ProgressUpdateInterval = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan UpdateQueryTimeout = TimeSpan.FromSeconds(20);
    private readonly WingetExecutor _wingetExecutor;
    private readonly WinProvisionApiService _apiService;
    private readonly WinGetPkgsManifestService _winGetPkgs = new();
    private readonly Func<Action<string>?, CancellationToken, Task<WingetBootstrapResult>> _provision;
    private readonly Func<PackageInstallMethod> _preferredMethod;
    private readonly Func<PackageUpdateMethod> _preferredUpdateMethod;
    private readonly Func<CancellationToken, Task<List<UpgradablePackage>>> _apiInventory;
    private int _postProvisionHandled;
    private readonly WingetBootstrapper _bootstrapper;
    private readonly Action<string> _diagnostic;
    private readonly StuckUpgradeTracker _stuckUpgrades;
    private readonly ConcurrentDictionary<string, UpgradablePackage> _offeredUpgrades = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _localIdentifiers = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _comReadinessGate = new(1, 1);
    private bool _comReadinessChecked;

    /// <summary>
    /// Qual operação COM disparar em <see cref="RunComPackageOperationAsync"/>:
    /// <see cref="Install"/> chama PackageManager.InstallPackageAsync (instalação "fresh");
    /// <see cref="Upgrade"/> chama PackageManager.UpgradePackageAsync no mesmo CatalogPackage
    /// (atualização de uma instalação existente) — mesmo InstallOptions, mesmo InstallResult/
    /// InstallProgress, só o método da COM que muda.
    /// </summary>
    private enum ComOperationKind { Install, Upgrade, Uninstall }

    /// <summary>Marca o instante em que a instalação foi entregue ao servidor COM (a partir daí não se repete).</summary>
    private sealed partial class InstallAttemptState
    {
        public volatile bool Started;
        public volatile bool InstallerStarted;
        public InstallerPreferences? Preferences;
    }

    internal sealed record InstallerPreferences(
        string? Scope,
        string? Architecture,
        bool RequiresElevation,
        bool ElevationProhibited);

    /// <summary>
    /// IProgress&lt;T&gt; que chama o handler de forma síncrona, no thread que invoca Report.
    /// Progress&lt;T&gt; normal capturaria o SynchronizationContext do Task.Run (que é nulo) e
    /// enfileiraria cada Report no ThreadPool, sem garantir ordem entre callbacks sucessivos.
    /// </summary>
    private sealed partial class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    public WinGetService(
        WingetExecutor wingetExecutor,
        WinProvisionApiService apiService,
        WingetBootstrapper bootstrapper,
        Func<PackageInstallMethod>? preferredMethod = null,
        Func<Action<string>?, CancellationToken, Task<WingetBootstrapResult>>? provision = null,
        Action<string>? diagnostic = null,
        Func<PackageUpdateMethod>? preferredUpdateMethod = null,
        Func<CancellationToken, Task<List<UpgradablePackage>>>? apiInventory = null,
        string? stuckUpgradeStatePath = null)
    {
        _bootstrapper = bootstrapper;
        _diagnostic = diagnostic ?? PackageDiagnostics.Write;
        _wingetExecutor = wingetExecutor;
        _apiService = apiService;
        _provision = provision ?? ((log, token) => bootstrapper.EnsureOnceAsync(log, token));
        _preferredMethod = preferredMethod ?? (() => PackageInstallMethod.Automatic);
        _preferredUpdateMethod = preferredUpdateMethod ?? (() => PackageUpdateMethod.Automatic);
        _apiInventory = apiInventory ?? ReadRegistryInventoryAsync;
        _stuckUpgrades = new StuckUpgradeTracker(stuckUpgradeStatePath);
    }

    /// <summary>
    /// Provisiona o gerenciador de pacotes e aquece COM sob demanda. O fluxo de instalação automática não usa
    /// esse método antes da primeira tentativa COM; chama-o somente quando precisar do CLI.
    /// </summary>
    public async Task PrepareAsync(
        CancellationToken cancellationToken = default,
        bool includeMicrosoftStoreCatalog = false,
        Action<string>? onLogReceived = null)
    {
        try
        {
            Action<string> logSink = onLogReceived ?? PackageDiagnostics.Write;
            if (!await EnsureWingetProvisionedAsync(logSink, cancellationToken).ConfigureAwait(false))
            {
                onLogReceived?.Invoke("[WinProvision] O WinGet não ficou disponível; a etapa COM será pulada e a cadeia seguirá pela WinProvision API, com CLI se puder ser provisionado.");
                return;
            }
            onLogReceived?.Invoke("[WinProvision] Aquecendo a API COM do WinGet para os pacotes selecionados…");
            await WinGetFactoryHelper.ProbeAsync(cancellationToken, includeMicrosoftStoreCatalog, onLogReceived)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PackageDiagnostics.Write($"PREPARE falhou {ex.GetType().Name}: {ex.Message}");
            onLogReceived?.Invoke($"[WinProvision] Aquecimento do WinGet não concluído; a instalação tentará a cadeia normal. Motivo: {ex.Message}");
        }
    }

    /// <summary>
    /// Provisiona o gerenciador de pacotes apenas quando pesquisa/preparação ou o CLI são necessários. Falha
    /// NÃO aborta a API própria, e o que
    /// exigir o gerenciador de pacotes de verdade (cliente de pacotes, ODT via gerenciador de pacotes) falha com mensagem própria.
    /// </summary>
    private async Task<bool> EnsureWingetProvisionedAsync(Action<string>? onLog, CancellationToken cancellationToken)
    {
        WingetBootstrapResult result;
        try
        {
            result = await _provision(onLog, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PackageDiagnostics.Write($"WINGET PROVISION exceção {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        if (!result.IsUsable)
        {
            PackageDiagnostics.Write($"WINGET PROVISION falhou: {result.ErrorMessage}");
            onLog?.Invoke(
                "Winget indisponível e não foi possível provisioná-lo; seguindo com a API própria da WinProvision Store.");
            return false;
        }

        PackageDiagnostics.Write($"WINGET PROVISION status={result.Status} exe={WingetLocator.ExecutablePath}");

        // Só na PRIMEIRA vez que o gerenciador de pacotes aparece nesta sessão: falhas de COM anteriores
        // (App Installer ausente) deixam de valer, então a COM ganha uma tentativa limpa.
        if (result.Status == WingetBootstrapStatus.Bootstrapped &&
            Interlocked.Exchange(ref _postProvisionHandled, 1) == 0)
        {
            WinGetFactoryHelper.ResetAfterProvisioning("winget provisionado nesta sessão");
        }
        if (WinGetFactoryHelper.Mode != WinGetMode.CliOnly)
        {
            await _comReadinessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_comReadinessChecked)
                {
                    await _bootstrapper.EnsureComAsync(CheckComActivationAsync, onLog ?? _diagnostic, cancellationToken).ConfigureAwait(false);
                    _comReadinessChecked = true;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _comReadinessChecked = true;
                PackageDiagnostics.Write($"COM provisionamento falhou: {ex.Message}");
                onLog?.Invoke("Não foi possível reparar a API COM; seguindo com o fallback.");
            }
            finally { _comReadinessGate.Release(); }
        }
        return true;
    }

    private Task<bool> CheckComActivationAsync(CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        WinGetFactoryHelper.ResetAfterProvisioning("verificando ativação COM");
        try
        {
            _ = WinGetFactoryHelper.CreateResilientPackageManager();
            _ = WinGetFactoryHelper.CreateFindPackagesOptions();
            _ = WinGetFactoryHelper.CreatePackageMatchFilter();
            WinGetFactoryHelper.ReportComSuccess();
            return true;
        }
        catch (Exception ex) when (WinGetFactoryHelper.IsActivationFailure(ex) || ex is InvalidCastException)
        {
            _diagnostic($"COM ativação indisponível: 0x{ex.HResult:X8} {ex.Message}");
            WinGetFactoryHelper.DisableComForSession(ex);
            return false;
        }
    }, token);

    public record WinGetPackageMatch(
        string PackageId,
        string Name,
        string Version,
        string Publisher,
        string Source);

    public async Task<IReadOnlyList<WinGetPackageMatch>> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        await EnsureWingetProvisionedAsync(PackageDiagnostics.Write, cancellationToken).ConfigureAwait(false);

        Task<IReadOnlyList<WinGetPackageMatch>> storeSearch =
            WinGetFactoryHelper.CliFallbackAllowed
                ? SearchCliFallbackAsync(query, cancellationToken, source: "msstore")
                : Task.FromResult<IReadOnlyList<WinGetPackageMatch>>([]);

        IReadOnlyList<WinGetPackageMatch> results;
        if (WinGetFactoryHelper.IsComDisabled)
        {
            PackageDiagnostics.Write($"COM DESATIVADO NA SESSÃO: motivo={WinGetFactoryHelper.DisabledReason}");
            results = await SearchCliFallbackAsync(query, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            try
            {
                results = await Task.Run(
                    () => SearchApiAsync(query, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                WinGetFactoryHelper.DisableComForSession(ex);
                if (!WinGetFactoryHelper.CliFallbackAllowed)
                {
                    PackageDiagnostics.Write($"SEARCH COM-ONLY exception={ex}");
                    throw;
                }

                PackageDiagnostics.Write(
                    $"SEARCH FALLBACK exception={ex.GetType().FullName} " +
                    $"hresult=0x{ex.HResult:X8} message=\"{ex.Message}\" stack={ex}");
                PackageDiagnostics.Write("FALLBACK PARA CLI: motivo=COM search exception");
                WinProvision.Core.Services.CoreLog.Debug($"WinGet COM search failed; falling back to winget.exe: {ex}");
                results = await SearchCliFallbackAsync(query, cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<WinGetPackageMatch> storeResults = await storeSearch.ConfigureAwait(false);
        return PackageMatchDeduplicator.Deduplicate([results, storeResults], cancellationToken);
    }

    public Task<WingetExecutionResult> InstallAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string? installLocation = null,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget")
        => RunInstallOrUpgradeAsync(
            ComOperationKind.Install, packageId, onLogReceived, cancellationToken, installLocation, onProgress, source);

    public Task<WingetExecutionResult> InstallPreferredAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        PackageInstallOptions? options = null,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget")
        => InstallWithMethodAsync(packageId, _preferredMethod(), onLogReceived,
            cancellationToken, options?.InstallLocation, onProgress, source,
            options?.Version, options?.Scope, options?.Architecture, options);

    public Task<WingetExecutionResult> InstallPreferredVersionAsync(
        string packageId, string version, Action<string>? onLogReceived,
        CancellationToken cancellationToken, string? installLocation,
        Action<InstallProgressUpdate>? onProgress, string source,
        string? scope, string? architecture)
        => InstallPreferredAsync(packageId, onLogReceived, cancellationToken,
            new PackageInstallOptions(Scope: scope, Architecture: architecture,
                InstallLocation: installLocation, Version: version), onProgress, source);

    public async Task<WingetExecutionResult> InstallWithMethodAsync(
        string packageId,
        PackageInstallMethod method,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string? installLocation = null,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget",
        string? version = null,
        string? preferredScope = null,
        string? preferredArchitecture = null,
        PackageInstallOptions? options = null)
    {
        options ??= new PackageInstallOptions(InstallLocation: installLocation, Version: version,
            Scope: preferredScope, Architecture: preferredArchitecture);
        // Compatibilidade com preferências antigas: esse modo duplicava a API própria.
        if (method == PackageInstallMethod.WinGetPkgsViper)
            method = PackageInstallMethod.Automatic;
        // COM/API não expõem todas as opções do comando install. Encaminhe as opções
        // que não podem ser aplicadas com segurança ao CLI, sem perder a seleção do usuário.
        if (method == PackageInstallMethod.ComApi && RequiresWingetCliForInstallOptions(options))
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = "A API COM não oferece suporte à versão, arquitetura, argumentos personalizados ou à opção de ignorar o hash solicitados. Escolha Automático ou WinGet CLI."
            };
        if (method == PackageInstallMethod.WinProvisionApi
            && RequiresWingetCliForApiInstallOptions(options))
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = -1,
                Output = "A WinProvision API não oferece suporte ao local, modo interativo, argumentos personalizados ou à opção de ignorar o hash solicitados. Escolha Automático ou WinGet CLI."
            };
        if (method == PackageInstallMethod.Automatic && RequiresWingetCliForInstallOptions(options))
        {
            onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe));
            await EnsureWingetProvisionedAsync(onLogReceived, cancellationToken).ConfigureAwait(false);
            var versionPreferences = await ResolveInstallerElevationAsync(packageId, source, cancellationToken).ConfigureAwait(false);
            return await _wingetExecutor.InstallAppAsync(packageId, onLogReceived, cancellationToken,
                installLocation, source, options.Scope ?? preferredScope ?? versionPreferences?.Scope,
                options.Architecture ?? preferredArchitecture ?? versionPreferences?.Architecture,
                options.RequiresElevation || (versionPreferences?.RequiresElevation ?? false),
                versionPreferences?.ElevationProhibited ?? false, options.Interactive,
                options.IgnoreSecurityHash, options.Version, options.CustomArguments).ConfigureAwait(false);
        }
        switch (method)
        {
            case PackageInstallMethod.Automatic:
                return await RunInstallOrUpgradeAsync(ComOperationKind.Install, packageId,
                    onLogReceived, cancellationToken, installLocation, onProgress, source,
                    options: options).ConfigureAwait(false);
            case PackageInstallMethod.ComApi:
                onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.ComApi));
                return await RunInstallOrUpgradeAsync(ComOperationKind.Install, packageId,
                    onLogReceived, cancellationToken, installLocation, onProgress, source,
                    forceComOnly: true, options: options).ConfigureAwait(false);
            case PackageInstallMethod.WingetCli:
                onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe));
                await EnsureWingetProvisionedAsync(onLogReceived, cancellationToken).ConfigureAwait(false);
                var cliPreferences = await ResolveInstallerElevationAsync(packageId, source, cancellationToken).ConfigureAwait(false);
                return await _wingetExecutor.InstallAppAsync(packageId, onLogReceived, cancellationToken,
                    installLocation, source, options.Scope ?? cliPreferences?.Scope, options.Architecture ?? cliPreferences?.Architecture,
                    options.RequiresElevation || (cliPreferences?.RequiresElevation ?? false),
                    cliPreferences?.ElevationProhibited ?? false, options.Interactive, options.IgnoreSecurityHash,
                    options.Version, options.CustomArguments).ConfigureAwait(false);
            case PackageInstallMethod.WinProvisionApi:
                // Método explícito: tenta exclusivamente o catálogo da API própria.
                if (string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase))
                    return new WingetExecutionResult { Success = false, ExitCode = -1,
                        Output = "A WinProvision API não publica pacotes da Microsoft Store.",
                        FailureReason = WingetFailureReason.PackageNotInCatalog };
                onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.OwnApi));
                try
                {
                    var apiPreferences = await ResolveInstallerElevationAsync(packageId, source, cancellationToken).ConfigureAwait(false);
                    var result = await TryInstallApiWithCompatibilityRetryAsync(packageId,
                        onLogReceived is null ? null : new Progress<string>(onLogReceived),
                        cancellationToken,
                        options.Architecture ?? preferredArchitecture ?? apiPreferences?.Architecture,
                        options.Scope ?? preferredScope ?? apiPreferences?.Scope,
                        onProgress, source,
                        options.RequiresElevation || (apiPreferences?.RequiresElevation ?? false),
                        apiPreferences?.ElevationProhibited ?? false, options.Version).ConfigureAwait(false);
                    if (result.Outcome is WinProvisionInstallOutcome.RequiresWinget
                        or WinProvisionInstallOutcome.PackageNotFound
                        or WinProvisionInstallOutcome.NoCompatibleInstaller)
                    {
                        string fallbackSource = result.Outcome == WinProvisionInstallOutcome.RequiresWinget
                            ? "msstore"
                            : source;
                        onLogReceived?.Invoke(result.Message
                            ?? "A API própria não contém um instalador compatível; preparando o WinGet sob demanda.");
                        onProgress?.Invoke(new InstallProgressUpdate(
                            InstallProgressPhase.Preparing,
                            Method: WingetMethod.WingetExe));
                        await EnsureWingetProvisionedAsync(onLogReceived, cancellationToken).ConfigureAwait(false);
                        return await _wingetExecutor.InstallAppAsync(
                            packageId,
                            onLogReceived,
                            cancellationToken,
                            installLocation,
                            source: fallbackSource,
                            scope: options.Scope ?? preferredScope,
                            architecture: options.Architecture ?? preferredArchitecture,
                            version: options.Version,
                            interactive: options.Interactive,
                            ignoreSecurityHash: options.IgnoreSecurityHash,
                            requiresElevation: options.RequiresElevation,
                            customArguments: options.CustomArguments).ConfigureAwait(false);
                    }

                    return new WingetExecutionResult
                    {
                        Success = result.Outcome == WinProvisionInstallOutcome.Success,
                        ExitCode = result.ExitCode ?? (result.Outcome == WinProvisionInstallOutcome.Success ? 0 : -1),
                        Output = result.Message ?? (result.Outcome == WinProvisionInstallOutcome.Success
                            ? "Instalado pela API da WinProvision Store."
                            : $"A API da WinProvision Store não concluiu a instalação: {result.Outcome}."),
                        InstallerStarted = result.InstallerStarted,
                        FailureReason = result.Outcome == WinProvisionInstallOutcome.Success
                            ? WingetFailureReason.Unknown
                            : WingetErrorTranslator.Classify(result.ExitCode ?? -1, result.Message) is var classified
                                && classified != WingetFailureReason.Unknown ? classified
                            : result.Outcome switch
                            {
                                WinProvisionInstallOutcome.PackageNotFound => WingetFailureReason.PackageNotInCatalog,
                                WinProvisionInstallOutcome.NoCompatibleInstaller => WingetFailureReason.NoApplicableInstallers,
                                WinProvisionInstallOutcome.DownloadFailed => WingetFailureReason.DownloadError,
                                WinProvisionInstallOutcome.HashMismatch => WingetFailureReason.InstallerHashMismatch,
                                WinProvisionInstallOutcome.ElevationCanceled => WingetFailureReason.ElevationCanceled,
                                _ => WingetFailureReason.InstallError
                            }
                    };
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return new WingetExecutionResult
                    {
                        Success = false,
                        ExitCode = ex.HResult,
                        Output = $"Falha na API da WinProvision Store: {ex.Message}",
                        FailureReason = WingetFailureReason.InternalError
                    };
                }
            default:
                return await InstallAsync(packageId, onLogReceived, cancellationToken,
                    installLocation, onProgress, source).ConfigureAwait(false);
        }
    }

    internal static bool RequiresWingetCliForInstallOptions(PackageInstallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // A COM aceita modo interativo, escopo e local; a API própria valida o hash
        // obrigatoriamente. Essas opções, por outro lado, só são executáveis pelo CLI.
        return !string.IsNullOrWhiteSpace(options.Version)
            || !string.IsNullOrWhiteSpace(options.Architecture)
            || !string.IsNullOrWhiteSpace(options.CustomArguments)
            || options.IgnoreSecurityHash;
    }

    internal static bool RequiresWingetCliForApiInstallOptions(PackageInstallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !string.IsNullOrWhiteSpace(options.InstallLocation)
            || options.Interactive
            || !string.IsNullOrWhiteSpace(options.CustomArguments)
            || options.IgnoreSecurityHash;
    }

    /// <summary>
    /// Atualiza um pacote já instalado. Mesma cadeia de fallback e mesma robustez de
    /// <see cref="InstallAsync"/> (COM com retry/watchdog/heartbeat -&gt; API própria -&gt; CLI),
    /// trocando só a chamada COM final: <see cref="RunComPackageOperationAsync"/> despacha para
    /// PackageManager.UpgradePackageAsync em vez de InstallPackageAsync quando
    /// <see cref="ComOperationKind.Upgrade"/>. A API própria entra como nível 2 chamando o mesmo
    /// <see cref="WinProvisionApiService.TryInstallAsync"/> do install — o catálogo próprio só
    /// conhece a versão mais recente de cada pacote, então "instalar" e "atualizar pra última
    /// versão" são a mesma operação do ponto de vista dela (baixa e roda o instalador mais novo
    /// silenciosamente; pra Inno/NSIS/MSI isso atualiza em cima da instalação existente, sem
    /// desinstalar/reinstalar do zero).
    /// </summary>
    public async Task<WingetExecutionResult> UpdateAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string source = "winget",
        Action<InstallProgressUpdate>? onProgress = null,
        UpgradablePackage? offeredPackage = null,
        PackageInstallOptions? updateOptions = null)
    {
        _offeredUpgrades.TryGetValue(UpgradeKey(source, packageId), out var cachedOffer);
        var offered = offeredPackage ?? cachedOffer;
        PackageUpdateMethod method = _preferredUpdateMethod();
        string? selectedTarget = updateOptions?.Version;
        if (string.IsNullOrWhiteSpace(selectedTarget) && offered is not null)
            selectedTarget = method == PackageUpdateMethod.WinProvisionApi
                ? offered.WinProvisionApiAvailableVersion
                : null;
        if (string.IsNullOrWhiteSpace(selectedTarget))
            selectedTarget = offered?.AvailableVersion;
        if (offered is not null && !string.IsNullOrWhiteSpace(selectedTarget))
        {
            var freshInventory = await GetUpdateInventoryAsync(cancellationToken).ConfigureAwait(false);
            var current = ResolveCurrentUpdateInstallation(offered, freshInventory);
            if (current is not null && IsUpdateSatisfied(current.CurrentVersion, selectedTarget))
                return AlreadyUpdated(current.CurrentVersion, selectedTarget, onLogReceived);
        }
        if (offered is not null)
            _offeredUpgrades[UpgradeKey(source, packageId)] = offered;
        UpgradablePackage? verificationOffer = offered;
        if (verificationOffer is not null && !string.IsNullOrWhiteSpace(selectedTarget)
            && !verificationOffer.AvailableVersion.Equals(selectedTarget, StringComparison.Ordinal))
            verificationOffer = new UpgradablePackage
            {
                Id = verificationOffer.Id,
                Name = verificationOffer.Name,
                Source = verificationOffer.Source,
                CurrentVersion = verificationOffer.CurrentVersion,
                AvailableVersion = selectedTarget,
                Publisher = verificationOffer.Publisher,
                Description = verificationOffer.Description,
                AvailableVersions = verificationOffer.AvailableVersions,
                WinProvisionApiAvailableVersion = verificationOffer.WinProvisionApiAvailableVersion,
                IsWinProvisionApiUpdate = verificationOffer.IsWinProvisionApiUpdate,
                InstalledRegistryId = verificationOffer.InstalledRegistryId,
                InstalledPublisher = verificationOffer.InstalledPublisher,
                InstalledScope = verificationOffer.InstalledScope,
                InstalledArchitecture = verificationOffer.InstalledArchitecture,
                InstalledInstallerType = verificationOffer.InstalledInstallerType
            };
        bool requiresCli = RequiresCliForUpdateOptions(updateOptions);
        if (method == PackageUpdateMethod.Automatic && requiresCli)
            method = PackageUpdateMethod.WingetCli;
        if (method == PackageUpdateMethod.ComApi && requiresCli)
            return new WingetExecutionResult
            {
                Success = false, ExitCode = -1,
                Output = "A API COM não aceita a versão específica, argumentos personalizados ou a opção de ignorar hash solicitados para esta atualização.",
                FailureReason = WingetFailureReason.NoApplicableUpgrade
            };
        if (method == PackageUpdateMethod.WinProvisionApi && RequiresCliForApiUpdateOptions(updateOptions))
            return new WingetExecutionResult
            {
                Success = false, ExitCode = -1,
                Output = "A WinProvision API exige hash verificado e não oferece modo interativo, pasta ou argumentos personalizados para atualização.",
                FailureReason = WingetFailureReason.NoApplicableUpgrade
            };

        var result = method switch
        {
            PackageUpdateMethod.ComApi => await RunInstallOrUpgradeAsync(
                ComOperationKind.Upgrade, packageId, onLogReceived, cancellationToken, null, onProgress,
                source, forceComOnly: true, options: updateOptions).ConfigureAwait(false),
            PackageUpdateMethod.WinProvisionApi => await TryApiUpdateThenCliAsync(
                packageId, onLogReceived, onProgress, cancellationToken, source,
                "método WinProvision API selecionado", options: updateOptions,
                fallbackToCli: false).ConfigureAwait(false),
            PackageUpdateMethod.WingetCli => await RunCliUpdateFallbackAsync(
                packageId, onLogReceived, onProgress, cancellationToken, source,
                ResolveExecutionScope(null), updateOptions).ConfigureAwait(false),
            _ => await RunInstallOrUpgradeAsync(
                ComOperationKind.Upgrade, packageId, onLogReceived, cancellationToken, null, onProgress,
                source, options: updateOptions).ConfigureAwait(false)
        };
        if (result.Success && !string.IsNullOrWhiteSpace(result.ApiUpdateTargetVersion)
            && verificationOffer is not null)
        {
            verificationOffer = new UpgradablePackage
            {
                Id = verificationOffer.Id,
                Name = verificationOffer.Name,
                Source = verificationOffer.Source,
                CurrentVersion = verificationOffer.CurrentVersion,
                AvailableVersion = result.ApiUpdateTargetVersion,
                Publisher = verificationOffer.Publisher,
                Description = verificationOffer.Description,
                AvailableVersions = verificationOffer.AvailableVersions,
                WinProvisionApiAvailableVersion = verificationOffer.WinProvisionApiAvailableVersion,
                IsWinProvisionApiUpdate = verificationOffer.IsWinProvisionApiUpdate,
                InstalledRegistryId = verificationOffer.InstalledRegistryId,
                InstalledPublisher = verificationOffer.InstalledPublisher,
                InstalledScope = verificationOffer.InstalledScope,
                InstalledArchitecture = verificationOffer.InstalledArchitecture,
                InstalledInstallerType = verificationOffer.InstalledInstallerType
            };
        }
        if (!result.Success || result.UpdateAlreadySatisfied || result.UpdateVersionVerified) return result;
        return verificationOffer is null
            ? MarkUpdateUnverified(result, "A operação terminou, mas não havia uma versão-alvo conhecida para conferir.")
            : await VerifySuccessfulUpgradeAsync(verificationOffer, result, cancellationToken).ConfigureAwait(false);
    }

    internal static bool RequiresCliForUpdateOptions(PackageInstallOptions? options) => options is not null
        && (!string.IsNullOrWhiteSpace(options.Version)
            || !string.IsNullOrWhiteSpace(options.CustomArguments)
            || options.IgnoreSecurityHash);

    internal static UpgradablePackage? ResolveCurrentUpdateInstallation(UpgradablePackage offered,
        IReadOnlyList<UpgradablePackage> inventory)
    {
        bool SameInstallation(UpgradablePackage item) =>
            (offered.InstalledScope is null || offered.InstalledScope.Equals(item.InstalledScope, StringComparison.OrdinalIgnoreCase))
            && (offered.InstalledArchitecture is null || offered.InstalledArchitecture.Equals(item.InstalledArchitecture, StringComparison.OrdinalIgnoreCase));
        string identity = offered.InstalledRegistryId ?? offered.Id;
        var exact = inventory.Where(item => SameInstallation(item)
            && ((item.InstalledRegistryId ?? item.Id).Equals(identity, StringComparison.OrdinalIgnoreCase)
                || item.Id.Equals(offered.Id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count > 0) return WinProvisionApiService.SelectUpdateInstallation(exact);

        string publisher = offered.InstalledPublisher ?? offered.Publisher;
        string name = WinProvisionApiService.NormalizeInstalledName(offered.Name, offered.CurrentVersion);
        var byName = inventory.Where(item => SameInstallation(item)
            && WinProvisionApiService.NormalizeInstalledName(item.Name, item.CurrentVersion) == name
            && (string.IsNullOrWhiteSpace(publisher) || string.IsNullOrWhiteSpace(item.Publisher)
                || WinProvisionApiService.NormalizeForMatching(item.Publisher) == WinProvisionApiService.NormalizeForMatching(publisher))).ToList();
        return WinProvisionApiService.SelectUpdateInstallation(byName);
    }

    internal static bool IsUpdateSatisfied(string installedVersion, string targetVersion) =>
        WinProvisionApiService.NumericVersionsEqual(installedVersion, targetVersion)
        || WinProvisionApiService.IsStrictlyNewerNumericVersion(targetVersion, installedVersion);

    private static WingetExecutionResult AlreadyUpdated(string installedVersion, string targetVersion, Action<string>? log)
    {
        string message = $"O aplicativo já está atualizado (instalado: {installedVersion}; alvo: {targetVersion}). Nenhum instalador foi executado.";
        log?.Invoke(message);
        return new WingetExecutionResult { Success = true, UpdateAlreadySatisfied = true, Output = message };
    }

    private sealed class AlreadyUpdatedException(string installedVersion, string targetVersion) : InvalidOperationException
    {
        public string InstalledVersion { get; } = installedVersion;
        public string TargetVersion { get; } = targetVersion;
    }

    internal static bool RequiresCliForApiUpdateOptions(PackageInstallOptions? options) => options is not null
        && (options.Interactive
            || !string.IsNullOrWhiteSpace(options.InstallLocation)
            || !string.IsNullOrWhiteSpace(options.CustomArguments)
            || options.IgnoreSecurityHash);

    /// <summary>Worker elevado: executa somente a COM; o processo chamador confere a versão ofertada.</summary>
    public Task<WingetExecutionResult> UpdateComOnlyAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget") =>
        RunInstallOrUpgradeAsync(ComOperationKind.Upgrade, packageId, onLogReceived,
            cancellationToken, null, onProgress, source, forceComOnly: true);

    public async Task<WingetExecutionResult> VerifySuccessfulUpgradeAsync(
        UpgradablePackage offered, WingetExecutionResult result, CancellationToken cancellationToken)
    {
        if (!result.Success || result.UpdateAlreadySatisfied || result.UpdateVersionVerified) return result;
        if (result.RebootRequired)
            return MarkUpdateUnverified(result, "A atualização aguarda reinicialização; a versão instalada ainda não foi confirmada.");
        // Conte sucessos reportados pelo motor mesmo quando a conferência abaixo revela
        // que a versão não mudou. Sem isso, um motor que retorna sucesso sem atualizar
        // poderia ser acionado indefinidamente a cada tentativa do usuário.
        RecordSuccessfulUpgrade(offered, result);
        if (string.IsNullOrWhiteSpace(offered.AvailableVersion))
            return MarkUpdateUnverified(result, "A operação terminou, mas não havia uma versão-alvo conhecida para conferir.");

        try
        {
            if (offered.IsWinProvisionApiUpdate || !string.IsNullOrWhiteSpace(result.ApiUpdateTargetVersion)
                || _preferredUpdateMethod() == PackageUpdateMethod.WinProvisionApi)
            {
                // A bootstrapper may return before its final ARP registration is visible.
                // Observe a bounded number of fresh snapshots; never rerun the installer.
                for (int attempt = 0; ; attempt++)
                {
                    var inventory = await GetUpdateInventoryAsync(cancellationToken).ConfigureAwait(false);
                    if (HasConfirmedApiVersion(offered, result, inventory))
                        return ConfirmApiUpgradeResult(offered, result, inventory);
                    if (attempt == 2) return ConfirmApiUpgradeResult(offered, result, inventory);
                    await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 250 : 750), cancellationToken).ConfigureAwait(false);
                }
            }

            // A listagem é uma consulta nova pelo processo CLI. Não usamos o objeto COM
            // da operação nem o inventário já carregado, que podem refletir cache anterior.
            using var verifyTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            verifyTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            var installed = await _wingetExecutor.ListInstalledPackagesAsync(
                verifyTimeout.Token, NormalizeUpdateSource(offered.Source)).ConfigureAwait(false);
            result = ConfirmUpgradeResult(offered, result, installed);
            if (result.Success)
                result = ConfirmKnownRuntimeVersion(offered, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return MarkUpdateUnverified(result,
                $"Não foi possível confirmar a versão instalada ({exception.GetType().Name}).");
        }

        return result;
    }

    internal static WingetExecutionResult ConfirmApiUpgradeResult(UpgradablePackage offered,
        WingetExecutionResult result, IReadOnlyList<UpgradablePackage> inventory)
    {
        if (HasConfirmedApiVersion(offered, result, inventory))
        {
            result.UpdateVersionVerified = true;
            return result;
        }
        string actual = string.Join(", ", inventory.Where(item =>
            WinProvisionApiService.NormalizeInstalledName(item.Name, item.CurrentVersion)
                == WinProvisionApiService.NormalizeInstalledName(offered.Name, offered.CurrentVersion))
            .Select(item => $"{item.CurrentVersion} ({item.InstalledScope ?? "escopo desconhecido"}, {item.InstalledArchitecture ?? "arquitetura desconhecida"})"));
        return MarkUpdateUnverified(result,
            $"O inventário do Windows não confirmou uma instalação única na versão {offered.AvailableVersion} ou superior. Encontrada: {(actual.Length == 0 ? "não localizada" : actual)}.");
    }

    private static bool HasConfirmedApiVersion(UpgradablePackage offered, WingetExecutionResult result,
        IReadOnlyList<UpgradablePackage> inventory)
    {
        string identity = offered.InstalledRegistryId ?? offered.Id;
        var matches = inventory.Where(item =>
            (item.InstalledRegistryId ?? item.Id).Equals(identity, StringComparison.OrdinalIgnoreCase)
            && (offered.InstalledScope is null || item.InstalledScope == offered.InstalledScope)).ToArray();
        if (matches.Length == 0 && !string.IsNullOrWhiteSpace(result.ApiUpdateProductCode))
        {
            matches = inventory.Where(item =>
                item.Id.Equals(result.ApiUpdateProductCode, StringComparison.OrdinalIgnoreCase)
                && (result.ApiUpdateProductCodeFromInstaller || offered.InstalledScope is null || item.InstalledScope == offered.InstalledScope)
                && (offered.InstalledArchitecture is null || item.InstalledArchitecture == offered.InstalledArchitecture)).ToArray();
        }
        // MSI major upgrades can replace the ProductCode/uninstall key.
        if (matches.Length == 0)
            matches = inventory.Where(item =>
                WinProvisionApiService.NormalizeInstalledName(item.Name, item.CurrentVersion) == WinProvisionApiService.NormalizeInstalledName(offered.Name, offered.CurrentVersion)
                && (offered.InstalledScope is null || item.InstalledScope == offered.InstalledScope)
                && (offered.InstalledArchitecture is null || item.InstalledArchitecture == offered.InstalledArchitecture)
                && (string.IsNullOrWhiteSpace(offered.InstalledPublisher ?? offered.Publisher) || string.IsNullOrWhiteSpace(item.Publisher)
                    || WinProvisionApiService.NormalizeForMatching(item.Publisher) == WinProvisionApiService.NormalizeForMatching(offered.InstalledPublisher ?? offered.Publisher))).ToArray();
        return matches.Length == 1 && IsUpdateSatisfied(matches[0].CurrentVersion, offered.AvailableVersion);
    }

    internal static WingetExecutionResult ConfirmUpgradeResult(UpgradablePackage offered,
        WingetExecutionResult result, IReadOnlyList<UpgradablePackage> installed)
    {
        var matches = installed.Where(item =>
            item.Id.Equals(offered.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length == 1 && VersionsMatch(matches[0].CurrentVersion, offered.AvailableVersion))
            return result;

        string actual = matches.Length switch
        {
            0 => "não encontrado",
            1 => matches[0].CurrentVersion,
            _ => "identidade ambígua no inventário"
        };
        return MarkUpdateUnverified(result,
            $"A versão instalada não corresponde ao alvo {offered.AvailableVersion} (encontrada: {actual}).");
    }

    private static WingetExecutionResult ConfirmKnownRuntimeVersion(
        UpgradablePackage offered, WingetExecutionResult result)
    {
        if (!offered.Id.Equals("Microsoft.PowerShell", StringComparison.OrdinalIgnoreCase))
            return result;

        string executable = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "pwsh.exe");
        if (!File.Exists(executable)) return result;
        try
        {
            var file = FileVersionInfo.GetVersionInfo(executable);
            string runtimeVersion = $"{file.FileMajorPart}.{file.FileMinorPart}.{file.FileBuildPart}";
            if (RuntimeVersionMatches(runtimeVersion, offered.AvailableVersion)) return result;
            return MarkUpdateUnverified(result,
                $"O executável principal ainda informa {runtimeVersion}, embora o inventário informe {offered.AvailableVersion}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return MarkUpdateUnverified(result,
                $"Não foi possível conferir o executável principal ({exception.GetType().Name}).");
        }
    }

    internal static bool RuntimeVersionMatches(string? actual, string? target)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(target)) return false;
        if (!TryNormalizeVersion(actual, out var normalizedActual)
            || !TryNormalizeVersion(target, out var normalizedTarget)) return false;
        return normalizedActual.Major == normalizedTarget.Major
            && normalizedActual.Minor == normalizedTarget.Minor
            && normalizedActual.Build == normalizedTarget.Build;
    }

    private static WingetExecutionResult MarkUpdateUnverified(WingetExecutionResult result, string reason)
    {
        result.Success = false;
        result.InstallerStarted = true;
        result.FailureReason = WingetFailureReason.InstallError;
        result.Output = $"A atualização foi executada, mas não foi confirmada. {reason}";
        return result;
    }

    internal static bool VersionsMatch(string? actual, string? target)
    {
        if (string.IsNullOrWhiteSpace(actual) || string.IsNullOrWhiteSpace(target)) return false;
        actual = actual.Trim().TrimStart('v', 'V');
        target = target.Trim().TrimStart('v', 'V');
        if (IsVersionPlaceholder(actual) || IsVersionPlaceholder(target)) return false;
        if (TryNormalizeVersion(actual, out var normalizedActual)
            && TryNormalizeVersion(target, out var normalizedTarget))
            return normalizedActual.Equals(normalizedTarget);
        return actual.Equals(target, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVersionPlaceholder(string value) => value.Trim() is "-" or "?"
        || value.Contains("unknown", StringComparison.OrdinalIgnoreCase)
        || value.Contains("desconhec", StringComparison.OrdinalIgnoreCase)
        || value.Equals("n/a", StringComparison.OrdinalIgnoreCase)
        || value.Equals("na", StringComparison.OrdinalIgnoreCase)
        || value.Contains("not available", StringComparison.OrdinalIgnoreCase)
        || value.Contains("indispon", StringComparison.OrdinalIgnoreCase);

    private static bool TryNormalizeVersion(string value, out Version version)
    {
        version = null!;
        string[] parts = value.Split('.');
        if (parts.Length is < 2 or > 4 || parts.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
            return false;
        string normalized = string.Join('.', parts.Concat(Enumerable.Repeat("0", 4 - parts.Length)));
        if (!Version.TryParse(normalized, out var parsed)) return false;
        version = parsed;
        return true;
    }

    internal void RecordSuccessfulUpgrade(UpgradablePackage package, WingetExecutionResult result)
    {
        if (result.Success) _stuckUpgrades.RecordSuccess(package);
    }

    private List<UpgradablePackage> FilterUpgrades(IEnumerable<UpgradablePackage> packages)
    {
        var unique = DeduplicateUpdates(packages);
        var visible = _stuckUpgrades.FilterOffered(unique);
        foreach (var package in visible)
            _offeredUpgrades[UpgradeKey(package.Source, package.Id)] = package;
        return visible;
    }

    private static string UpgradeKey(string source, string id) => NormalizeUpdateSource(source) + "\u001f" + id;

    /// <summary>Consulta ofertas COM dos instalados e complementa com CLI e catálogo próprio.</summary>
    public async Task<List<UpgradablePackage>> GetUpgradablePackagesAsync(
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default)
    {
        var timer = Stopwatch.StartNew();
        if (_preferredUpdateMethod() == PackageUpdateMethod.WinProvisionApi)
        {
            onLogReceived?.Invoke("Comparando aplicativos instalados com o catálogo WinProvision...");
            var inventory = await GetUpdateInventoryAsync(cancellationToken).ConfigureAwait(false);
            var ownUpdates = await _apiService.GetUpdatesAsync(inventory, cancellationToken).ConfigureAwait(false);
            PackageDiagnostics.Write($"UPDATE DISCOVERY method=WinProvisionApi installed={inventory.Count} offers={ownUpdates.Count} elapsed={timer.Elapsed}");
            return FilterUpgrades(ownUpdates);
        }
        await EnsureWingetProvisionedAsync(onLogReceived, cancellationToken).ConfigureAwait(false);
        List<UpgradablePackage>? comPackages = null;
        List<UpgradablePackage>? cliPackages = null;
        var failures = new List<Exception>();
        if (!WinGetFactoryHelper.IsComDisabled)
        {
            try
            {
                onLogReceived?.Invoke("Consultando atualizações dos aplicativos instalados pela API COM...");
                comPackages = await RunUpdateQueryAsync(GetComUpgradablePackagesAsync,
                    UpdateQueryTimeout, cancellationToken).ConfigureAwait(false);
                PackageDiagnostics.Write($"UPDATE DISCOVERY COM success count={comPackages.Count} elapsed={timer.Elapsed}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                failures.Add(exception);
                // Do not accumulate native RPCs if a getter ignores cancellation.
                if (exception is TimeoutException || WinGetFactoryHelper.IsActivationFailure(exception))
                    WinGetFactoryHelper.DisableComForSession(exception);
                PackageDiagnostics.Error(exception, "WinGet.DiscoverUpdatesCOM");
            }
        }
        if (WinGetFactoryHelper.CliFallbackAllowed)
        {
            try
            {
                onLogReceived?.Invoke("Conferindo atualizações pelo WinGet...");
                cliPackages = await RunUpdateQueryAsync(
                    token => _wingetExecutor.GetUpgradablePackagesAsync(null, token),
                    UpdateQueryTimeout, cancellationToken).ConfigureAwait(false);
                PackageDiagnostics.Write($"UPDATE DISCOVERY CLI success count={cliPackages.Count} elapsed={timer.Elapsed}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                failures.Add(exception);
                PackageDiagnostics.Error(exception, "WinGet.DiscoverUpdatesCLI");
            }
        }
        if (comPackages is null && cliPackages is null)
        {
            try
            {
                var inventory = await GetUpdateInventoryAsync(cancellationToken).ConfigureAwait(false);
                return FilterUpgrades(await _apiService.GetUpdatesAsync(inventory, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { failures.Add(exception); }
            throw new InvalidOperationException("Nenhuma fonte concluiu a consulta de atualizações. Tente recarregar a lista.",
                failures.Count == 0 ? null : new AggregateException(failures));
        }
        var packages = await MergeWinProvisionApiUpdatesAsync(
            MergeUpdates(comPackages ?? [], cliPackages ?? []), onLogReceived, cancellationToken).ConfigureAwait(false);
        PackageDiagnostics.Write($"UPDATE DISCOVERY completed count={packages.Count} elapsed={timer.Elapsed}");
        return packages;
    }

    internal static async Task<List<UpgradablePackage>> RunUpdateQueryAsync(
        Func<CancellationToken, Task<List<UpgradablePackage>>> query,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        CancellationToken queryToken = timeout.Token;
        // Native activation and property getters are synchronous RPCs. Keep them off the
        // dispatcher and bound the wait even if native code ignores cancellation.
        Task<List<UpgradablePackage>> worker = Task.Run(() => query(queryToken), CancellationToken.None);
        try { return await worker.WaitAsync(queryToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _ = ObserveUpdateQueryAsync(worker);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"A fonte de atualizações não respondeu em {deadline.TotalSeconds:0} segundos.");
        }
    }

    private static async Task ObserveUpdateQueryAsync(Task query)
    {
        try { await query.ConfigureAwait(false); }
        catch (OperationCanceledException) { PackageDiagnostics.Write("UPDATE DISCOVERY worker cancelado."); }
        catch (Exception exception) { PackageDiagnostics.Error(exception, "WinGet.DiscoverUpdatesLateCompletion"); }
    }

    private async Task<List<UpgradablePackage>> GetComUpgradablePackagesAsync(CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        var manager = WinGetFactoryHelper.CreateResilientPackageManager();
        // LocalCatalogs maps to Repository::CompositeSearchBehavior::Installed.
        // It correlates installed packages with the attached sources, as outras interfaces does.
        // Searching all remote packages makes listing updates scan the entire catalog.
        var composite = WinGetFactoryHelper.CreateCompositePackageCatalogOptions();
        composite.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;
        foreach (var source in new[] { PredefinedPackageCatalog.OpenWindowsCatalog, PredefinedPackageCatalog.MicrosoftStore })
        {
            var reference = manager.GetPredefinedPackageCatalog(source);
            reference.AcceptSourceAgreements = true;
            composite.Catalogs.Add(reference);
        }
        var catalogReference = manager.CreateCompositePackageCatalog(composite);

        var connectResult = await catalogReference.ConnectAsync()
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (connectResult.Status != ConnectResultStatus.Ok || connectResult.PackageCatalog is null)
            throw new InvalidOperationException(
                $"Falha ao conectar ao catálogo de pacotes instalados: {connectResult.Status}.");

        PackageDiagnostics.Write($"UPDATE DISCOVERY COM connected elapsed={timer.Elapsed}");
        var options = WinGetFactoryHelper.CreateFindPackagesOptions();
        var selector = WinGetFactoryHelper.CreatePackageMatchFilter();
        selector.Field = PackageMatchField.Id;
        selector.Option = PackageFieldMatchOption.StartsWithCaseInsensitive;
        selector.Value = string.Empty;
        options.Filters.Add(selector);

        var findResult = await connectResult.PackageCatalog.FindPackagesAsync(options)
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (findResult.Status != FindPackagesResultStatus.Ok)
            throw new InvalidOperationException($"Consulta COM de atualizações falhou: {findResult.Status}.");
        if (findResult.WasLimitExceeded)
            throw new InvalidOperationException("O catálogo COM limitou a lista de atualizações; a consulta será repetida pela CLI.");
        PackageDiagnostics.Write($"UPDATE DISCOVERY COM installed-matches={findResult.Matches.Count} elapsed={timer.Elapsed}");
        var packages = new List<UpgradablePackage>();

        foreach (var match in IndexedCollectionReader.Read(findResult.Matches, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var package = match.CatalogPackage;
                if (!package.IsUpdateAvailable)
                    continue;

                var installed = package.InstalledVersion;
                var available = package.DefaultInstallVersion;
                if (installed is null || available is null || string.IsNullOrWhiteSpace(package.Id))
                {
                    PackageDiagnostics.Write(
                        $"UPDATE DISCOVERY COM skip id=\"{package.Id}\" installed={installed is not null} available={available is not null}");
                    continue;
                }

                string source = available.PackageCatalog?.Info?.Name
                    ?? installed.PackageCatalog?.Info?.Name
                    ?? "winget";
                if (IsUsableLocalIdentifier(package.Id, installed.Id))
                    _localIdentifiers[UpgradeKey(source, package.Id)] = installed.Id;
                packages.Add(new UpgradablePackage
                {
                    Id = package.Id,
                    Name = package.Name,
                    CurrentVersion = installed.Version,
                    AvailableVersion = available.Version,
                    Source = NormalizeUpdateSource(source)
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Un pacote malformado no índice local não deve esconder as atualizações
                // dos demais; falha global ainda cai para o CLI no bloco externo.
                PackageDiagnostics.Write(
                    $"UPDATE DISCOVERY COM package-skip {ex.GetType().Name}: {ex.Message}");
            }
        }

        WinGetFactoryHelper.ReportComSuccess();
        return packages;
    }

    private async Task<List<UpgradablePackage>> MergeWinProvisionApiUpdatesAsync(
        List<UpgradablePackage> nativeUpdates,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken)
    {
        try
        {
            onLogReceived?.Invoke("Comparando instalações WinProvision com o catálogo verificado...");
            var installed = await GetUpdateInventoryAsync(cancellationToken).ConfigureAwait(false);
            var ownApiUpdates = await _apiService.GetUpdatesAsync(installed, cancellationToken)
                .ConfigureAwait(false);
            if (ownApiUpdates.Count == 0)
                return FilterUpgrades(nativeUpdates);

            nativeUpdates = DeduplicateUpdates(nativeUpdates);
            var combined = nativeUpdates.ToDictionary(
                package => UpgradeKey(package.Source, package.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var apiOffer in ownApiUpdates)
            {
                string key = UpgradeKey(apiOffer.Source, apiOffer.Id);
                if (combined.TryGetValue(key, out var nativeOffer))
                {
                    nativeOffer.WinProvisionApiAvailableVersion = apiOffer.AvailableVersion;
                    nativeOffer.InstalledScope ??= apiOffer.InstalledScope;
                    nativeOffer.InstalledArchitecture ??= apiOffer.InstalledArchitecture;
                    nativeOffer.InstalledInstallerType ??= apiOffer.InstalledInstallerType;
                }
                else
                {
                    apiOffer.WinProvisionApiAvailableVersion = apiOffer.AvailableVersion;
                    combined.Add(key, apiOffer);
                }
            }

            PackageDiagnostics.Write(
                $"UPDATE DISCOVERY API-PROPRIA managed={ownApiUpdates.Count} native={nativeUpdates.Count} total={combined.Count}");
            return FilterUpgrades(combined.Values);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            PackageDiagnostics.Write(
                $"UPDATE DISCOVERY API-PROPRIA indisponível; mantendo WinGet {ex.GetType().Name}: {ex.Message}");
            return FilterUpgrades(nativeUpdates);
        }
    }

    private Task<List<UpgradablePackage>> GetUpdateInventoryAsync(CancellationToken token) => _apiInventory(token);

    private static Task<List<UpgradablePackage>> ReadRegistryInventoryAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var inventory = InstalledAppsService.GetRegistryInventory(token);
            token.ThrowIfCancellationRequested();
            return inventory;
        }, token);

    internal static List<UpgradablePackage> MergeUpdateInventory(
        IReadOnlyCollection<UpgradablePackage> identified, IReadOnlyCollection<UpgradablePackage> registry)
    {
        var result = identified.ToList();
        foreach (var app in registry)
        {
            var matches = identified.Where(item => NormalizeUpdateSource(item.Source) == "winget"
                && (item.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)
                    || (WinProvisionApiService.NormalizeForMatching(item.Name) == WinProvisionApiService.NormalizeForMatching(app.Name)
                        && item.CurrentVersion == app.CurrentVersion))).ToArray();
            int localMatches = registry.Count(item =>
                WinProvisionApiService.NormalizeForMatching(item.Name) == WinProvisionApiService.NormalizeForMatching(app.Name)
                && item.CurrentVersion == app.CurrentVersion);
            if (matches.Length != 1 || localMatches != 1)
            {
                result.Add(app);
                continue;
            }
            var match = matches[0];
            match.InstalledScope ??= app.InstalledScope;
            match.InstalledArchitecture ??= app.InstalledArchitecture;
            match.InstalledInstallerType ??= app.InstalledInstallerType;
        }
        return result;
    }

    /// <summary>Mesmo preflight usado pela execução API; não baixa nem inicia instaladores.</summary>
    public async Task<WinProvisionUpdatePlan> PrepareApiUpdateAsync(string packageId, string? requiredVersion = null,
        CancellationToken token = default)
    {
        if (_offeredUpgrades.TryGetValue(UpgradeKey("winget", packageId), out var offered))
        {
            var fresh = await GetUpdateInventoryAsync(token).ConfigureAwait(false);
            var current = ResolveCurrentUpdateInstallation(offered, fresh)
                ?? throw new InvalidOperationException("O inventário atual não confirmou uma única instalação para atualizar. Recarregue a lista.");
            string target = requiredVersion ?? offered.WinProvisionApiAvailableVersion;
            if (string.IsNullOrWhiteSpace(target)) target = offered.AvailableVersion;
            if (IsUpdateSatisfied(current.CurrentVersion, target)) throw new AlreadyUpdatedException(current.CurrentVersion, target);
            var refreshed = new UpgradablePackage
            {
                Id = offered.Id, Name = offered.Name, Source = offered.Source,
                CurrentVersion = current.CurrentVersion, AvailableVersion = offered.AvailableVersion,
                Publisher = offered.Publisher, Description = offered.Description, IconUrl = offered.IconUrl,
                WinProvisionApiAvailableVersion = offered.WinProvisionApiAvailableVersion, IsWinProvisionApiUpdate = offered.IsWinProvisionApiUpdate,
                InstalledRegistryId = current.InstalledRegistryId ?? current.Id, InstalledPublisher = current.Publisher,
                InstalledScope = current.InstalledScope, InstalledArchitecture = current.InstalledArchitecture,
                InstalledInstallerType = current.InstalledInstallerType
            };
            return await _apiService.PrepareUpdateAsync(refreshed, requiredVersion, token).ConfigureAwait(false);
        }

        var inventory = await GetUpdateInventoryAsync(token).ConfigureAwait(false);
        var matches = inventory.Where(item => item.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)
            && item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            var offers = await _apiService.GetUpdatesAsync(inventory, token, refreshRemote: false).ConfigureAwait(false);
            var offer = offers.SingleOrDefault(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            if (offer is not null)
                return await _apiService.PrepareUpdateAsync(offer, requiredVersion, token).ConfigureAwait(false);
            throw new InvalidOperationException("Não foi encontrada uma única instalação com o ID solicitado. Recarregue o inventário antes de atualizar.");
        }
        return await _apiService.PrepareUpdateAsync(matches[0], requiredVersion, token).ConfigureAwait(false);
    }

    private static List<UpgradablePackage> DeduplicateUpdates(IEnumerable<UpgradablePackage> packages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return packages
            .Where(package => seen.Add($"{NormalizeUpdateSource(package.Source)}\u001f{package.Id}"))
            .ToList();
    }

    internal static List<UpgradablePackage> MergeUpdates(
        IEnumerable<UpgradablePackage> comPackages,
        IEnumerable<UpgradablePackage> cliPackages)
    {
        var combined = DeduplicateUpdates(comPackages).ToDictionary(
            package => UpgradeKey(package.Source, package.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var cli in cliPackages)
        {
            string key = UpgradeKey(cli.Source, cli.Id);
            if (!combined.TryGetValue(key, out var existing))
            {
                combined.Add(key, cli);
                continue;
            }

            // COM may retain an older offer than CLI. Compare only versions of the
            // same installed release; conflicting inventories cannot settle a target.
            if (!VersionsMatch(existing.CurrentVersion, cli.CurrentVersion)
                || !WinProvisionApiService.IsStrictlyNewerNumericVersion(existing.AvailableVersion, cli.AvailableVersion))
                continue;

            if (string.IsNullOrWhiteSpace(cli.Publisher)) cli.Publisher = existing.Publisher;
            if (string.IsNullOrWhiteSpace(cli.Description)) cli.Description = existing.Description;
            if (string.IsNullOrWhiteSpace(cli.IconUrl)) cli.IconUrl = existing.IconUrl;
            cli.InstalledScope ??= existing.InstalledScope;
            cli.InstalledArchitecture ??= existing.InstalledArchitecture;
            cli.InstalledInstallerType ??= existing.InstalledInstallerType;
            cli.InstalledRegistryId ??= existing.InstalledRegistryId;
            combined[key] = cli;
        }
        return combined.Values.ToList();
    }

    private static string NormalizeUpdateSource(string source)
    {
        if (source.Contains("store", StringComparison.OrdinalIgnoreCase)
            || source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
            return "msstore";
        return string.IsNullOrWhiteSpace(source) ? "winget" : source;
    }

    /// <summary>
    /// Corpo comum de <see cref="InstallAsync"/> e <see cref="UpdateAsync"/> — primeiro
    /// provisiona o gerenciador de pacotes (necessário para registrar o servidor COM), depois tenta COM,
    /// API própria e CLI como último recurso.
    /// </summary>
    private async Task<WingetExecutionResult> RunInstallOrUpgradeAsync(
        ComOperationKind opKind,
        string packageId,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken,
        string? installLocation,
        Action<InstallProgressUpdate>? onProgress,
        string source,
        bool forceComOnly = false,
        PackageInstallOptions? options = null)
    {
        options ??= new PackageInstallOptions(InstallLocation: installLocation);
        installLocation = options.InstallLocation ?? installLocation;
        await EnsureWingetProvisionedAsync(onLogReceived, cancellationToken).ConfigureAwait(false);
        bool cliOnly = !forceComOnly && WinGetFactoryHelper.Mode == WinGetMode.CliOnly;
        if (forceComOnly && WinGetFactoryHelper.Mode == WinGetMode.CliOnly)
            return new WingetExecutionResult { Success = false, ExitCode = -1,
                Output = "A API COM está desativada pelo modo CLI explícito.",
                FailureReason = WingetFailureReason.InternalError };

        string logTag = opKind == ComOperationKind.Install ? "INSTALL" : "UPDATE";
        string actionLower = opKind == ComOperationKind.Install ? "instalação" : "atualização";
        bool isSystem = WinProvisionApiService.IsRunningAsSystem();

        PackageDiagnostics.Write(
            $"{logTag} ENTER packageId=\"{packageId}\" source={source} mode={WinGetFactoryHelper.Mode} " +
            $"identity={(isSystem ? "SYSTEM" : "User")} comDisabled={WinGetFactoryHelper.IsComDisabled} " +
            $"thread={Environment.CurrentManagedThreadId}");

        if (forceComOnly && (WinProvisionElevationState.HasFailedThisSession
            || WinGetFactoryHelper.IsComDisabled))
        {
            var reason = WinProvisionElevationState.HasFailedThisSession
                    ? "A API COM não será tentada novamente após uma falha de elevação nesta sessão."
                : $"A API COM do WinGet está indisponível: {WinGetFactoryHelper.DisabledReason}.";
            onLogReceived?.Invoke(reason);
            return new WingetExecutionResult { Success = false, ExitCode = -1, Output = reason, FailureReason = WingetFailureReason.InternalError };
        }

        if (cliOnly && opKind == ComOperationKind.Upgrade)
        {
            onLogReceived?.Invoke("Modo WinGet CLI explícito; atualizando diretamente pela CLI.");
            return await RunCliUpdateFallbackAsync(packageId, onLogReceived, onProgress,
                cancellationToken, source, ResolveExecutionScope(null), options).ConfigureAwait(false);
        }

        // SYSTEM, por si só, não desativa COM. Só pulamos uma operação COM após uma recusa
        // de elevação já registrada para evitar repetir um pedido de UAC recusado.
        if (cliOnly || WinProvisionElevationState.HasFailedThisSession)
        {
            var bypassReason = cliOnly ? "modo-cli-explicito" : "reelevação-falhou-antes";
            PackageDiagnostics.Write($"{logTag} BYPASS COM: motivo={bypassReason}");
            onLogReceived?.Invoke(cliOnly
                ? "Modo WinGet CLI explícito; tentando a API própria antes do CLI."
                : "Pulando API COM após falha de elevação; usando a API própria da WinProvision Store.");
            return await RunApiThenCliFallbackAsync(
                opKind, packageId, onLogReceived, onProgress, cancellationToken, installLocation, source, bypassReason, options: options)
                .ConfigureAwait(false);
        }

        if (WinGetFactoryHelper.IsComDisabled && !cliOnly)
        {
            PackageDiagnostics.Write($"FALLBACK PARA CLI: motivo={WinGetFactoryHelper.DisabledReason}");
            onLogReceived?.Invoke("API COM indisponível; usando a API própria da WinProvision Store.");
            return await RunApiThenCliFallbackAsync(
                opKind, packageId, onLogReceived, onProgress, cancellationToken, installLocation, source,
                $"COM-desativada-sessão({WinGetFactoryHelper.DisabledReason})", options: options).ConfigureAwait(false);
        }

        // Só serve pra sinalizar a via atual pro OperationRunner (ver ReportProgress) e colorir a
        // barra de progresso; as mensagens de falha/pulo da COM já existiam antes disso e cobriam
        // só o caminho de erro — sem isso, um install/update que dá certo de primeira na COM nunca
        // reportava nada em onLogReceived até a linha final de sucesso, perto demais do fim pra
        // colorir a barra durante a operação inteira.
        onLogReceived?.Invoke("Comunicando com a API COM do WinGet...");

        // Envia progresso inicial para definir o método como ComApi
        onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.ComApi));

        var state = new InstallAttemptState();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                // COM (RunComPackageOperationAsync) resolve o pacote pelo Id nos catálogos
                // predefinidos (OpenWindowsCatalog e, para source=msstore, MicrosoftStore).
                return await Task.Run(
                    () => RunComPackageOperationAsync(
                        opKind, packageId, onLogReceived, onProgress, cancellationToken, installLocation, source, state, forceComOnly, options),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Falha de ativação/identidade ANTES de a operação ser entregue ao servidor COM: tenta a
                // próxima estratégia (lower-trust -> empacotado) antes de desistir da COM.
                if (!state.Started && WinGetFactoryHelper.TryAdvanceStrategy(ex))
                {
                    PackageDiagnostics.Write(
                        $"{logTag} COM repetindo com a estratégia {WinGetFactoryHelper.CurrentStrategy} " +
                        $"(falha anterior 0x{ex.HResult:X8})");
                    continue;
                }

                WinGetFactoryHelper.DisableComForSession(ex);
                PackageDiagnostics.Write(
                    $"{logTag} COM FALHOU attempt={attempt}/{MaxPreflightAttempts} started={state.Started} " +
                    $"exception={ex.GetType().FullName} hresult=0x{ex.HResult:X8} " +
                    $"message=\"{ex.Message}\" stack={ex}");

                if (forceComOnly)
                {
                    var message = $"A instalação pela API COM falhou: {ex.Message}";
                    onLogReceived?.Invoke(message);
                    return new WingetExecutionResult
                    {
                        Success = false,
                        ExitCode = ex.HResult,
                        Output = message,
                        FailureReason = WingetFailureReason.InternalError
                    };
                }

                // Depois que a operação COM é despachada, uma exceção pode deixar incerto
                // se o instalador começou. Não repetir por outro método para evitar uma
                // instalação duplicada ou parcial. Antes do despacho, mantém a recuperação.
                if (state.Started)
                {
                    onLogReceived?.Invoke(
                        state.InstallerStarted
                            ? $"A API COM falhou durante a {actionLower} após iniciar o instalador ({ex.Message}); não haverá fallback automático."
                            : $"A API COM falhou durante a {actionLower} após entregar a operação ao WinGet ({ex.Message}); como o estado do instalador é incerto, não haverá fallback automático.");
                    return new WingetExecutionResult
                    {
                        Success = false,
                        ExitCode = ex.HResult,
                        Output = $"Falha na API COM durante a {actionLower}: {ex.Message}",
                        FailureReason = WingetFailureReason.InternalError,
                        // Depois do despacho, a COM pode não informar se o instalador
                        // chegou a iniciar. Marca como incerto para impedir nova tentativa.
                        InstallerStarted = true
                    };
                }

                // Falha passageira antes de começar (servidor COM reiniciando, RPC caiu): tenta de novo
                // com uma nova ativação antes de desistir da COM.
                if (attempt < MaxPreflightAttempts &&
                    WinGetFactoryHelper.IsTransient(ex) &&
                    !WinGetFactoryHelper.IsComDisabled)
                {
                    var delay = TimeSpan.FromSeconds(attempt * 2);
                    PackageDiagnostics.Write($"{logTag} COM nova tentativa em {delay.TotalSeconds:0}s");
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!WinGetFactoryHelper.CliFallbackAllowed)
                {
                    PackageDiagnostics.Write("MODO COM-ONLY: fallback para winget.exe bloqueado");
                    return new WingetExecutionResult
                    {
                        Success = false,
                        ExitCode = ex.HResult,
                        Output = $"Falha na API COM (modo COM-only, sem fallback): {ex.Message}",
                        FailureReason = WingetFailureReason.InternalError
                    };
                }

                PackageDiagnostics.Write(
                    $"FALLBACK PARA API PRÓPRIA: motivo=COM {opKind} exception ({ex.GetType().Name} 0x{ex.HResult:X8})");
                WinProvision.Core.Services.CoreLog.Debug($"WinGet COM {opKind} failed; falling back to WinProvision API: {ex}");
                onLogReceived?.Invoke("API COM indisponível; usando a API própria da WinProvision Store.");
                return await RunApiThenCliFallbackAsync(
                    opKind,
                    packageId,
                    onLogReceived,
                    onProgress,
                    cancellationToken,
                    installLocation,
                    source,
                    $"COM-exception({ex.GetType().Name} 0x{ex.HResult:X8})",
                    state.Preferences, options).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Nível 2 da cadeia de fallback, despachado por <paramref name="opKind"/>: instalação usa
    /// <see cref="TryWinProvisionApiThenCliAsync"/> (fallback final cliente de pacotes install);
    /// atualização usa <see cref="TryApiUpdateThenCliAsync"/> (fallback final cliente de pacotes update).
    /// </summary>
    private Task<WingetExecutionResult> RunApiThenCliFallbackAsync(
        ComOperationKind opKind,
        string packageId,
        Action<string>? onLogReceived,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken cancellationToken,
        string? installLocation,
        string source,
        string reason,
        InstallerPreferences? preferences = null,
        PackageInstallOptions? options = null)
        => opKind == ComOperationKind.Install
            ? TryWinProvisionApiThenCliAsync(packageId, onLogReceived, onProgress, cancellationToken, installLocation, source, reason, preferences, options)
            : TryApiUpdateThenCliAsync(packageId, onLogReceived, onProgress, cancellationToken, source, reason,
                preferences, options);

    /// <summary>
    /// Tenta atualizar via <see cref="WinProvisionApiService.TryInstallAsync"/> (mesmo endpoint
    /// usado para instalar — ver comentário em <see cref="UpdateAsync"/>); só recorre ao
    /// cliente de pacotes (<see cref="_wingetExecutor"/>) se a API própria também não resolver. Mesmo
    /// papel de <see cref="TryWinProvisionApiThenCliAsync"/>, só que pro lado da atualização.
    /// </summary>
    private async Task<WingetExecutionResult> TryApiUpdateThenCliAsync(
        string packageId,
        Action<string>? onLogReceived,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken cancellationToken,
        string source,
        string reason,
        InstallerPreferences? preferences = null,
        PackageInstallOptions? options = null,
        bool fallbackToCli = true)
    {
        InstallerPreferences? effectivePreferences = ResolveExecutionScope(preferences);
        effectivePreferences = effectivePreferences is null
            ? options is null ? null : new InstallerPreferences(options.Scope, options.Architecture,
                options.RequiresElevation, ElevationProhibited: false)
            : effectivePreferences with
            {
                Scope = options?.Scope ?? effectivePreferences.Scope,
                Architecture = options?.Architecture ?? effectivePreferences.Architecture,
                RequiresElevation = options?.RequiresElevation == true || effectivePreferences.RequiresElevation
            };
        PackageDiagnostics.Write($"UPDATE API-PROPRIA tentativa packageId=\"{packageId}\" motivo={reason}");

        _offeredUpgrades.TryGetValue(UpgradeKey(source, packageId), out var offered);
        string? requiredVersion = options?.Version;
        if (string.IsNullOrWhiteSpace(requiredVersion)) requiredVersion = offered?.WinProvisionApiAvailableVersion;
        if (string.IsNullOrWhiteSpace(requiredVersion) && fallbackToCli) requiredVersion = offered?.AvailableVersion;
        WinProvisionUpdatePlan plan;
        try
        {
            if (!source.Equals("winget", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A WinProvision API só atualiza aplicativos clássicos do catálogo próprio.");
            plan = await PrepareApiUpdateAsync(packageId, requiredVersion, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(options?.Scope)
                && !options.Scope.Equals(plan.InstalledPackage.InstalledScope, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Uma atualização pela API deve preservar o escopo da instalação existente.");
            if (!string.IsNullOrWhiteSpace(options?.Architecture)
                && !options.Architecture.Equals(plan.Installer.Architecture, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A arquitetura solicitada não corresponde ao instalador de atualização compatível com a instalação existente.");
            effectivePreferences = new InstallerPreferences(plan.InstalledPackage.InstalledScope,
                plan.InstalledPackage.InstalledArchitecture ?? plan.Installer.Architecture,
                options?.RequiresElevation == true || plan.Installer.ElevationRequirement is "elevationRequired" or "elevatesSelf",
                plan.Installer.ElevationRequirement == "elevationProhibited");
            PackageDiagnostics.Write($"UPDATE API-PROPRIA preflight id={packageId} installed={plan.InstalledPackage.CurrentVersion} target={plan.Manifest.Version} scope={effectivePreferences.Scope} type={plan.Installer.Type} arch={effectivePreferences.Architecture}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (AlreadyUpdatedException exception)
        {
            return AlreadyUpdated(exception.InstalledVersion, exception.TargetVersion, onLogReceived);
        }
        catch (Exception exception)
        {
            PackageDiagnostics.Warning(exception, "PrepareWinProvisionApiUpdate");
            if (fallbackToCli)
            {
                onLogReceived?.Invoke($"A WinProvision API não oferece uma atualização compatível ({exception.Message}); usando WinGet.");
                return await RunCliUpdateFallbackAsync(packageId, onLogReceived, onProgress,
                    cancellationToken, source, effectivePreferences, options).ConfigureAwait(false);
            }
            return new WingetExecutionResult
            {
                Success = false, ExitCode = -1, Output = exception.Message,
                FailureReason = exception is HttpRequestException ? WingetFailureReason.DownloadError : WingetFailureReason.NoApplicableUpgrade
            };
        }
        string targetVersion = plan.Manifest.Version;

        WinProvisionInstallResult apiResult;
        try
        {
            // Envia progresso inicial para definir o método como OwnApi
            onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.OwnApi));

            apiResult = await _apiService.TryInstallAsync(
                packageId,
                onLogReceived is null ? null : new Progress<string>(onLogReceived),
                cancellationToken,
                effectivePreferences?.Architecture,
                effectivePreferences?.Scope,
                onProgress,
                source,
                requiresElevation: options?.RequiresElevation == true || (effectivePreferences?.RequiresElevation ?? false),
                elevationProhibited: effectivePreferences?.ElevationProhibited ?? false,
                suppliedManifest: plan.Manifest,
                requestedVersion: targetVersion,
                recordInstall: false).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Uma exceção inesperada pode ocorrer depois de o instalador ter começado. O
            // estado é incerto, portanto não despachamos a mesma atualização pelo CLI.
            PackageDiagnostics.Write(
                $"UPDATE API-PROPRIA exceção packageId=\"{packageId}\" {ex.GetType().Name}: {ex.Message}");
            apiResult = new WinProvisionInstallResult(
                WinProvisionInstallOutcome.InstallProcessFailed,
                Message: $"Falha inesperada com estado do instalador desconhecido: {ex.Message}", InstallerStarted: true);
        }

        if (apiResult.Outcome == WinProvisionInstallOutcome.Success)
        {
            PackageDiagnostics.Write(
                $"UPDATE API-PROPRIA sucesso packageId=\"{packageId}\" exitCode={apiResult.ExitCode}");
            onLogReceived?.Invoke("Atualização concluída via API própria da WinProvision Store.");
            return new WingetExecutionResult
            {
                Success = true,
                ExitCode = apiResult.ExitCode ?? 0,
                Output = "Atualizado via API própria da WinProvision Store.",
                ApiUpdateTargetVersion = targetVersion,
                ApiUpdateProductCode = apiResult.VerifiedProductCode ?? plan.Installer.ProductCode,
                ApiUpdateProductCodeFromInstaller = !string.IsNullOrWhiteSpace(apiResult.VerifiedProductCode),
                InstallerStarted = apiResult.InstallerStarted
            };
        }

        if (apiResult.Outcome == WinProvisionInstallOutcome.ElevationCanceled)
        {
            onLogReceived?.Invoke("A elevação foi cancelada; não será iniciada outra tentativa de atualização.");
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = apiResult.ExitCode ?? 1223,
                Output = apiResult.Message ?? "A elevação foi cancelada.",
                FailureReason = WingetFailureReason.ElevationCanceled
            };
        }

        if (!CanFallbackAfterApiUpdate(apiResult))
        {
            string message = apiResult.Message
                ?? "A API própria iniciou o instalador e falhou; não será iniciada outra tentativa automaticamente.";
            PackageDiagnostics.Write(
                $"UPDATE API-PROPRIA falhou após iniciar instalador packageId=\"{packageId}\" outcome={apiResult.Outcome} " +
                $"exitCode={apiResult.ExitCode?.ToString() ?? "indisponível"}; sem fallback para CLI");
            onLogReceived?.Invoke(message);
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = apiResult.ExitCode ?? -1,
                Output = message,
                FailureReason = InstallerExitCodes.Classify(apiResult.ExitCode ?? -1) is var nativeFailure && nativeFailure != WingetFailureReason.Unknown
                    ? nativeFailure : WingetFailureReason.InstallError,
                InstallerStarted = true
            };
        }

        if (!fallbackToCli)
        {
            string message = apiResult.Message ?? $"A WinProvision API não concluiu a atualização: {apiResult.Outcome}.";
            WingetFailureReason failureReason = WingetErrorTranslator.Classify(apiResult.ExitCode ?? -1, message);
            if (failureReason == WingetFailureReason.Unknown)
                failureReason = apiResult.Outcome switch
                {
                    WinProvisionInstallOutcome.PackageNotFound => WingetFailureReason.PackageNotInCatalog,
                    WinProvisionInstallOutcome.NoCompatibleInstaller => WingetFailureReason.NoApplicableInstallers,
                    WinProvisionInstallOutcome.DownloadFailed => WingetFailureReason.DownloadError,
                    WinProvisionInstallOutcome.HashMismatch => WingetFailureReason.InstallerHashMismatch,
                    _ => WingetFailureReason.InstallError
                };
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = apiResult.ExitCode ?? -1,
                Output = message,
                FailureReason = failureReason,
                InstallerStarted = apiResult.InstallerStarted
            };
        }
        PackageDiagnostics.Write(
            $"UPDATE API-PROPRIA falhou packageId=\"{packageId}\" outcome={apiResult.Outcome} " +
            $"msg=\"{apiResult.Message}\" — caindo pro winget.exe (nível 3)");
        onLogReceived?.Invoke(
            $"API própria não conseguiu atualizar ({apiResult.Outcome}); usando winget.exe como último recurso.");

        return await RunCliUpdateFallbackAsync(packageId, onLogReceived, onProgress,
            cancellationToken, source, effectivePreferences, options).ConfigureAwait(false);
    }

    private async Task<WingetExecutionResult> RunCliUpdateFallbackAsync(
        string packageId,
        Action<string>? onLogReceived,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken cancellationToken,
        string source,
        InstallerPreferences? effectivePreferences,
        PackageInstallOptions? options = null)
    {

        // Envia progresso inicial para definir o método como WingetExe
        onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe));

        await EnsureWingetProvisionedAsync(onLogReceived, cancellationToken).ConfigureAwait(false);
        string? targetVersion = options?.Version
            ?? (_offeredUpgrades.TryGetValue(UpgradeKey(source, packageId), out var targetOffer)
                ? targetOffer.AvailableVersion : null);
        var cliResult = await _wingetExecutor.UpdateAppAsync(
            packageId,
            onLogReceived,
            cancellationToken,
            source,
            options?.Scope ?? effectivePreferences?.Scope,
            options?.Architecture ?? effectivePreferences?.Architecture,
            options?.RequiresElevation == true || (effectivePreferences?.RequiresElevation ?? false),
            effectivePreferences?.ElevationProhibited ?? false,
            interactive: options?.Interactive ?? false,
            ignoreSecurityHash: options?.IgnoreSecurityHash ?? false,
            version: targetVersion).ConfigureAwait(false);
        if (_localIdentifiers.TryGetValue(UpgradeKey(source, packageId), out var localId)
            && CanRetryLocalIdentifier(cliResult, packageId, localId, cancellationToken))
        {
            onLogReceived?.Invoke($"O ID do catálogo não localizou a instalação; tentando o identificador local {localId}.");
            return await _wingetExecutor.UpdateAppAsync(localId, onLogReceived, cancellationToken,
                source: string.Empty, scope: options?.Scope ?? effectivePreferences?.Scope,
                architecture: options?.Architecture ?? effectivePreferences?.Architecture,
                requiresElevation: options?.RequiresElevation == true || (effectivePreferences?.RequiresElevation ?? false),
                elevationProhibited: effectivePreferences?.ElevationProhibited ?? false,
                interactive: options?.Interactive ?? false,
                ignoreSecurityHash: options?.IgnoreSecurityHash ?? false,
                version: targetVersion).ConfigureAwait(false);
        }
        return cliResult;
    }

    internal static bool CanFallbackAfterApiUpdate(WinProvisionInstallResult result) =>
        !result.InstallerStarted && result.Outcome is not (WinProvisionInstallOutcome.Success
            or WinProvisionInstallOutcome.ElevationCanceled);

    internal static bool CanRetryLocalIdentifier(WingetExecutionResult result, string catalogId,
        string? localId, CancellationToken token) =>
        !result.Success && !result.InstallerStarted && !result.WasElevated && !token.IsCancellationRequested
        && result.FailureReason is (WingetFailureReason.NoPackageFound or WingetFailureReason.PackageNotInCatalog)
        && IsUsableLocalIdentifier(catalogId, localId);

    private static bool IsUsableLocalIdentifier(string catalogId, string? localId) =>
        !string.IsNullOrWhiteSpace(localId) && localId.Length <= 256
        && !localId.Equals(catalogId, StringComparison.OrdinalIgnoreCase)
        && !localId.Any(char.IsControl);

    private static async Task<IReadOnlyList<WinGetPackageMatch>> SearchApiAsync(
        string query,
        CancellationToken cancellationToken)
    {
        WinProvision.Core.Services.CoreLog.Debug("WinGet COM search: creating resilient PackageManager.");
        var packageManager = WinGetFactoryHelper.CreateResilientPackageManager();
        var connectedCatalogs = new List<(PackageCatalog Catalog, string Source)>();
        Exception? lastConnectionError = null;

        foreach (var (kind, source) in new[]
        {
            (PredefinedPackageCatalog.OpenWindowsCatalog, "winget"),
            (PredefinedPackageCatalog.MicrosoftStore, "msstore")
        })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reference = packageManager.GetPredefinedPackageCatalog(kind);
                reference.AcceptSourceAgreements = true;
                var connected = await reference.ConnectAsync()
                    .AsTask(cancellationToken).ConfigureAwait(false);
                if (connected.Status == ConnectResultStatus.Ok && connected.PackageCatalog is { } catalog)
                {
                    connectedCatalogs.Add((catalog, source));
                }
                else
                {
                    lastConnectionError = new InvalidOperationException(
                        $"Não foi possível conectar à fonte {source}: {connected.Status}.");
                    WinProvision.Core.Services.CoreLog.Debug($"WinGet COM search connection failed for {source}: {connected.Status}.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastConnectionError = ex;
                WinProvision.Core.Services.CoreLog.Debug($"WinGet COM search could not connect to {source}: {ex.Message}");
            }
        }

        if (connectedCatalogs.Count == 0)
        {
            throw new InvalidOperationException("Não foi possível conectar a nenhuma fonte de pacotes.", lastConnectionError);
        }

        // Pesquisar os campos principais também encontra pacotes pelo identificador
        // quando o nome exibido não contém o texto digitado.
        var searches = connectedCatalogs
            .SelectMany(item => new[]
            {
                SearchFieldAsync(item.Catalog, item.Source, PackageMatchField.Name),
                SearchFieldAsync(item.Catalog, item.Source, PackageMatchField.Id),
                SearchFieldAsync(item.Catalog, item.Source, PackageMatchField.Moniker)
            })
            .ToArray();
        var batches = await Task.WhenAll(searches).ConfigureAwait(false);
        return PackageMatchDeduplicator.Deduplicate(batches, cancellationToken);

        async Task<List<WinGetPackageMatch>> SearchFieldAsync(
            PackageCatalog catalog,
            string source,
            PackageMatchField field)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var options = WinGetFactoryHelper.CreateFindPackagesOptions();
            var filter = WinGetFactoryHelper.CreatePackageMatchFilter();
            filter.Field = field;
            filter.Option = PackageFieldMatchOption.ContainsCaseInsensitive;
            filter.Value = query;
            options.Filters.Add(filter);

            try
            {
                var findResult = await catalog.FindPackagesAsync(options)
                    .AsTask(cancellationToken).ConfigureAwait(false);
                return IndexedCollectionReader.Read(findResult.Matches, cancellationToken).Select(match =>
                {
                    var package = match.CatalogPackage;
                    var version = package.DefaultInstallVersion;
                    return new WinGetPackageMatch(
                        package.Id,
                        package.Name,
                        version?.Version ?? string.Empty,
                        version?.Publisher ?? string.Empty,
                        source);
                }).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                WinProvision.Core.Services.CoreLog.Debug($"WinGet COM search failed for {source}/{field}: {ex.Message}");
                return [];
            }
        }
    }

    private static async Task<IReadOnlyList<WinGetPackageMatch>> SearchCliFallbackAsync(
        string query,
        CancellationToken cancellationToken,
        string? source = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = WingetLocator.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("search");
        startInfo.ArgumentList.Add(query);
        startInfo.ArgumentList.Add("--accept-source-agreements");
        startInfo.ArgumentList.Add("--disable-interactivity");
        if (!string.IsNullOrWhiteSpace(source))
        {
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(source);
        }

        string argumentsForLog = string.Join(" ", startInfo.ArgumentList.Select(argument => $"\"{argument}\""));
        WingetCliAudit.Launch(startInfo.FileName, argumentsForLog);
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return Array.Empty<WinGetPackageMatch>();
        }

        Task<string[]> readers = Task.WhenAll(
            process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        string output;
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var captured = await readers.ConfigureAwait(false);
            output = captured[0];
            if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(captured[1]))
                PackageDiagnostics.Write($"WinGet search failed: {captured[1]}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            { PackageDiagnostics.Error(exception, "StopWingetSearch"); }
            await process.WaitForExitAsync().ConfigureAwait(false);
            throw;
        }
        finally { await readers.ConfigureAwait(false); }

        var lines = output
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line[(line.LastIndexOf('\r') + 1)..].TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        var separatorIndex = Array.FindIndex(lines, IsTableSeparator);
        if (separatorIndex < 0)
        {
            return Array.Empty<WinGetPackageMatch>();
        }

        var headerIndex = separatorIndex - 1;
        while (headerIndex >= 0 && string.IsNullOrWhiteSpace(lines[headerIndex]))
        {
            headerIndex--;
        }

        if (headerIndex < 0)
        {
            return Array.Empty<WinGetPackageMatch>();
        }

        var columnStarts = new[] { lines[headerIndex].TakeWhile(char.IsWhiteSpace).Count() }
            .Concat(Regex.Matches(lines[headerIndex], @"\s{2,}")
                .Select(match => match.Index + match.Length))
            .ToArray();
        int minimumColumns = string.IsNullOrWhiteSpace(source) ? 4 : 3;
        if (columnStarts.Length < minimumColumns)
        {
            return Array.Empty<WinGetPackageMatch>();
        }

        var results = new List<WinGetPackageMatch>();
        foreach (var line in lines[(separatorIndex + 1)..])
        {
            var columns = ReadTableColumns(line, minimumColumns);
            if (columns is null || string.IsNullOrWhiteSpace(columns.Value.Id))
            {
                continue;
            }

            results.Add(new WinGetPackageMatch(
                columns.Value.Id,
                columns.Value.Name,
                columns.Value.Version,
                string.Empty,
                string.IsNullOrWhiteSpace(source) ? columns.Value.Source : source));
        }

        return results;
    }

    private static bool IsTableSeparator(string line) =>
        Regex.IsMatch(line, @"^\s*-{3,}\s*$");

    private static (string Name, string Id, string Version, string Source)? ReadTableColumns(
        string line,
        int minimumColumns)
    {
        var fields = Regex.Split(line.Trim(), @"\s{2,}")
            .Where(field => !string.IsNullOrWhiteSpace(field))
            .ToArray();
        if (fields.Length < minimumColumns)
        {
            WinProvision.Core.Services.CoreLog.Debug($"WinGet CLI row discarded: expected at least {minimumColumns} columns: {line}");
            return null;
        }

        var source = fields.Length >= 4 ? fields[^1] : string.Empty;
        var version = fields[2];
        var id = fields[1];
        var name = fields[0];
        // Match is optional and may contain spaces (for example, "Tag: office");
        // it is intentionally ignored because Source is always the final field.
        if (id.Contains(' ', StringComparison.Ordinal))
        {
            WinProvision.Core.Services.CoreLog.Debug($"WinGet CLI row discarded: package ID contains spaces: {line}");
            return null;
        }

        if (id.Contains('…', StringComparison.Ordinal) || id.Contains("...", StringComparison.Ordinal))
        {
            WinProvision.Core.Services.CoreLog.Debug($"WinGet CLI row discarded: package ID is truncated: {line}");
            return null;
        }

        return (name, id, version, source);
    }

    private async Task<WingetExecutionResult> RunComPackageOperationAsync(
        ComOperationKind opKind,
        string packageId,
        Action<string>? onLogReceived,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken cancellationToken,
        string? installLocation,
        string source,
        InstallAttemptState attemptState,
        bool forceComOnly,
        PackageInstallOptions options)
    {
        var stopwatch = Stopwatch.StartNew();
        // Rótulo usado em toda esta operação COM: "INSTALL COM" numa instalação nova,
        // "UPDATE COM" numa atualização — antes ficava fixo em "INSTALL COM" mesmo
        // durante updates, o que deixava o log de atualização com linhas do tipo
        // "INSTALL COM stage=install-dispatched" (confuso: parecia instalação, era update).
        string comTag = opKind == ComOperationKind.Install ? "INSTALL COM" : "UPDATE COM";
        PackageDiagnostics.Write($"{comTag} stage=begin");

        // Callbacks da UI nunca devem derrubar o pipeline COM (uma exceção aqui viraria "falha da COM").
        var userOnProgress = onProgress;
        if (userOnProgress is not null)
        {
            onProgress = update =>
            {
                try { userOnProgress(update); }
                catch (Exception ex) { PackageDiagnostics.Write($"{comTag} onProgress lançou {ex.GetType().Name}: {ex.Message}"); }
            };
        }

        var userOnLog = onLogReceived;
        if (userOnLog is not null)
        {
            onLogReceived = line =>
            {
                try { userOnLog(line); }
                catch (Exception ex) { PackageDiagnostics.Write($"{comTag} onLog lançou {ex.GetType().Name}: {ex.Message}"); }
            };
        }
        WinProvision.Core.Services.CoreLog.Debug("WinGet COM install: creating resilient PackageManager.");
        var packageManager = WinGetFactoryHelper.CreateResilientPackageManager();
        PackageDiagnostics.Write($"{comTag} stage=packageManager-created elapsed={stopwatch.Elapsed}");
        WinProvision.Core.Services.CoreLog.Debug($"WinGet COM install: PackageManager created in {stopwatch.Elapsed}.");
        // A fonte principal reúne os pacotes comuns. Aplicativos do catálogo visual (source=msstore) ficam no
        // catálogo correspondente; se não achar lá, ainda tenta o catálogo de pacotes.
        var catalogKinds = string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase)
            ? new[] { PredefinedPackageCatalog.MicrosoftStore, PredefinedPackageCatalog.OpenWindowsCatalog }
            : new[] { PredefinedPackageCatalog.OpenWindowsCatalog };

        CatalogPackage? matchedPackage = null;
        var connectedAny = false;
        string? lastConnectFailure = null;
        for (var i = 0; i < catalogKinds.Length && matchedPackage is null; i++)
        {
            var kind = catalogKinds[i];
            var packageCatalogReference = packageManager.GetPredefinedPackageCatalog(kind);
            packageCatalogReference.AcceptSourceAgreements = true;
            var connectStarted = stopwatch.Elapsed;
            var connectResult = await packageCatalogReference.ConnectAsync()
                .AsTask(cancellationToken).ConfigureAwait(false);
            PackageDiagnostics.Write(
                $"{comTag} stage=connect-completed catalog={kind} status={connectResult.Status} " +
                $"elapsed={stopwatch.Elapsed} connect={stopwatch.Elapsed - connectStarted}");
            if (connectResult.Status != ConnectResultStatus.Ok || connectResult.PackageCatalog is null)
            {
                lastConnectFailure = $"Falha ao conectar ao catálogo do WinGet ({kind}): {connectResult.Status}.";
                continue;
            }

            connectedAny = true;
            WinGetFactoryHelper.ReportComSuccess();
            var packageCatalog = connectResult.PackageCatalog;

            var findOptions = WinGetFactoryHelper.CreateFindPackagesOptions();
            var filter = WinGetFactoryHelper.CreatePackageMatchFilter();
            filter.Field = PackageMatchField.Id;
            filter.Option = PackageFieldMatchOption.Equals;
            filter.Value = packageId;
            findOptions.Filters.Add(filter);

            var findStarted = stopwatch.Elapsed;
            var findResult = await packageCatalog.FindPackagesAsync(findOptions)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (findResult.Status != FindPackagesResultStatus.Ok)
                throw new WinGetComPreflightException(
                    $"A consulta COM do pacote falhou antes da atualização: {findResult.Status}.");
            matchedPackage = IndexedCollectionReader.Read(findResult.Matches, cancellationToken).FirstOrDefault()?.CatalogPackage;
            PackageDiagnostics.Write(
                $"{comTag} stage=package-lookup-completed catalog={kind} found={matchedPackage is not null} " +
                $"elapsed={stopwatch.Elapsed} lookup={stopwatch.Elapsed - findStarted}");
        }

        if (!connectedAny)
        {
            throw new WinGetComPreflightException(
                lastConnectFailure ?? "Falha ao conectar ao catálogo do WinGet.",
                isTransient: true);
        }

        if (matchedPackage is null)
        {
            // Não é falha de instalação: o índice COM pode estar defasado. Deixa o chamador decidir
            // (modo auto tenta o CLI; modo COM-only mostra este erro).
            throw new WinGetComPreflightException($"Pacote não encontrado no catálogo COM: {packageId}");
        }

        if (opKind == ComOperationKind.Upgrade)
        {
            // UpgradePackageAsync exige a oferta remota associada à instalação local.
            var composite = WinGetFactoryHelper.CreateCompositePackageCatalogOptions();
            composite.CompositeSearchBehavior = CompositeSearchBehavior.RemotePackagesFromAllCatalogs;
            foreach (var kind in catalogKinds)
            {
                var reference = packageManager.GetPredefinedPackageCatalog(kind);
                reference.AcceptSourceAgreements = true;
                composite.Catalogs.Add(reference);
            }
            var compositeReference = packageManager.CreateCompositePackageCatalog(composite);
            var compositeConnection = await compositeReference.ConnectAsync()
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (compositeConnection.Status != ConnectResultStatus.Ok || compositeConnection.PackageCatalog is null)
                throw new WinGetComPreflightException(
                    $"Não foi possível correlacionar a instalação com o catálogo COM: {compositeConnection.Status}.");

            var compositeFind = WinGetFactoryHelper.CreateFindPackagesOptions();
            var compositeFilter = WinGetFactoryHelper.CreatePackageMatchFilter();
            compositeFilter.Field = PackageMatchField.Id;
            compositeFilter.Option = PackageFieldMatchOption.Equals;
            compositeFilter.Value = packageId;
            compositeFind.Filters.Add(compositeFilter);
            var compositeMatches = await compositeConnection.PackageCatalog.FindPackagesAsync(compositeFind)
                .AsTask(cancellationToken).ConfigureAwait(false);
            if (compositeMatches.Status != FindPackagesResultStatus.Ok)
                throw new WinGetComPreflightException(
                    $"A correlação COM entre pacote instalado e oferta remota falhou: {compositeMatches.Status}.");
            if (compositeMatches.WasLimitExceeded)
                throw new WinGetComPreflightException("A correlação COM foi limitada antes de identificar a instalação.");
            matchedPackage = IndexedCollectionReader.Read(compositeMatches.Matches, cancellationToken)
                .Select(match => match.CatalogPackage)
                .FirstOrDefault(package => package.InstalledVersion is not null
                    && package.DefaultInstallVersion is not null
                    && package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
            if (matchedPackage is null)
                throw new WinGetComPreflightException(
                    $"O pacote {packageId} não possui versão instalada correlacionada no catálogo COM.");
        }

        var installOptions = WinGetFactoryHelper.CreateInstallOptions();
        // catálogo visual packages may require Store UI/consent and don't behave like
        // conventional silent installers. Preserve silent mode for gerenciador de pacotes manifests.
        installOptions.PackageInstallMode = options.Interactive || string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase)
            ? PackageInstallMode.Interactive
            : PackageInstallMode.Silent;
        installOptions.AcceptPackageAgreements = true;
        installOptions.PreferredInstallLocation = installLocation;
        if (string.Equals(options.Scope, "machine", StringComparison.OrdinalIgnoreCase))
            installOptions.PackageInstallScope = PackageInstallScope.System;
        else if (string.Equals(options.Scope, "user", StringComparison.OrdinalIgnoreCase))
            installOptions.PackageInstallScope = PackageInstallScope.User;
        bool isSystem = WinProvisionApiService.IsRunningAsSystem();
        if (isSystem)
            installOptions.PackageInstallScope = PackageInstallScope.System;

        InstallerPreferences? preferences = isSystem
            ? new InstallerPreferences("machine", null, RequiresElevation: false, ElevationProhibited: false)
            : null;
        try
        {
            var installerInfo = matchedPackage.DefaultInstallVersion?.GetApplicableInstaller(installOptions)
                ?? matchedPackage.InstalledVersion?.GetApplicableInstaller(installOptions);
            if (installerInfo is not null)
            {
                string? scope = installerInfo.Scope switch
                {
                    PackageInstallerScope.User => "user",
                    PackageInstallerScope.System => "machine",
                    _ => null
                };
                bool requiresElevation = installerInfo.ElevationRequirement is
                    ElevationRequirement.ElevationRequired or ElevationRequirement.ElevatesSelf;
                bool elevationProhibited = installerInfo.ElevationRequirement is
                    ElevationRequirement.ElevationProhibited;
                string? architecture = ReadInstallerArchitecture(installerInfo);
                string? effectiveScope = options.Scope ?? (isSystem ? "machine" : scope);
                architecture = options.Architecture ?? architecture;
                preferences = new InstallerPreferences(effectiveScope, architecture, requiresElevation, elevationProhibited);
                attemptState.Preferences = preferences;

                if (isSystem && string.Equals(scope, "user", StringComparison.OrdinalIgnoreCase))
                {
                    // Nunca executar um instalador de escopo de usuário sob a identidade
                    // LocalSystem: isso gravaria o pacote no perfil errado. Tenta os níveis
                    // seguintes explicitamente em escopo de máquina.
                    var machinePreferences = preferences with { Scope = "machine" };
                    onLogReceived?.Invoke(
                        "O catálogo indicou instalador somente para usuário, mas o processo é SYSTEM; tentando os níveis seguintes em escopo de máquina.");
                    return await RunApiThenCliFallbackAsync(
                        opKind, packageId, onLogReceived, onProgress, cancellationToken,
                        installLocation, source, "SYSTEM-requer-escopo-machine", machinePreferences, options)
                        .ConfigureAwait(false);
                }

                if (string.Equals(effectiveScope, "machine", StringComparison.OrdinalIgnoreCase))
                    installOptions.PackageInstallScope = PackageInstallScope.System;
                else if (string.Equals(effectiveScope, "user", StringComparison.OrdinalIgnoreCase))
                    installOptions.PackageInstallScope = PackageInstallScope.User;

                PackageDiagnostics.Write(
                    $"{comTag} installer-metadata scope={scope ?? "default"} arch={architecture ?? "auto"} " +
                    $"elevation={(requiresElevation ? "required" : elevationProhibited ? "prohibited" : "default")}");
                onLogReceived?.Invoke(requiresElevation
                    ? "O instalador requer elevação; a operação vai solicitar autorização se o WinGet precisar dela."
                    : "Opções do instalador verificadas.");

                if (elevationProhibited && IsCurrentProcessElevated())
                {
                    const string error = "Este instalador não pode ser executado com privilégios de administrador. Feche a Store e abra-a sem elevação.";
                    onLogReceived?.Invoke(error);
                    return new WingetExecutionResult
                    {
                        Success = false,
                        ExitCode = unchecked((int)0x8A150056),
                        Output = error,
                        FailureReason = WingetFailureReason.ElevationProhibited
                    };
                }
            }
            else
            {
                PackageDiagnostics.Write($"{comTag} installer-metadata unavailable; WinGet will select scope/architecture");
            }
        }
        catch (Exception ex)
        {
            // A pré-consulta nunca deve impedir a instalação. Se o metadado COM estiver
            // ausente ou inconsistente, o gerenciador de pacotes escolhe o instalador e seus requisitos.
            PackageDiagnostics.Write(
                $"{comTag} installer-metadata unavailable {ex.GetType().Name}: {ex.Message}");
        }

        // Route BEFORE dispatch: self-elevating installers otherwise show their own
        // UAC prompt and cannot reuse the host's helper de elevação authorization.
        if (!InProcessWinGetFactory.Enabled && preferences is not null
            && InstallerElevationPolicy.RequiresElevatedComHost(source, preferences.Scope,
                preferences.RequiresElevation, preferences.ElevationProhibited))
        {
            onLogReceived?.Invoke("A operação COM será executada no host local elevado pelo WinProvision Elevator.");
            var elevatedResult = await RunElevatedComWorkerAsync(opKind, packageId, installLocation, onLogReceived,
                onProgress, cancellationToken, attemptState).ConfigureAwait(false);
            if (CanFallbackFromElevatedWorker(elevatedResult, forceComOnly, cancellationToken))
            {
                onLogReceived?.Invoke("O host COM elevado falhou antes de iniciar o instalador; usando a API própria da WinProvision Store.");
                return await RunApiThenCliFallbackAsync(opKind, packageId, onLogReceived, onProgress,
                    cancellationToken, installLocation, source, "host-COM-elevado-inicializacao",
                    attemptState.Preferences, options).ConfigureAwait(false);
            }
            return elevatedResult;
        }
        onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.ComApi));
        attemptState.Started = true;
        PackageDiagnostics.Write($"{comTag} stage=install-dispatched elapsed={stopwatch.Elapsed}");
        var installOperation = opKind == ComOperationKind.Install
            ? packageManager.InstallPackageAsync(matchedPackage, installOptions)
            : packageManager.UpgradePackageAsync(matchedPackage, installOptions);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watchdogCts = new CancellationTokenSource();
        using var heartbeatCts = new CancellationTokenSource();
        var lastProgress = Stopwatch.GetTimestamp();
        var lastProgressState = PackageInstallProgressState.Queued;
        var lastDownloadProgress = 0d;
        var lastInstallationProgress = 0d;
        var firstProgress = 0;
        var progressCallbacks = 0;
        var lastLoggedPercent = -1;
        var watchdogTimedOut = 0;
        var lastUiUpdate = TimeSpan.MinValue;
        var hasUiUpdate = false;
        var lastUiPercent = -1;
        var lastUiState = (PackageInstallProgressState)(-1);
        var progressLock = new System.Threading.Lock();
        IProgress<InstallProgress> progress = new SynchronousProgress<InstallProgress>(value =>
        {
            var state = value.State;
            if (state == PackageInstallProgressState.Installing)
                attemptState.InstallerStarted = true;
            var downloadProgress = value.DownloadProgress;
            var installationProgress = value.InstallationProgress;
            lastProgressState = state;
            lastDownloadProgress = downloadProgress;
            lastInstallationProgress = installationProgress;
            Volatile.Write(ref lastProgress, Stopwatch.GetTimestamp());

            if (Interlocked.Exchange(ref firstProgress, 1) == 0)
            {
                PackageDiagnostics.Write(
                    $"{comTag} stage=first-progress state={state} " +
                    $"download={downloadProgress} install={installationProgress} " +
                    $"dispatcher=false elapsed={stopwatch.Elapsed}");
                WinProvision.Core.Services.CoreLog.Debug(
                    $"WinGet COM install: first progress callback at {stopwatch.Elapsed}.");
                watchdogCts.Cancel();
            }

            var callbackNumber = Interlocked.Increment(ref progressCallbacks);
            var percentForLog = state == PackageInstallProgressState.Downloading
                ? (int)Math.Clamp(Math.Round(downloadProgress * 100), 0, 100)
                : (int)Math.Clamp(Math.Round(installationProgress * 100), 0, 100);
            if (callbackNumber <= 5 ||
                (percentForLog >= 0 && percentForLog / 10 > lastLoggedPercent / 10))
            {
                Interlocked.Exchange(ref lastLoggedPercent, percentForLog);
                PackageDiagnostics.Write(
                    $"{comTag} progress callback={callbackNumber} state={state} " +
                    $"download={downloadProgress} install={installationProgress} " +
                    $"dispatcher=false elapsed={stopwatch.Elapsed}");
            }

            var now = stopwatch.Elapsed;
            lock (progressLock)
            {
                if (state is PackageInstallProgressState.Downloading or
                    PackageInstallProgressState.Installing)
                {
                    lastUiState = state;
                    var isDownloading = state == PackageInstallProgressState.Downloading;

                    // DownloadProgress fica em 0 em alguns catálogos até o primeiro byte
                    // chegar; BytesDownloaded/BytesRequired cobrem esse intervalo.
                    var progressValue = isDownloading
                        ? (downloadProgress > 0 || value.BytesRequired <= 0
                            ? downloadProgress
                            : (double)value.BytesDownloaded / value.BytesRequired)
                        : installationProgress;
                    var percent = (int)Math.Clamp(
                        Math.Round(progressValue * 100, MidpointRounding.AwayFromZero),
                        0,
                        100);
                    var phase = isDownloading
                        ? InstallProgressPhase.Downloading
                        : InstallProgressPhase.Installing;

                    if (isDownloading && percent >= 100)
                    {
                        if (lastUiPercent != 100)
                        {
                            lastUiPercent = 100;
                            lastUiUpdate = now;
                            hasUiUpdate = true;
                            onProgress?.Invoke(new InstallProgressUpdate(
                                InstallProgressPhase.Preparing, Method: WingetMethod.ComApi));
                        }
                    }
                    else if (percent != lastUiPercent &&
                        (!hasUiUpdate ||
                         now - lastUiUpdate >= ProgressUpdateInterval))
                    {
                        lastUiPercent = percent;
                        lastUiUpdate = now;
                        hasUiUpdate = true;
                        onProgress?.Invoke(new InstallProgressUpdate(phase, percent, WingetMethod.ComApi));
                    }
                }
                else if (state != lastUiState)
                {
                    lastUiState = state;
                    lastUiUpdate = now;
                    hasUiUpdate = true;
                    switch (state)
                    {
                        case PackageInstallProgressState.Queued:
                            onProgress?.Invoke(new InstallProgressUpdate(
                                InstallProgressPhase.Preparing, Method: WingetMethod.ComApi));
                            break;
                        case PackageInstallProgressState.PostInstall:
                            onProgress?.Invoke(new InstallProgressUpdate(
                                InstallProgressPhase.Installing, Method: WingetMethod.ComApi));
                            break;
                        case PackageInstallProgressState.Finished:
                            onProgress?.Invoke(new InstallProgressUpdate(
                                InstallProgressPhase.Installing, 100, WingetMethod.ComApi));
                            break;
                    }
                }
            }
        });

        var watchdog = MonitorComProgressAsync(
            watchdogCts.Token,
            operationCts,
            () => Stopwatch.GetElapsedTime(Volatile.Read(ref lastProgress)),
            () => Interlocked.Exchange(ref watchdogTimedOut, 1),
            stopwatch);
        PackageDiagnostics.WriteComServerInfo();
        var heartbeat = MonitorComHeartbeatAsync(
            comTag,
            heartbeatCts.Token,
            () => (lastProgressState, lastDownloadProgress, lastInstallationProgress),
            () => Stopwatch.GetElapsedTime(Volatile.Read(ref lastProgress)),
            stopwatch);
        InstallResult installResult;
        try
        {
            installResult = await installOperation.AsTask(operationCts.Token, progress)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            Volatile.Read(ref watchdogTimedOut) == 1 &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"A API COM não reportou progresso por {ComProgressTimeout.TotalSeconds:0} segundos.");
        }
        finally
        {
            watchdogCts.Cancel();
            heartbeatCts.Cancel();
            await watchdog.ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
        }

        var output = $"Resultado da API COM: {installResult.Status}.";
        PackageDiagnostics.Write(
            $"{comTag} stage=result status={installResult.Status} " +
            $"reboot={installResult.RebootRequired} elapsed={stopwatch.Elapsed}");
        WinProvision.Core.Services.CoreLog.Debug($"WinGet COM install result: {installResult.Status}.");
        WinProvision.Core.Services.CoreLog.Debug($"WinGet COM install completed in {stopwatch.Elapsed}.");
        if (installResult.RebootRequired)
        {
            output += " Reinicialização necessária.";
        }

        if (installResult.Status != InstallResultStatus.Ok)
        {
            int installerErrorCode = unchecked((int)installResult.InstallerErrorCode);
            int extendedErrorCode = installResult.ExtendedErrorCode?.HResult ?? 0;
            PackageDiagnostics.Write(
                $"{comTag} failure status={installResult.Status} " +
                $"installerErrorCode=0x{installerErrorCode:X8} ({installerErrorCode}) " +
                $"extendedErrorCode=0x{extendedErrorCode:X8} ({extendedErrorCode})");

            bool requiresElevation =
                installerErrorCode == 740 || extendedErrorCode == unchecked((int)0x800702E4);

            // O instalador chegou a rodar e devolveu o próprio código de erro: repetir por outro
            // caminho executaria o mesmo instalador de novo (risco de instalação dupla/parcial).
            // O gerenciador de pacotes classifica opções inválidas e ausência de instalador aplicável
            // antes de executar o instalador (PackageManager.cpp/GetPackageVersion).
            // Um callback pode informar Installing antes desse HRESULT, mas nenhum instalador
            // foi iniciado; só neste caso é seguro continuar pela cadeia de fallback.
            bool preExecutionComFailure = IsPreExecutionComFailure(installerErrorCode, extendedErrorCode);
            bool installerFailedItself = !preExecutionComFailure && (attemptState.InstallerStarted
                || installerErrorCode != 0 && !requiresElevation);

            // Política bloqueando o pacote: contornar pela API própria seria burlar a política.
            bool blockedByPolicy = installResult.Status == InstallResultStatus.BlockedByPolicy;

            if (CanRetryComWithElevation(requiresElevation, attemptState.InstallerStarted,
                    blockedByPolicy, source, options, IsCurrentProcessElevated(),
                    InProcessWinGetFactory.Enabled, cancellationToken))
            {
                onLogReceived?.Invoke("O WinGet solicitou privilégios administrativos antes de iniciar o instalador; tentando a COM no host elevado...");
                var elevatedAttempt = new InstallAttemptState();
                var elevatedResult = await RunElevatedComWorkerAsync(opKind, packageId, installLocation,
                    onLogReceived, onProgress, cancellationToken, elevatedAttempt).ConfigureAwait(false);
                if (!CanFallbackFromElevatedWorker(elevatedResult, forceComOnly, cancellationToken))
                    return elevatedResult;

                onLogReceived?.Invoke("A COM elevada falhou antes de iniciar o instalador; tentando a API própria.");
            }

            // Qualquer outra falha da COM (download, dependências do pacote, catálogo, erro
            // interno, sem instalador aplicável...) cai pra API própria e depois pro cliente de pacotes.
            // Antes só a elevação caía; o resto voltava como falha final, sem fallback.
            if (!forceComOnly && WinGetFactoryHelper.CliFallbackAllowed && !installerFailedItself && !blockedByPolicy)
            {
                string reason = requiresElevation
                    ? "COM-requer-elevação"
                    : $"COM-status-{installResult.Status}(0x{extendedErrorCode:X8})";

                PackageDiagnostics.Write($"FALLBACK PARA API PRÓPRIA: motivo={reason}");
                string actionLower = opKind == ComOperationKind.Install ? "instalação" : "atualização";
                onLogReceived?.Invoke(requiresElevation
                    ? $"A {actionLower} requer privilégios de administrador; usando a API própria da WinProvision Store."
                    : $"A API COM falhou ({installResult.Status}); usando a API própria da WinProvision Store.");
                return await RunApiThenCliFallbackAsync(
                    opKind,
                    packageId,
                    onLogReceived,
                    onProgress,
                    cancellationToken,
                    installLocation,
                    source,
                    reason,
                    attemptState.Preferences, options).ConfigureAwait(false);
            }

            int failureCode = GetComFailureExitCode(installerErrorCode, extendedErrorCode);
            var failureReason = InstallerExitCodes.Classify(failureCode);
            if (failureReason == WingetFailureReason.Unknown)
                failureReason = MapInstallFailure(installResult.Status);
            if (installResult.InstallerErrorCode == 0 && installResult.ExtendedErrorCode is { } extendedError)
            {
                output += $" Erro estendido: {extendedError.Message}";
            }

            WinProvision.Core.Services.CoreLog.Debug(output);
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = failureCode,
                Output = output,
                FailureReason = failureReason,
                InstallerStarted = !preExecutionComFailure && (attemptState.InstallerStarted || installerErrorCode != 0 && !requiresElevation)
            };
        }

        string actionCap = opKind == ComOperationKind.Install ? "Instalação" : "Atualização";
        onLogReceived?.Invoke(
            installResult.RebootRequired
                ? $"{actionCap} concluída via API COM; reinicialização necessária."
                : $"{actionCap} concluída via API COM do WinGet.");
        return new WingetExecutionResult
        {
            Success = true,
            ExitCode = 0,
            RebootRequired = installResult.RebootRequired,
            Output = output
        };
    }

    private static async Task MonitorComProgressAsync(
        CancellationToken watchdogCancellationToken,
        CancellationTokenSource operationCts,
        Func<TimeSpan> elapsedSinceProgress,
        Action onTimeout,
        Stopwatch stopwatch)
    {
        try
        {
            while (!watchdogCancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), watchdogCancellationToken)
                    .ConfigureAwait(false);
                if (elapsedSinceProgress() < ComProgressTimeout)
                {
                    continue;
                }

                WinProvision.Core.Services.CoreLog.Debug(
                    $"WinGet COM install: no progress for {ComProgressTimeout.TotalSeconds:0} seconds at {stopwatch.Elapsed}; cancelling.");
                onTimeout();
                operationCts.Cancel();
                return;
            }
        }
        catch (OperationCanceledException)
        {
            PackageDiagnostics.Write($"COM watchdog cancelado após {stopwatch.Elapsed}.");
        }
    }

    private static async Task MonitorComHeartbeatAsync(
        string comTag,
        CancellationToken cancellationToken,
        Func<(PackageInstallProgressState State, double Download, double Install)> getProgress,
        Func<TimeSpan> elapsedSinceProgress,
        Stopwatch stopwatch)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken)
                    .ConfigureAwait(false);
                var progress = getProgress();
                PackageDiagnostics.Write(
                    $"{comTag} heartbeat state={progress.State} " +
                    $"download={progress.Download} install={progress.Install} " +
                    $"sinceLastCallback={elapsedSinceProgress()} elapsed={stopwatch.Elapsed}");
            }
        }
        catch (OperationCanceledException)
        {
            PackageDiagnostics.Write($"{comTag} heartbeat cancelado após {stopwatch.Elapsed}.");
        }
    }

    private static WingetFailureReason MapInstallFailure(InstallResultStatus status) =>
        status switch
        {
            InstallResultStatus.BlockedByPolicy => WingetFailureReason.BlockedByPolicy,
            InstallResultStatus.NoApplicableInstallers => WingetFailureReason.NoApplicableInstallers,
            InstallResultStatus.PackageAgreementsNotAccepted =>
                WingetFailureReason.PackageAgreementsNotAccepted,
            InstallResultStatus.DownloadError => WingetFailureReason.DownloadError,
            InstallResultStatus.InstallError => WingetFailureReason.InstallError,
            InstallResultStatus.CatalogError => WingetFailureReason.CatalogError,
            InstallResultStatus.InternalError => WingetFailureReason.InternalError,
            _ => WingetFailureReason.Unknown
        };

    internal static bool IsPreExecutionComFailure(int installerErrorCode, int extendedErrorCode) =>
        installerErrorCode == 0 && unchecked((uint)extendedErrorCode) is 0x8A150002 or 0x8A150010;

    internal static int GetComFailureExitCode(int installerErrorCode, int extendedErrorCode) =>
        installerErrorCode != 0 ? installerErrorCode : extendedErrorCode;

    private static string? ReadInstallerArchitecture(PackageInstallerInfo installerInfo)
    {
        try
        {
            string? value = installerInfo.Architecture.ToString();
            return value?.Trim().ToLowerInvariant() switch
            {
                "x86" => "x86",
                "x64" => "x64",
                "arm64" => "arm64",
                "arm" => "arm",
                _ => null
            };
        }
        catch (Exception recoveryException)
        {
            PackageDiagnostics.Warning(recoveryException, "WinGetService.ReadInstallerArchitecture");

            return null;
        }
    }

    private static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>
    /// Nível 2 da cadeia de fallback (COM -&gt; API própria -&gt; CLI): tenta instalar via
    /// <see cref="WinProvisionApiService"/> (catálogo próprio hospedado no R2); só recorre
    /// ao cliente de pacotes (<see cref="_wingetExecutor"/>) se a API própria também não resolver
    /// (pacote fora do catálogo próprio, sem installer compatível, hash divergente, etc.).
    /// Chamado a partir de todo ponto onde a API COM do gerenciador de pacotes é pulada ou falha, e do
    /// bypass direto de SYSTEM/reelevação-falhada no início de <see cref="InstallAsync"/>.
    /// </summary>
    private async Task<WingetExecutionResult> TryWinProvisionApiThenCliAsync(
        string packageId,
        Action<string>? onLogReceived,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken cancellationToken,
        string? installLocation,
        string source,
        string reason,
        InstallerPreferences? preferences = null,
        PackageInstallOptions? options = null)
    {
        options ??= new PackageInstallOptions(InstallLocation: installLocation);
        InstallerPreferences? effectivePreferences = ResolveExecutionScope(preferences);
        PackageDiagnostics.Write($"INSTALL API-PROPRIA tentativa packageId=\"{packageId}\" motivo={reason}");

        if (RequiresWingetCliForApiInstallOptions(options))
        {
            onLogReceived?.Invoke("A API própria não oferece suporte a todas as opções solicitadas; preservando-as pelo WinGet CLI.");
            onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe));
            await EnsureWingetProvisionedAsync(onLogReceived, cancellationToken).ConfigureAwait(false);
            var constrainedOptionsResult = await _wingetExecutor.InstallAppAsync(packageId, onLogReceived, cancellationToken,
                installLocation, source, options.Scope ?? effectivePreferences?.Scope,
                options.Architecture ?? effectivePreferences?.Architecture,
                options.RequiresElevation || (effectivePreferences?.RequiresElevation ?? false),
                effectivePreferences?.ElevationProhibited ?? false, options.Interactive,
                options.IgnoreSecurityHash, options.Version, options.CustomArguments).ConfigureAwait(false);
            if (!constrainedOptionsResult.Success) constrainedOptionsResult.InstallerStarted = true;
            return constrainedOptionsResult;
        }

        WinProvisionInstallResult apiResult;
        try
        {
            // Envia progresso inicial para definir o método como OwnApi
            onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.OwnApi));

            apiResult = await TryInstallApiWithCompatibilityRetryAsync(
                packageId,
                onLogReceived is null ? null : new Progress<string>(onLogReceived),
                cancellationToken,
                options.Architecture ?? effectivePreferences?.Architecture,
                options.Scope ?? effectivePreferences?.Scope,
                onProgress,
                source,
                options.RequiresElevation || (effectivePreferences?.RequiresElevation ?? false),
                effectivePreferences?.ElevationProhibited ?? false,
                options.Version).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Uma exceção inesperada pode ter ocorrido depois de iniciar o instalador.
            // Como o estado é desconhecido, encerra sem executar o pacote por outro backend.
            PackageDiagnostics.Write(
                $"INSTALL API-PROPRIA exceção packageId=\"{packageId}\" {ex.GetType().Name}: {ex.Message}");
            apiResult = new WinProvisionInstallResult(
                WinProvisionInstallOutcome.InstallProcessFailed,
                Message: $"Falha inesperada com estado do instalador desconhecido: {ex.Message}",
                InstallerStarted: true);
        }

        if (apiResult.Outcome == WinProvisionInstallOutcome.Success)
        {
            PackageDiagnostics.Write(
                $"INSTALL API-PROPRIA sucesso packageId=\"{packageId}\" exitCode={apiResult.ExitCode}");
            onLogReceived?.Invoke("Instalação concluída via API própria da WinProvision Store.");
            return new WingetExecutionResult
            {
                Success = true,
                ExitCode = apiResult.ExitCode ?? 0,
                Output = "Instalado via API própria da WinProvision Store."
            };
        }

        if (apiResult.Outcome == WinProvisionInstallOutcome.ElevationCanceled)
        {
            onLogReceived?.Invoke("A elevação foi cancelada; não será iniciada outra tentativa de instalação.");
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = apiResult.ExitCode ?? 1223,
                Output = apiResult.Message ?? "A elevação foi cancelada.",
                FailureReason = WingetFailureReason.ElevationCanceled
            };
        }

        if (apiResult.InstallerStarted)
        {
            string message = apiResult.Message
                ?? "O instalador da WinProvision API foi iniciado e falhou; não será iniciado outro backend automaticamente.";
            PackageDiagnostics.Write(
                $"INSTALL API-PROPRIA falhou após iniciar instalador packageId=\"{packageId}\" outcome={apiResult.Outcome} " +
                $"exitCode={apiResult.ExitCode?.ToString() ?? "indisponível"}; sem fallback para CLI");
            onLogReceived?.Invoke(message);
            return new WingetExecutionResult
            {
                Success = false,
                ExitCode = apiResult.ExitCode ?? -1,
                Output = message,
                FailureReason = InstallerExitCodes.Classify(apiResult.ExitCode ?? -1) is var nativeFailure && nativeFailure != WingetFailureReason.Unknown
                    ? nativeFailure : WingetFailureReason.InstallError,
                InstallerStarted = true
            };
        }

        PackageDiagnostics.Write(
            $"INSTALL API-PROPRIA falhou packageId=\"{packageId}\" outcome={apiResult.Outcome} " +
            $"msg=\"{apiResult.Message}\" — caindo pro winget.exe (nível 3)");
        onLogReceived?.Invoke(
            $"API própria não conseguiu instalar ({apiResult.Outcome}); usando winget.exe como último recurso.");

        // Envia progresso inicial para definir o método como WingetExe
        onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe));

        await EnsureWingetProvisionedAsync(onLogReceived, cancellationToken).ConfigureAwait(false);
        var cliResult = await _wingetExecutor.InstallAppAsync(
            packageId,
            onLogReceived,
            cancellationToken,
            installLocation,
            source,
            options.Scope ?? effectivePreferences?.Scope,
            options.Architecture ?? effectivePreferences?.Architecture,
            options.RequiresElevation || (effectivePreferences?.RequiresElevation ?? false),
            effectivePreferences?.ElevationProhibited ?? false,
            options.Interactive, options.IgnoreSecurityHash, options.Version, options.CustomArguments).ConfigureAwait(false);
        // O gerenciador de pacotes CLI não expõe ao chamador o instante em que transfere o controle ao
        // instalador. Em caso de falha, tratamos o estado como incerto e não repetimos o
        // pacote automaticamente por outra tentativa do /auto.
        if (!cliResult.Success)
            cliResult.InstallerStarted = true;
        return cliResult;
    }

    private async Task<WinProvisionInstallResult> TryInstallApiWithCompatibilityRetryAsync(
        string packageId, IProgress<string>? progress, CancellationToken token,
        string? architecture, string? scope, Action<InstallProgressUpdate>? onProgress,
        string source, bool requiresElevation, bool elevationProhibited, string? requestedVersion)
    {
        var result = await _apiService.TryInstallAsync(packageId, progress, token,
            preferredArchitecture: architecture, preferredScope: scope, onProgress: onProgress,
            source: source, requiresElevation: requiresElevation, elevationProhibited: elevationProhibited,
            requestedVersion: requestedVersion).ConfigureAwait(false);
        if (!ShouldRetryApiWithoutInstallerConstraints(result, architecture is not null || scope is not null,
                alreadyRetried: false, token: token))
            return result;

        token.ThrowIfCancellationRequested();
        PackageDiagnostics.Write($"INSTALL API-PROPRIA retry=without-arch-scope packageId=\"{packageId}\"");
        onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.OwnApi));
        return await _apiService.TryInstallAsync(packageId, progress, token,
            preferredArchitecture: null, preferredScope: null, onProgress: onProgress, source: source,
            requiresElevation: requiresElevation, elevationProhibited: elevationProhibited,
            requestedVersion: requestedVersion).ConfigureAwait(false);
    }

    internal static bool ShouldRetryApiWithoutInstallerConstraints(WinProvisionInstallResult result,
        bool hasConstraints, bool alreadyRetried, CancellationToken token)
        => !token.IsCancellationRequested && hasConstraints && !alreadyRetried
            && result.Outcome == WinProvisionInstallOutcome.NoCompatibleInstaller
            && !result.InstallerStarted;

    private static InstallerPreferences? ResolveExecutionScope(InstallerPreferences? preferences)
    {
        if (!WinProvisionApiService.IsRunningAsSystem())
            return preferences;

        // Sob SYSTEM, um escopo não informado também deve ser tratado como máquina:
        // instalar em HKCU significaria gravar no perfil da conta LocalSystem.
        return preferences is null
            ? new InstallerPreferences("machine", null, RequiresElevation: false, ElevationProhibited: false)
            : preferences with { Scope = "machine" };
    }
}
