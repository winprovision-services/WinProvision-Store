using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Microsoft.Extensions.Logging;

namespace WinProvision.PackageManagement;

/// <summary>Coordena os serviços de pacotes usados pela interface e pela CLI.</summary>
public sealed class WinGetService
{
    private readonly WingetExecutor _wingetExecutor;
    private readonly Func<WinGetEnginePreference> _preferredEngine;
    private readonly Action<string> _diagnostic;
    private readonly WinGetReadinessService _readiness;
    private readonly WinGetComOperationService _comOperations;
    private readonly AppLogTaskExecutor? _taskExecutor;
    private readonly WinGetSearchService _search;
    private readonly WinGetUpdateDiscoveryService _discovery;
    private readonly WinGetFallbackService _fallback;
    private readonly WinGetOperationVerificationService _verification;

    public WinGetService(
        WingetExecutor wingetExecutor,
        WingetBootstrapper bootstrapper,
        Func<WinGetEnginePreference>? preferredEngine = null,
        Func<Action<string>?, CancellationToken, Task<WingetBootstrapResult>>? provision = null,
        Action<string>? diagnostic = null,
        Func<CancellationToken, Task<List<UpgradablePackage>>>? installedInventory = null,
        string? stuckUpgradeStatePath = null,
        AppLogTaskExecutor? taskExecutor = null,
        ILogger? comLogger = null)
    {
        _diagnostic = diagnostic ?? PackageDiagnostics.Write;
        _taskExecutor = taskExecutor;
        _comOperations = new WinGetComOperationService(taskExecutor, logger: comLogger);
        _wingetExecutor = wingetExecutor;
        _preferredEngine = preferredEngine ?? (() => WinGetEnginePreference.WingetCli);
        _readiness = new WinGetReadinessService(bootstrapper,
            provision ?? ((log, token) => bootstrapper.EnsureOnceAsync(log, token)), _diagnostic);
        _search = new WinGetSearchService((probeCom, log, token) =>
            _readiness.EnsureWingetProvisionedAsync(log, token, probeCom),
            (query, includeStore, token) => taskExecutor is null
                ? WinGetComQueryService.SearchAsync(query, token, includeStore)
                : taskExecutor.RunNativeAsync(new AppLogTaskDescriptor(PackageManagerLogCategories.Winget,
                    "WinGet.COM", "Search", "WinGet"), innerToken => WinGetComQueryService.SearchAsync(query, innerToken, includeStore), token),
            (query, source, token) => WinGetCliSearchService.SearchAsync(query, token, wingetExecutor, source),
            _preferredEngine, () => WinGetFactoryHelper.Mode, _diagnostic);
        _discovery = new WinGetUpdateDiscoveryService(wingetExecutor, _readiness,
            installedInventory, stuckUpgradeStatePath, taskExecutor: taskExecutor);
        _fallback = new WinGetFallbackService(wingetExecutor, _readiness, _discovery);
        _verification = new WinGetOperationVerificationService(wingetExecutor, _discovery, _diagnostic);
    }

    public Task PrepareAsync(CancellationToken cancellationToken = default, bool includeMicrosoftStoreCatalog = false,
        Action<string>? onLogReceived = null) =>
        _readiness.PrepareAsync(cancellationToken, includeMicrosoftStoreCatalog, onLogReceived);

    public record WinGetPackageMatch(
        string PackageId,
        string Name,
        string Version,
        string Publisher,
        string Source);

    public Task<IReadOnlyList<WinGetPackageMatch>> SearchAsync(string query, CancellationToken cancellationToken = default,
        bool includeMicrosoftStore = true) =>
        _search.SearchAsync(query, cancellationToken, includeMicrosoftStore);

    public async Task<IReadOnlyList<WinGetPackageMatch>> SearchComOnlyAsync(string query, CancellationToken token)
    {
        await RequireComAsync(token).ConfigureAwait(false);
        return _taskExecutor is null
            ? await WinGetComQueryService.SearchAsync(query, token).ConfigureAwait(false)
            : await _taskExecutor.RunNativeAsync(new AppLogTaskDescriptor(PackageManagerLogCategories.Winget,
                "WinGet.COM", "Search", "WinGet"), innerToken => WinGetComQueryService.SearchAsync(query, innerToken), token).ConfigureAwait(false);
    }

    public async Task<List<UpgradablePackage>> ListComOnlyAsync(string? source, CancellationToken token,
        bool includeInstallerMetadata = false)
    {
        await RequireComAsync(token).ConfigureAwait(false);
        return _taskExecutor is null
            ? await WinGetComQueryService.ListInstalledAsync(source, token, includeInstallerMetadata).ConfigureAwait(false)
            : await _taskExecutor.RunNativeAsync(new AppLogTaskDescriptor(PackageManagerLogCategories.Winget,
                "WinGet.COM", "ListInstalled", "WinGet"), innerToken => WinGetComQueryService.ListInstalledAsync(source, innerToken, includeInstallerMetadata), token).ConfigureAwait(false);
    }

    public async Task<List<UpgradablePackage>> ListInstalledAsync(string? source, CancellationToken token,
        bool includeInstallerMetadata = false)
    {
        WinGetRouteDecision decision = WinGetRouteResolver.Resolve(WinGetRouteOperation.ListInstalled,
            _preferredEngine(), WinGetFactoryHelper.Mode);
        _diagnostic($"WINGET ROUTE operation=list-installed preference={decision.Preference} route={decision.Route} reason={decision.Reason} fallback=false");
        if (decision.Route == WinGetRoute.WingetCli)
        {
            await _readiness.EnsureWingetProvisionedAsync(null, token, probeCom: false).ConfigureAwait(false);
            return await _wingetExecutor.ListInstalledPackagesAsync(token, source).ConfigureAwait(false);
        }
        try
        {
            await RequireComAsync(token).ConfigureAwait(false);
            var packages = await ListComOnlyAsync(source, token, includeInstallerMetadata).ConfigureAwait(false);
            _diagnostic($"WINGET ROUTE RESULT operation=list-installed preference={decision.Preference} route=com count={packages.Count} success=true fallback=false");
            return packages;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            WinGetRouteDecision? fallback = WinGetRouteResolver.ResolveFallback(decision,
                WinGetFailurePhase.BeforeDispatch, exception is TimeoutException ? "timeout" : "query-error");
            if (fallback is null) throw;
            _diagnostic($"WINGET ROUTE operation=list-installed preference={decision.Preference} route=winget-cli count=null success=false fallback=true reason={fallback.Reason}");
            await _readiness.EnsureWingetProvisionedAsync(null, token, probeCom: false).ConfigureAwait(false);
            return await _wingetExecutor.ListInstalledPackagesAsync(token, source).ConfigureAwait(false);
        }
    }

    public async Task<WingetExecutionResult?> UninstallComOnlyAsync(string id, string? source, CancellationToken token,
        Action<string>? log = null)
    {
        if (!await _readiness.EnsureWingetProvisionedAsync(null, token).ConfigureAwait(false) || WinGetFactoryHelper.IsComDisabled)
            return null;
        return await _comOperations.UninstallAsync(id, source, log, token).ConfigureAwait(false);
    }

    public async Task<WingetExecutionResult> UninstallAsync(string id, string? source,
        Action<string>? log, CancellationToken token, bool interactive = false,
        bool requiresElevation = false, PackageInstallOptions? options = null)
    {
        options ??= new PackageInstallOptions();
        WinGetRouteDecision decision = WinGetRouteResolver.Resolve(WinGetRouteOperation.Uninstall,
            _preferredEngine(), WinGetFactoryHelper.Mode,
            !string.IsNullOrWhiteSpace(options.UninstallArguments));
        _diagnostic($"WINGET ROUTE operation=uninstall preference={decision.Preference} route={decision.Route} reason={decision.Reason} fallback=false phase=before-dispatch");
        if (decision.Route == WinGetRoute.WingetCli)
        {
            await _readiness.EnsureWingetProvisionedAsync(log, token, probeCom: false).ConfigureAwait(false);
            log?.Invoke("Executando desinstalação por winget.exe (WinGet CLI).");
            return LogUninstallResult(id, decision, WinGetRoute.WingetCli, await RunCliUninstallAsync(id, source, log, token, interactive, requiresElevation, options).ConfigureAwait(false));
        }
        try
        {
            await RequireComAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            WinGetRouteDecision? fallback = WinGetRouteResolver.ResolveFallback(decision,
                WinGetFailurePhase.BeforeDispatch, exception is TimeoutException ? "timeout" : "query-error");
            if (fallback is null) return LogUninstallResult(id, decision, WinGetRoute.ComApi,
                new WingetExecutionResult { Success = false, ExitCode = exception.HResult,
                    FailureReason = WingetFailureReason.InternalError, Output = $"Falha ao desinstalar pela rota COM do WinGet: {exception.Message}" });
            _diagnostic($"WINGET ROUTE operation=uninstall preference={decision.Preference} route=winget-cli reason={fallback.Reason} fallback=true phase=before-dispatch");
            try
            {
                await _readiness.EnsureWingetProvisionedAsync(log, token, probeCom: false).ConfigureAwait(false);
                log?.Invoke("A API COM ficou indisponível antes do despacho; fallback para winget.exe.");
                return LogUninstallResult(id, decision with { Route = WinGetRoute.WingetCli, IsFallback = true }, WinGetRoute.WingetCli,
                    await RunCliUninstallAsync(id, source, log, token, interactive, requiresElevation, options).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception cliException) { return LogUninstallResult(id, decision, WinGetRoute.WingetCli,
                new WingetExecutionResult { Success = false, ExitCode = cliException.HResult,
                    FailureReason = WingetFailureReason.InternalError, Output = $"Falha ao desinstalar pelo WinGet CLI: {cliException.Message}" }); }
        }

        WingetExecutionResult? comResult;
        try
        {
            log?.Invoke("Comunicando com a API COM do WinGet...");
            comResult = await UninstallComOnlyAsync(id, source, token, log).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return LogUninstallResult(id, decision, WinGetRoute.ComApi, new WingetExecutionResult { Success = false, ExitCode = exception.HResult, InstallerStarted = true,
                FailureReason = WingetFailureReason.InternalError,
                Output = $"A rota COM falhou durante a operação; o início do desinstalador é incerto e nenhuma segunda rota foi iniciada. {exception.Message}" });
        }
        if (comResult is null)
            return LogUninstallResult(id, decision, WinGetRoute.ComApi, new WingetExecutionResult { Success = false, ExitCode = -1, InstallerStarted = false,
                FailureReason = WingetFailureReason.PackageNotInCatalog,
                Output = "O pacote não foi localizado na lista de instalados retornada pela API COM do WinGet." });
        if (comResult.Success || decision.Preference == WinGetEnginePreference.ComApi
            || comResult.WasElevated || comResult.InstallerStarted || comResult.IsUnconfirmed
            || comResult.FailureReason is WingetFailureReason.ElevationCanceled or WingetFailureReason.BlockedByPolicy or WingetFailureReason.OperationCanceled)
            return LogUninstallResult(id, decision, WinGetRoute.ComApi, comResult);
        WinGetRouteDecision? resultFallback = WinGetRouteResolver.ResolveFallback(decision,
            WinGetFailurePhase.BeforeDispatch, $"COM-status-{comResult.FailureReason}");
        if (resultFallback is null) return LogUninstallResult(id, decision, WinGetRoute.ComApi, comResult);
        _diagnostic($"WINGET ROUTE operation=uninstall preference={decision.Preference} route=winget-cli reason={resultFallback.Reason} fallback=true phase=before-dispatch");
        log?.Invoke("A API COM falhou antes de iniciar o desinstalador; fallback para winget.exe.");
        await _readiness.EnsureWingetProvisionedAsync(log, token, probeCom: false).ConfigureAwait(false);
        log?.Invoke("Executando desinstalação por winget.exe (WinGet CLI).");
        return LogUninstallResult(id, decision with { Route = WinGetRoute.WingetCli, IsFallback = true }, WinGetRoute.WingetCli,
            await RunCliUninstallAsync(id, source, log, token, interactive, requiresElevation, options).ConfigureAwait(false));
    }

    private Task<WingetExecutionResult> RunCliUninstallAsync(string id, string? source, Action<string>? log,
        CancellationToken token, bool interactive, bool requiresElevation, PackageInstallOptions options) =>
        !string.IsNullOrWhiteSpace(options.UninstallArguments)
            ? _wingetExecutor.UninstallAppAsync(id, log, token, source, interactive: interactive,
                requiresElevation: requiresElevation || options.RequiresElevation, customArguments: options.UninstallArguments)
            : _wingetExecutor.UninstallExactPackageAsync(id, source, log, token);

    private WingetExecutionResult LogUninstallResult(string id, WinGetRouteDecision decision, WinGetRoute route,
        WingetExecutionResult result) =>
        LogRouteResult("uninstall", id, decision.Preference, route, decision.IsFallback, result);

    private WingetExecutionResult LogRouteResult(string operation, string id, WinGetEnginePreference preference,
        WinGetRoute route, bool fallback, WingetExecutionResult result)
    {
        _diagnostic($"WINGET ROUTE RESULT operation={operation} packageId={id} preference={preference} route={route} rawCount=unknown success={result.Success} fallback={fallback} phase={(result.InstallerStarted ? "after-dispatch" : "before-dispatch")} exitCode={result.ExitCode} installerStarted={result.InstallerStarted} outcome={(result.Success ? "completed" : result.IsUnconfirmed ? "uncertain" : "failed")}");
        return result;
    }

    private async Task RequireComAsync(CancellationToken token)
    {
        if (!await _readiness.EnsureWingetProvisionedAsync(null, token).ConfigureAwait(false) || WinGetFactoryHelper.IsComDisabled)
            throw new InvalidOperationException("API COM indisponível.");
    }

    public Task<List<UpgradablePackage>> GetUpgradablePackagesAsync(Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default, bool includeMicrosoftStore = true) =>
        _discovery.GetUpgradablePackagesAsync(_preferredEngine(), onLogReceived, cancellationToken, includeMicrosoftStore);

    public Task<WingetExecutionResult> VerifySuccessfulUpgradeAsync(UpgradablePackage offered,
        WingetExecutionResult result, CancellationToken cancellationToken) =>
        _verification.VerifySuccessfulUpgradeAsync(offered, result, cancellationToken);

    public async Task<WingetExecutionResult> InstallAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string? installLocation = null,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget")
    {
        var result = await RunInstallOrUpgradeAsync(
            ComOperationKind.Install, packageId, onLogReceived, cancellationToken, installLocation, onProgress, source)
            .ConfigureAwait(false);
        return await _verification.VerifyInstallResultAsync(packageId, source, null, result, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<WingetExecutionResult> InstallPreferredAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        PackageInstallOptions? options = null,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget")
        => InstallWithMethodAsync(packageId, _preferredEngine().ToInstallMethod(), onLogReceived,
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
        string? requestedVersion = options?.Version ?? version;
        int observedRoute = (int)WingetMethod.Unknown;
        void ObserveProgress(InstallProgressUpdate update)
        {
            if (update.Method != WingetMethod.Unknown) Interlocked.Exchange(ref observedRoute, (int)update.Method);
            onProgress?.Invoke(update);
        }
        var result = await InstallWithMethodCoreAsync(packageId, method, onLogReceived, cancellationToken,
            installLocation, ObserveProgress, source, version, preferredScope, preferredArchitecture, options)
            .ConfigureAwait(false);
        var verified = await _verification.VerifyInstallResultAsync(packageId, source, requestedVersion, result, cancellationToken)
            .ConfigureAwait(false);
        WingetMethod actualMethod = (WingetMethod)Volatile.Read(ref observedRoute);
        if (actualMethod == WingetMethod.Unknown) actualMethod = method switch
        {
            PackageInstallMethod.ComApi => WingetMethod.ComApi,
            PackageInstallMethod.WingetCli => WingetMethod.WingetExe,
            _ when options is not null && RequiresWingetCliForInstallOptions(options) => WingetMethod.WingetExe,
            _ => WingetMethod.ComApi
        };
        _diagnostic($"WINGET ROUTE RESULT operation=install packageId={packageId} selected={method} route={RouteName(actualMethod)} success={verified.Success} exitCode={verified.ExitCode} installerStarted={verified.InstallerStarted} outcome={(verified.Success ? "completed" : verified.IsUnconfirmed ? "uncertain" : "failed")}");
        return verified;
    }

    private async Task<WingetExecutionResult> InstallWithMethodCoreAsync(
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
        WinGetEnginePreference preference = method switch
        {
            PackageInstallMethod.ComApi => WinGetEnginePreference.ComApi,
            PackageInstallMethod.WingetCli => WinGetEnginePreference.WingetCli,
            PackageInstallMethod.Automatic or PackageInstallMethod.WinProvisionApi => WinGetEnginePreference.Automatic,
            _ => _preferredEngine()
        };
        WinGetRouteDecision route = WinGetRouteResolver.Resolve(WinGetRouteOperation.Install, preference,
            WinGetFactoryHelper.Mode, RequiresWingetCliForInstallOptions(options));
        _diagnostic($"WINGET ROUTE operation=install packageId={packageId} preference={preference} route={route.Route} reason={route.Reason} phase=before-dispatch");
        if (route.Route == WinGetRoute.ComApi && RequiresWingetCliForInstallOptions(options))
            return new WingetExecutionResult { Success = false, ExitCode = -1,
                FailureReason = WingetFailureReason.NoApplicableInstallers,
                Output = "A API COM exclusiva não oferece suporte às opções de arquitetura, tipo, idioma, argumentos personalizados, troca prévia ou ignorar hash solicitadas." };
        if (route.Route == WinGetRoute.WingetCli)
        {
            onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe));
            await _readiness.EnsureWingetProvisionedAsync(onLogReceived, cancellationToken, probeCom: false).ConfigureAwait(false);
            var variant = await _fallback.PrepareCliVariantSelectionAsync(packageId, source, options, cancellationToken).ConfigureAwait(false);
            var cliPreferences = variant.Preferences;
            return await _wingetExecutor.InstallAppAsync(packageId, onLogReceived, cancellationToken,
                installLocation, source, variant.Selection.Scope ?? preferredScope ?? cliPreferences?.Scope,
                variant.Selection.Architecture ?? preferredArchitecture ?? cliPreferences?.Architecture,
                cliPreferences?.RequiresElevation ?? false, cliPreferences?.ElevationProhibited ?? false,
                options.Interactive, options.IgnoreSecurityHash, options.Version, options.CustomArguments,
                uninstallPrevious: options.UninstallPrevious, installerType: variant.Selection.InstallerType,
                locale: variant.Selection.Locale, selection: variant.Selection).ConfigureAwait(false);
        }
        return await RunInstallOrUpgradeAsync(ComOperationKind.Install, packageId, onLogReceived,
            cancellationToken, installLocation, onProgress, source,
            forceComOnly: preference == WinGetEnginePreference.ComApi, options: options).ConfigureAwait(false);
    }

    internal static bool RequiresWingetCliForInstallOptions(PackageInstallOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // Arquitetura, tipo, idioma e argumentos específicos só são executáveis pelo CLI.
        return !string.IsNullOrWhiteSpace(options.Architecture)
            || !string.IsNullOrWhiteSpace(options.InstallerType)
            || !string.IsNullOrWhiteSpace(options.Locale)
            || !string.IsNullOrWhiteSpace(options.CustomArguments)
            || options.UninstallPrevious
            || options.IgnoreSecurityHash;
    }

    /// <summary>Atualiza uma instalação existente e confere a versão oferecida.</summary>
    public async Task<WingetExecutionResult> UpdateAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        string source = "winget",
        Action<InstallProgressUpdate>? onProgress = null,
        UpgradablePackage? offeredPackage = null,
        PackageInstallOptions? updateOptions = null)
    {
        int observedRoute = (int)WingetMethod.Unknown;
        void ObserveProgress(InstallProgressUpdate update)
        {
            if (update.Method != WingetMethod.Unknown) Interlocked.Exchange(ref observedRoute, (int)update.Method);
            onProgress?.Invoke(update);
        }
        var offered = offeredPackage ?? _discovery.GetOffer(source, packageId);
        PackageUpdateMethod method = _preferredEngine().ToUpdateMethod();
        string? selectedTarget = updateOptions?.Version;
        if (string.IsNullOrWhiteSpace(selectedTarget))
            selectedTarget = offered?.AvailableVersion;
        if (offered is not null && !string.IsNullOrWhiteSpace(selectedTarget))
        {
            var freshInventory = await _discovery.GetUpdateInventoryAsync(cancellationToken).ConfigureAwait(false);
            var current = WinGetOperationVerificationService.ResolveCurrentUpdateInstallation(offered, freshInventory);
            if (current is not null && WinGetOperationVerificationService.IsUpdateSatisfied(current.CurrentVersion, selectedTarget))
                return WinGetFallbackService.AlreadyUpdated(current.CurrentVersion, selectedTarget, onLogReceived);
        }
        if (offered is not null)
            _discovery.RecordOffer(source, packageId, offered);
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
        string initialRoute = method switch
        {
            PackageUpdateMethod.ComApi => "com",
            PackageUpdateMethod.WingetCli => "winget-cli",
            _ => "com"
        };
        _diagnostic($"WINGET ROUTE operation=update packageId={packageId} preference={method} route={initialRoute} reason={(requiresCli ? "options-require-cli" : "preference-or-explicit-method")}");

        var result = method switch
        {
            PackageUpdateMethod.ComApi => await RunInstallOrUpgradeAsync(
                ComOperationKind.Upgrade, packageId, onLogReceived, cancellationToken, null, ObserveProgress,
                source, forceComOnly: true, options: updateOptions, installedPackage: offered).ConfigureAwait(false),
            PackageUpdateMethod.WingetCli => await _fallback.RunCliUpdateFallbackAsync(
                packageId, onLogReceived, ObserveProgress, cancellationToken, source,
                WinGetFallbackService.ResolveExecutionScope(null), updateOptions, offered).ConfigureAwait(false),
            _ => await RunInstallOrUpgradeAsync(
                ComOperationKind.Upgrade, packageId, onLogReceived, cancellationToken, null, ObserveProgress,
                source, options: updateOptions, installedPackage: offered).ConfigureAwait(false)
        };
        WingetExecutionResult verified = !result.Success || result.UpdateAlreadySatisfied || result.UpdateVersionVerified
            ? result
            : verificationOffer is null
            ? WinGetOperationVerificationService.MarkUpdateUnverified(result, "A operação terminou, mas não havia uma versão-alvo conhecida para conferir.")
            : await VerifySuccessfulUpgradeAsync(verificationOffer, result, cancellationToken).ConfigureAwait(false);
        WingetMethod actualMethod = (WingetMethod)Volatile.Read(ref observedRoute);
        if (actualMethod == WingetMethod.Unknown) actualMethod = method switch
        {
            PackageUpdateMethod.ComApi => WingetMethod.ComApi,
            PackageUpdateMethod.WingetCli => WingetMethod.WingetExe,
            _ when requiresCli => WingetMethod.WingetExe,
            _ => WingetMethod.ComApi
        };
        _diagnostic($"WINGET ROUTE RESULT operation=update packageId={packageId} selected={method} route={RouteName(actualMethod)} success={verified.Success} exitCode={verified.ExitCode} installerStarted={verified.InstallerStarted} outcome={(verified.Success ? "completed" : verified.IsUnconfirmed ? "uncertain" : "failed")}");
        return verified;
    }

    private static string RouteName(WingetMethod method) => method switch
    {
        WingetMethod.ComApi => "com",
        WingetMethod.WingetExe => "winget-cli",
        _ => "unknown"
    };

    internal static bool RequiresCliForUpdateOptions(PackageInstallOptions? options) => options is not null
        && (!string.IsNullOrWhiteSpace(options.CustomArguments)
            || !string.IsNullOrWhiteSpace(options.UpdateArguments)
            || !string.IsNullOrWhiteSpace(options.Architecture)
            || !string.IsNullOrWhiteSpace(options.InstallerType)
            || !string.IsNullOrWhiteSpace(options.Locale)
            || options.IgnoreSecurityHash);

    /// <summary>Worker elevado: executa somente a COM; o processo chamador confere a versão ofertada.</summary>
    public Task<WingetExecutionResult> UpdateComOnlyAsync(
        string packageId,
        Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget",
        PackageInstallOptions? options = null) =>
        RunInstallOrUpgradeAsync(ComOperationKind.Upgrade, packageId, onLogReceived,
            cancellationToken, null, onProgress, source, forceComOnly: true, options: options);

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
        PackageInstallOptions? options = null,
        UpgradablePackage? installedPackage = null)
    {
        options ??= new PackageInstallOptions(InstallLocation: installLocation);
        installLocation = options.InstallLocation ?? installLocation;
        bool install = opKind == ComOperationKind.Install;
        WinGetRouteOperation operation = install ? WinGetRouteOperation.Install : WinGetRouteOperation.Update;
        WinGetEnginePreference preference = forceComOnly ? WinGetEnginePreference.ComApi : _preferredEngine();
        WinGetRouteDecision route = WinGetRouteResolver.Resolve(operation, preference, WinGetFactoryHelper.Mode,
            install ? RequiresWingetCliForInstallOptions(options) : RequiresCliForUpdateOptions(options));
        if (forceComOnly && route.Route != WinGetRoute.ComApi)
            return new WingetExecutionResult { Success = false, ExitCode = -1, Output = "A rota COM exclusiva não está disponível neste modo.", FailureReason = WingetFailureReason.InternalError };
        _diagnostic($"WINGET ROUTE operation={operation} preference={preference} route={route.Route} reason={route.Reason} phase=before-dispatch");
        if (route.Route == WinGetRoute.WingetCli)
        {
            if (!install)
                return await _fallback.RunCliUpdateFallbackAsync(packageId, onLogReceived, onProgress, cancellationToken,
                    source, WinGetFallbackService.ResolveExecutionScope(null), options, installedPackage).ConfigureAwait(false);
            return await RunWinGetCliFallbackAsync(opKind, packageId, onLogReceived, onProgress,
                cancellationToken, installLocation, source, null, options).ConfigureAwait(false);
        }

        try
        {
            bool ready = await _readiness.EnsureWingetProvisionedAsync(onLogReceived, cancellationToken).ConfigureAwait(false);
            if (!ready) throw new InvalidOperationException("A API COM do WinGet não ficou disponível antes do despacho.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            WinGetRouteDecision? unavailableFallback = forceComOnly ? null : WinGetRouteResolver.ResolveFallback(route,
                WinGetFailurePhase.BeforeDispatch, ex is TimeoutException ? "timeout" : "unavailable");
            if (unavailableFallback is null) return new WingetExecutionResult { Success = false, ExitCode = ex.HResult,
                Output = $"WinGet COM indisponível antes do despacho: {ex.Message}", FailureReason = WingetFailureReason.InternalError };
            _diagnostic($"WINGET ROUTE operation={operation} preference={preference} route=winget-cli fallback=true reason={unavailableFallback.Reason} phase=before-dispatch");
            onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe, IsFallback: true));
            return install
                ? await RunWinGetCliFallbackAsync(opKind, packageId, onLogReceived, onProgress, cancellationToken, installLocation, source, null, options, isFallback: true).ConfigureAwait(false)
                : await _fallback.RunCliUpdateFallbackAsync(packageId, onLogReceived, onProgress, cancellationToken, source, null, options, installedPackage, isFallback: true).ConfigureAwait(false);
        }
        WinGetComOperationResult outcome;
        try
        {
            outcome = await _comOperations.ExecuteAsync(opKind, packageId, onLogReceived, onProgress,
                cancellationToken, installLocation, source, preference == WinGetEnginePreference.ComApi, options).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            WinGetRouteDecision? fallbackRoute = forceComOnly || !WinGetFactoryHelper.IsActivationFailure(ex) ? null
                : WinGetRouteResolver.ResolveFallback(route, WinGetFailurePhase.BeforeDispatch, "activation-error");
            if (fallbackRoute is null) return new WingetExecutionResult { Success = false, ExitCode = ex.HResult,
                Output = $"Falha na rota COM do WinGet: {ex.Message}", FailureReason = WingetFailureReason.InternalError };
            _diagnostic($"WINGET ROUTE operation={operation} preference={preference} route=winget-cli fallback=true reason={fallbackRoute.Reason} phase=before-dispatch");
            onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe, IsFallback: true));
            return install
                ? await RunWinGetCliFallbackAsync(opKind, packageId, onLogReceived, onProgress, cancellationToken, installLocation, source, null, options, isFallback: true).ConfigureAwait(false)
                : await _fallback.RunCliUpdateFallbackAsync(packageId, onLogReceived, onProgress, cancellationToken, source, null, options, isFallback: true).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (outcome.FallbackReason is null || forceComOnly) return outcome.Result;
        WinGetRouteDecision? fallback = WinGetRouteResolver.ResolveFallback(route, WinGetFailurePhase.BeforeDispatch, outcome.FallbackReason);
        if (fallback is null) return outcome.Result;
        _diagnostic($"WINGET ROUTE operation={operation} preference={preference} route=winget-cli fallback=true reason={fallback.Reason} phase=before-dispatch");
        onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe, IsFallback: true));
        return install
            ? await RunWinGetCliFallbackAsync(opKind, packageId, onLogReceived, onProgress, cancellationToken, installLocation, source, outcome.Preferences, options, isFallback: true).ConfigureAwait(false)
            : await _fallback.RunCliUpdateFallbackAsync(packageId, onLogReceived, onProgress, cancellationToken, source, outcome.Preferences, options, installedPackage, isFallback: true).ConfigureAwait(false);
    }

    private async Task<WingetExecutionResult> RunWinGetCliFallbackAsync(
        ComOperationKind opKind, string packageId, Action<string>? log,
        Action<InstallProgressUpdate>? progress, CancellationToken token, string? installLocation,
        string source, InstallerPreferences? preferences, PackageInstallOptions options, bool isFallback = false)
    {
        progress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe, IsFallback: isFallback));
        await _readiness.EnsureWingetProvisionedAsync(log, token).ConfigureAwait(false);
        var selectionOptions = options with
        {
            Scope = options.Scope ?? preferences?.Scope,
            Architecture = options.Architecture ?? preferences?.Architecture,
            InstallerType = options.InstallerType ?? preferences?.InstallerType
        };
        var variant = await _fallback.PrepareCliVariantSelectionAsync(packageId, source, selectionOptions, token,
            preferences).ConfigureAwait(false);
        preferences = variant.Preferences;
        return opKind == ComOperationKind.Install
            ? await _wingetExecutor.InstallAppAsync(packageId, log, token, installLocation, source,
                variant.Selection.Scope, variant.Selection.Architecture,
                preferences?.RequiresElevation ?? false,
                preferences?.ElevationProhibited ?? false,
                options.Interactive, options.IgnoreSecurityHash, options.Version, options.CustomArguments,
                uninstallPrevious: options.UninstallPrevious, installerType: variant.Selection.InstallerType,
                locale: variant.Selection.Locale, selection: variant.Selection).ConfigureAwait(false)
            : await _fallback.RunCliUpdateFallbackAsync(packageId, log, progress, token, source, preferences, options, isFallback: isFallback)
                .ConfigureAwait(false);
    }

}
