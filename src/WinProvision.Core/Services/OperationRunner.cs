using System.Runtime.Versioning;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Office;

namespace WinProvision.Core.Services;

/// <summary>
/// Enfileira e executa uma operação de instalação/remoção via gerenciador de pacotes ou motor próprio,
/// atualizando o <see cref="OperationItem"/> correspondente (status, progresso, cancelamento)
/// que o painel de operações exibe em tempo real.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class OperationRunner
{
    private static Func<string, Action<string>?, CancellationToken, PackageInstallOptions, Action<InstallProgressUpdate>?, string, Task<WingetExecutionResult>>? _installHandler;
    private static Action<string, string>? _successfulInstallObserver;
    private static Func<bool, Action<string>?, CancellationToken, Task>? _installWarmupHandler;
    private static Func<string, Action<string>?, CancellationToken, string, Action<InstallProgressUpdate>?, UpgradablePackage?, PackageInstallOptions?, Task<WingetExecutionResult>>? _updateHandler;
    private static Func<UpgradablePackage, WingetExecutionResult, CancellationToken, Task<WingetExecutionResult>>? _updateVerifier;
    private static Func<string, string, PackageInstallOptions?>? _packageInstallOptionsProvider;
    private static Func<string, string, string?, CancellationToken, Task<string?>>? _installedVersionResolver;

    public static void ConfigureInstalledVersionResolver(
        Func<string, string, string?, CancellationToken, Task<string?>> resolver)
    {
        _installedVersionResolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    private static async Task ReconcileInstalledVersionAsync(OperationItem item, CancellationToken token)
    {
        try
        {
            string? installedVersion = await (_installedVersionResolver?.Invoke(
                item.Manager, item.PackageId, item.SourceName, token) ?? Task.FromResult<string?>(null)).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(installedVersion))
                item.VersionTo = installedVersion.Trim();
        }
        catch (Exception exception)
        {
            CoreLog.Debug($"[OperationRunner] Não foi possível reconciliar a versão de {item.PackageId}: {exception.Message}");
        }
    }

    public static void ConfigurePackageInstallOptionsProvider(
        Func<string, string, PackageInstallOptions?> provider)
    {
        _packageInstallOptionsProvider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public static void ConfigureInstallHandler(
        Func<string, Action<string>?, CancellationToken, PackageInstallOptions, Action<InstallProgressUpdate>?, string, Task<WingetExecutionResult>> installHandler)
    {
        _installHandler = installHandler ?? throw new ArgumentNullException(nameof(installHandler));
    }

    public static void ConfigureSuccessfulInstallObserver(Action<string, string> observer)
    {
        _successfulInstallObserver = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    private static void ReportSuccessfulInstall(string packageId, string source)
    {
        try { _successfulInstallObserver?.Invoke(packageId, source); }
        catch (Exception ex) { WinProvision.Core.Services.CoreLog.Debug($"[OperationRunner] Métrica de instalação indisponível: {ex.Message}"); }
    }

    public static void ConfigureInstallWarmupHandler(
        Func<bool, Action<string>?, CancellationToken, Task> warmupHandler)
    {
        _installWarmupHandler = warmupHandler ?? throw new ArgumentNullException(nameof(warmupHandler));
    }

    public static Task WarmupConfiguredInstallHandlerAsync(
        bool includeMicrosoftStoreCatalog,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken)
        => _installWarmupHandler is null
            ? Task.CompletedTask
            : _installWarmupHandler(includeMicrosoftStoreCatalog, onLogReceived, cancellationToken);

    /// <summary>
    /// Configura o handler de atualização (API COM/CLI do gerenciador de pacotes, com fallback interno para a
    /// API própria da WinProvision Store — ver WinGetService.UpdateAsync). Sem handler
    /// configurado, RunUpdateAsync usa WingetExecutor.UpdateAppAsync diretamente (cliente de pacotes
    /// puro, sem a API própria como alternativa).
    /// </summary>
    public static void ConfigureUpdateHandler(
        Func<string, Action<string>?, CancellationToken, string, Action<InstallProgressUpdate>?, UpgradablePackage?, PackageInstallOptions?, Task<WingetExecutionResult>> updateHandler)
    {
        _updateHandler = updateHandler ?? throw new ArgumentNullException(nameof(updateHandler));
    }

    public static void ConfigureUpdateVerifier(
        Func<UpgradablePackage, WingetExecutionResult, CancellationToken, Task<WingetExecutionResult>> verifier)
    {
        _updateVerifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
    }

    /// <summary>
    /// Instala usando o handler configurado (API COM do gerenciador de pacotes, com fallback interno para a
    /// CLI). Sem handler configurado, usa o <see cref="WingetExecutor"/> diretamente. Usado
    /// também pelo modo /auto, para que ele não rode sempre o cliente de pacotes.
    /// </summary>
    public static async Task<WingetExecutionResult> InstallWithConfiguredHandlerAsync(
        WingetExecutor executor,
        string appId,
        Action<string>? onLogReceived,
        CancellationToken cancellationToken,
        string? installLocation = null,
        Action<InstallProgressUpdate>? onProgress = null,
        string source = "winget",
        PackageInstallOptions? options = null)
    {
        options ??= new PackageInstallOptions(InstallLocation: installLocation);
        var installHandler = _installHandler;
        Task<WingetExecutionResult> operation = installHandler is null
            ? executor.InstallAppAsync(appId, onLogReceived, cancellationToken, installLocation, source)
            : installHandler(appId, onLogReceived, cancellationToken, options, onProgress, source);
        WingetExecutionResult result = await operation.ConfigureAwait(false);
        if (installHandler is null)
            result = MarkInstallUnconfirmedIfNeeded(result);
        if (result.Success)
            ReportSuccessfulInstall(appId, result.EffectiveSource ?? source);
        return result;
    }

    public static async Task<WingetExecutionResult> RunInstallAsync(
        OperationsQueueService queue,
        WingetExecutor executor,
        string appId,
        string appName,
        string? iconUrl = null,
        InstalledAppsService? installedAppsService = null,
        string? installLocation = null,
        string source = "winget",
        bool interactive = false,
        string? scope = null,
        string? architecture = null,
        bool ignoreSecurityHash = false,
        bool useSavedPackageOptions = true,
        bool requiresElevation = false,
        string? version = null,
        string? customArguments = null,
        string? overrideArguments = null,
        string? preInstallScript = null,
        string? postInstallScript = null,
        bool runInstallHooks = false,
        bool abortInstallOnPreScriptFailure = true,
        IEnumerable<string>? processesToClose = null,
        bool forceCloseProcesses = false,
        bool uninstallPrevious = false,
        string? requestedVersion = null,
        PackageInstallOptions? packageOptions = null)
    {
        var installHandler = _installHandler;
        PackageInstallOptions? savedOptions = useSavedPackageOptions
            ? _packageInstallOptionsProvider?.Invoke(appId, source)
            : null;
        installLocation ??= savedOptions?.InstallLocation;
        interactive |= savedOptions?.Interactive == true;
        scope ??= savedOptions?.Scope;
        architecture ??= savedOptions?.Architecture;
        ignoreSecurityHash |= savedOptions?.IgnoreSecurityHash == true;
        requiresElevation |= savedOptions?.RequiresElevation == true;
        version ??= savedOptions?.Version;
        customArguments ??= savedOptions?.CustomArguments;
        overrideArguments ??= savedOptions?.OverrideArguments;
        uninstallPrevious |= savedOptions?.UninstallPrevious == true;
        preInstallScript ??= savedOptions?.PreInstallScript;
        postInstallScript ??= savedOptions?.PostInstallScript;
        processesToClose ??= savedOptions?.ProcessesToClose;
        forceCloseProcesses |= savedOptions?.ForceCloseProcesses == true;
        abortInstallOnPreScriptFailure &= savedOptions?.AbortInstallOnPreScriptFailure ?? true;

        var item = queue.Enqueue(appName, OperationKind.Install, iconUrl);
        SetHistoryMetadata(item, appId, source, versionTo: version ?? requestedVersion);
        item.SetStatusMessage("operations.message.preparing");

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            var onLogReceived = new Action<string>(line => ReportProgress(item, line));
            var onProgress = new Action<InstallProgressUpdate>(update => ReportInstallProgress(item, update));
            onLogReceived($"Instalando {appName} ({appId}) pela fonte {source}.");

            var installOptions = (packageOptions ?? savedOptions ?? new PackageInstallOptions()) with
            {
                Interactive = interactive,
                Scope = scope, Architecture = architecture, IgnoreSecurityHash = ignoreSecurityHash,
                InstallLocation = installLocation, RequiresElevation = requiresElevation, Version = version,
                CustomArguments = customArguments, OverrideArguments = overrideArguments,
                PreInstallScript = preInstallScript, PostInstallScript = postInstallScript,
                RunInstallHooks = runInstallHooks, AbortInstallOnPreScriptFailure = abortInstallOnPreScriptFailure,
                ProcessesToClose = processesToClose?.ToArray(), ForceCloseProcesses = forceCloseProcesses,
                UninstallPrevious = uninstallPrevious
            };
            item.Snapshot = new OperationSnapshot(1, item.Manager, NormalizeReplaySource(source, item.Manager), appId, appName, OperationKind.Install,
                null, version ?? requestedVersion, installOptions, installLocation, IconUrl: item.IconUrl);
            if (installHandler is null)
            {
                item.Method = WingetMethod.WingetExe;
                // Envia um progresso inicial para garantir que a cor seja aplicada
                onProgress(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe));
            }

            if (processesToClose is not null)
                await PackageProcessCloser.CloseAsync(processesToClose, forceCloseProcesses, item.CancellationTokenSource.Token).ConfigureAwait(false);

            bool preInstallSucceeded = !runInstallHooks
                || await PackageInstallHookRunner.RunAsync(preInstallScript, item.CancellationTokenSource.Token).ConfigureAwait(false);
            if (!preInstallSucceeded && abortInstallOnPreScriptFailure)
            {
                var hookFailure = PackageInstallHookRunner.Failure("pré-instalação");
                AppendResultLog(item, source, hookFailure);
                item.State = OperationState.Failed;
                item.StatusText = hookFailure.Output;
                return hookFailure;
            }

            var result = installHandler is null
                ? await executor.InstallAppAsync(
                    appId, onLogReceived, item.CancellationTokenSource.Token, installLocation,
                    source: source, requiresElevation: requiresElevation, scope: scope,
                    architecture: architecture, interactive: interactive, ignoreSecurityHash: ignoreSecurityHash,
                    version: version, customArguments: customArguments,
                    overrideArguments: overrideArguments,
                    uninstallPrevious: uninstallPrevious)
                : await installHandler(
                    appId,
                    onLogReceived,
                    item.CancellationTokenSource.Token,
                    installOptions,
                    onProgress,
                    source);

            if (result.Success && runInstallHooks
                && !await PackageInstallHookRunner.RunAsync(postInstallScript, item.CancellationTokenSource.Token).ConfigureAwait(false))
            {
                result = PackageInstallHookRunner.Failure("pós-instalação");
            }

            if (installHandler is null)
                result = MarkInstallUnconfirmedIfNeeded(result);
            string effectiveSource = result.EffectiveSource ?? source;
            AppendResultLog(item, effectiveSource, result);

            if (result.Success)
            {
                await ReconcileInstalledVersionAsync(item, item.CancellationTokenSource.Token).ConfigureAwait(false);
                ReportSuccessfulInstall(appId, effectiveSource);
                item.IsIndeterminate = false;
                item.Progress = 100;
            }
            item.State = result.Success
                ? OperationState.Completed
                : result.IsUnconfirmed
                    ? OperationState.Unconfirmed
                : IsCanceledResult(item, result)
                    ? OperationState.Canceled
                    : OperationState.Failed;

            if (result.Success)
            {
                InstalledPackageMethodHistory.Record(appId, item.Method);
                installedAppsService?.MarkInstalled();
                if (result.RebootRequired) item.SetStatusMessage("operations.message.reboot.install");
            }
            else if (result.InstallHookFailed && result.InstallerStarted)
            {
                // O instalador terminou, mas o pós-hook falhou; atualize o inventário
                // para impedir que a falha do hook provoque uma nova instalação.
                InstalledPackageMethodHistory.Record(appId, item.Method);
                installedAppsService?.MarkInstalled();
                item.StatusText = result.Output;
            }
            else if (result.IsUnconfirmed)
            {
                installedAppsService?.MarkInstalled();
                item.StatusText = result.Output;
            }
            else if (item.State is OperationState.Failed or OperationState.Canceled)
            {
                SetTranslatedFailureStatus(item, result.FailureReason, "instalar", appName);
            }

            return result;
        }
        catch (Exception ex)
        {
            item.AppendLog($"Exceção durante a instalação: {ex.GetType().Name}: {ex.Message}");
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Mesmo padrão de <see cref="RunInstallAsync"/>, só que para atualização de um pacote
    /// já instalado (gerenciador de pacotes update) — usado pela tela Atualizações. OperationKind.Update já
    /// existia no painel de fila (rótulo "Atualizando"), então isso só reaproveita a mesma
    /// exibição sem precisar de nada novo lá.
    /// </summary>
    public static async Task<WingetExecutionResult> RunUpdateAsync(
        OperationsQueueService queue,
        WingetExecutor executor,
        string appId,
        string appName,
        string? iconUrl = null,
        string source = "winget",
        bool elevated = false,
        bool interactive = false,
        bool ignoreSecurityHash = false,
        string? scope = null,
        string? architecture = null,
        bool useSavedPackageOptions = true,
        string? version = null,
        UpgradablePackage? offeredPackage = null,
        PackageInstallOptions? packageOptions = null,
        IEnumerable<string>? processesToClose = null,
        bool forceCloseProcesses = false,
        bool runUpdateHooks = false)
    {
        PackageInstallOptions? savedOptions = useSavedPackageOptions
            ? _packageInstallOptionsProvider?.Invoke(appId, source)
            : null;
        interactive |= packageOptions?.Interactive == true || savedOptions?.Interactive == true;
        ignoreSecurityHash |= packageOptions?.IgnoreSecurityHash == true || savedOptions?.IgnoreSecurityHash == true;
        scope ??= packageOptions?.Scope ?? savedOptions?.Scope;
        architecture ??= packageOptions?.Architecture ?? savedOptions?.Architecture;
        version ??= packageOptions?.Version ?? savedOptions?.Version;
        elevated |= packageOptions?.RequiresElevation == true || savedOptions?.RequiresElevation == true;
        processesToClose ??= packageOptions?.ProcessesToClose ?? savedOptions?.ProcessesToClose;
        forceCloseProcesses |= packageOptions?.ForceCloseProcesses == true || savedOptions?.ForceCloseProcesses == true;
        runUpdateHooks |= packageOptions?.RunUpdateHooks == true;
        var effectiveOptions = (packageOptions ?? savedOptions ?? new PackageInstallOptions()) with
        {
            Interactive = interactive,
            IgnoreSecurityHash = ignoreSecurityHash,
            Scope = scope,
            Architecture = architecture,
            Version = version,
            RequiresElevation = elevated
        };

        var item = queue.Enqueue(appName, OperationKind.Update, iconUrl);
        SetHistoryMetadata(item, appId, source, offeredPackage?.CurrentVersion, offeredPackage?.AvailableVersion ?? version);
        item.Snapshot = new OperationSnapshot(1, item.Manager, NormalizeReplaySource(source, item.Manager), appId, appName, OperationKind.Update,
            offeredPackage?.CurrentVersion, offeredPackage?.AvailableVersion ?? version, effectiveOptions, effectiveOptions.InstallLocation, IconUrl: item.IconUrl);
        item.SetStatusMessage("operations.message.preparing.update");

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            var updateHandler = _updateHandler;
            var onLogReceived = new Action<string>(line => ReportProgress(item, line));
            var onProgress = new Action<InstallProgressUpdate>(update => ReportInstallProgress(item, update));
            onLogReceived($"Atualizando {appName} ({appId}) pela fonte {source}.");

            if (processesToClose is not null)
                await PackageProcessCloser.CloseAsync(processesToClose, forceCloseProcesses, item.CancellationTokenSource.Token).ConfigureAwait(false);

            bool preScriptSucceeded = !runUpdateHooks
                || await PackageInstallHookRunner.RunAsync(effectiveOptions.PreUpdateScript, item.CancellationTokenSource.Token).ConfigureAwait(false);

            // Se não há handler configurado, usa cliente de pacotes diretamente
            // Os handlers de catálogo/API representam a atualização padrão. As variantes
            // precisam chegar aos argumentos da CLI para honrar elevação, escopo, arquitetura,
            // interação e hash.
            bool useWingetCli = updateHandler is null;
            if (useWingetCli)
            {
                item.Method = WingetMethod.WingetExe;
                // Envia um progresso inicial para garantir que a cor seja aplicada
                onProgress(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe));
            }

            WingetExecutionResult result = !preScriptSucceeded && effectiveOptions.AbortUpdateOnPreScriptFailure
                ? PackageInstallHookRunner.Failure("pré-atualização")
                : useWingetCli
                ? await executor.UpdateAppAsync(
                    appId,
                    onLogReceived,
                    cancellationToken: item.CancellationTokenSource.Token,
                    source: source,
                    scope: scope,
                    architecture: architecture,
                    installLocation: effectiveOptions.InstallLocation,
                    requiresElevation: elevated,
                    interactive: interactive,
                    ignoreSecurityHash: ignoreSecurityHash,
                    version: version ?? offeredPackage?.AvailableVersion,
                    customArguments: effectiveOptions.UpdateArguments ?? effectiveOptions.CustomArguments)
                : await updateHandler!(
                    appId,
                    onLogReceived,
                    item.CancellationTokenSource.Token,
                    source,
                    onProgress,
                    offeredPackage,
                    effectiveOptions);

            if (result.Success && runUpdateHooks
                && !await PackageInstallHookRunner.RunAsync(effectiveOptions.PostUpdateScript, item.CancellationTokenSource.Token).ConfigureAwait(false))
                result = PackageInstallHookRunner.Failure("pós-atualização");

            if (useWingetCli && offeredPackage is not null && _updateVerifier is not null)
                result = await _updateVerifier(offeredPackage, result, item.CancellationTokenSource.Token)
                    .ConfigureAwait(false);

            AppendResultLog(item, source, result);

            if (result.Success)
            {
                await ReconcileInstalledVersionAsync(item, item.CancellationTokenSource.Token).ConfigureAwait(false);
                item.IsIndeterminate = false;
                item.Progress = 100;
            }
            item.State = result.Success
                ? OperationState.Completed
                : result.IsUnconfirmed
                    ? OperationState.Unconfirmed
                : IsCanceledResult(item, result)
                    ? OperationState.Canceled
                    : OperationState.Failed;

            if (result.IsUnconfirmed)
            {
                item.StatusText = result.Output;
            }
            else if (!result.Success && item.State is OperationState.Failed or OperationState.Canceled)
            {
                if (result.InstallHookFailed || result.Output.StartsWith("A atualização foi executada, mas não foi confirmada.", StringComparison.Ordinal))
                    item.StatusText = result.Output;
                else SetTranslatedFailureStatus(item, result.FailureReason, "atualizar", appName);
            }
            else if (result.Success)
            {
                InstalledPackageMethodHistory.Record(appId, item.Method);
                if (result.RebootRequired) item.SetStatusMessage("operations.message.reboot.update");
            }

            return result;
        }
        catch (Exception ex)
        {
            item.AppendLog($"Exceção durante a atualização: {ex.GetType().Name}: {ex.Message}");
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Mesmo padrão de <see cref="RunInstallAsync"/>, só que para remoção: enfileira no
    /// painel flutuante, executa via WingetExecutor.UninstallAppAsync (gerenciador de pacotes uninstall
    /// silencioso) e, em caso de sucesso, marca o app como não-instalado no cache
    /// compartilhado (InstalledAppsService) para o botão da UI voltar a "Instalar".
    /// </summary>
    public static async Task<WingetExecutionResult> RunUninstallWithHandlerAsync(
        OperationsQueueService queue,
        InstalledAppDetail app,
        Func<Action<string>, CancellationToken, Task<WingetExecutionResult>> handler,
        InstalledAppsService? installedAppsService = null,
        PackageInstallOptions? packageOptions = null,
        string? iconUrl = null)
    {
        var item = queue.Enqueue(app.DisplayName, OperationKind.Uninstall, iconUrl);
        item.PackageId = app.Id;
        item.ManagerOverride = "winget";
        item.VersionFrom = app.DisplayVersion;

        item.SetStatusMessage("operations.message.preparing.uninstall");

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            packageOptions ??= new PackageInstallOptions();
            item.Snapshot = new OperationSnapshot(1, "winget", null, app.Id, app.DisplayName, OperationKind.Uninstall,
                app.DisplayVersion, null, packageOptions, packageOptions.InstallLocation, IconUrl: item.IconUrl);
            if (packageOptions.ProcessesToClose is not null)
                await PackageProcessCloser.CloseAsync(packageOptions.ProcessesToClose, packageOptions.ForceCloseProcesses, item.CancellationTokenSource.Token).ConfigureAwait(false);
            bool preScriptSucceeded = !packageOptions.RunUninstallHooks
                || await PackageInstallHookRunner.RunAsync(packageOptions.PreUninstallScript, item.CancellationTokenSource.Token).ConfigureAwait(false);
            WingetExecutionResult result = !preScriptSucceeded && packageOptions.AbortUninstallOnPreScriptFailure
                ? PackageInstallHookRunner.Failure("pré-desinstalação")
                : await handler(line => ReportProgress(item, line), item.CancellationTokenSource.Token).ConfigureAwait(false);
            if (result.Success && packageOptions.RunUninstallHooks
                && !await PackageInstallHookRunner.RunAsync(packageOptions.PostUninstallScript, item.CancellationTokenSource.Token).ConfigureAwait(false))
                result = PackageInstallHookRunner.Failure("pós-desinstalação");

            AppendResultLog(item, "automática", result);
            item.IsIndeterminate = false;
            bool operationCompleted = result.Success || result.InstallHookFailed && result.InstallerStarted;
            if (operationCompleted)
            {
                item.Progress = 100;
                item.State = OperationState.Completed;
                installedAppsService?.MarkUninstalled();
                InstalledPackageMethodHistory.Forget(app.Id);
                if (result.InstallHookFailed) item.StatusText = result.Output;
            }
            else
            {
                item.State = item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;
                if (result.InstallHookFailed) item.StatusText = result.Output;
                else SetTranslatedFailureStatus(item, result.FailureReason, "desinstalar", app.DisplayName);
            }

            return result;
        }
        catch (Exception ex)
        {
            item.AppendLog($"Exceção durante a remoção: {ex.GetType().Name}: {ex.Message}");
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Executa a desinstalação agressiva por uma operação separada do WinGet.
    /// Enfileira a operação no painel, força a injeção silenciosa e executa a limpeza
    /// agressiva (pastas/registro) via UninstallerEngineService.
    /// </summary>
    public static async Task<bool> RunAggressiveUninstallAsync(
        OperationsQueueService queue,
        UninstallerEngineService uninstallerEngine,
        InstalledAppDetail app,
        string? iconUrl = null,
        InstalledAppsService? installedAppsService = null)
    {
        var item = queue.Enqueue(app.DisplayName, OperationKind.Uninstall, iconUrl);

        item.SetStatusMessage("operations.message.aggressiveUninstall");
        item.IsIndeterminate = true;

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            // O UninstallerEngineService usa o Token para timeout e cancelamento
            bool success = await uninstallerEngine.UninstallAggressivelyAsync(app, item.CancellationTokenSource.Token);

            item.IsIndeterminate = false;
            item.Progress = 100;

            item.State = success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            if (success)
            {
                item.SetStatusMessage("operations.message.aggressive.completedUninstall");
                installedAppsService?.MarkUninstalled();
            }
            else if (item.State == OperationState.Failed)
            {
                item.SetStatusMessage("operations.message.aggressive.failedUninstall");
            }

            return success;
        }
        catch
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;

            item.SetStatusMessage("operations.message.uninstallFailedDetailsGeneric");
            item.IsIndeterminate = false;

            throw;
        }
    }

    /// <summary>
    /// Executa a desinstalação forçada (Force Uninstall) quando o desinstalador padrão falha.
    /// Usa detecção de processos, janelas e atalhos para identificar e remover o aplicativo
    /// </summary>
    public static async Task<bool> RunForceUninstallAsync(
        OperationsQueueService queue,
        UninstallerEngineService uninstallerEngine,
        InstalledAppDetail app,
        string? iconUrl = null,
        InstalledAppsService? installedAppsService = null)
    {
        var item = queue.Enqueue(app.DisplayName, OperationKind.Uninstall, iconUrl);

        item.SetStatusMessage("operations.message.forceUninstall");
        item.IsIndeterminate = true;

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            // O UninstallerEngineService usa o ForceUninstallAsync
            bool success = await uninstallerEngine.ForceUninstallAsync(app, item.CancellationTokenSource.Token);

            item.IsIndeterminate = false;
            item.Progress = 100;

            item.State = success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            if (success)
            {
                item.SetStatusMessage("operations.message.force.completedUninstall");
                installedAppsService?.MarkUninstalled();
            }
            else if (item.State == OperationState.Failed)
            {
                item.SetStatusMessage("operations.message.force.failedUninstall");
            }

            return success;
        }
        catch
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;

            item.SetStatusMessage("operations.message.uninstallFailedDetailsGeneric");
            item.IsIndeterminate = false;

            throw;
        }
    }

    /// <summary>
    /// Mesmo padrão de enfileiramento do RunInstallAsync, só que para o pipeline do
    /// Office (ODT + configuration.xml) em vez do gerenciador de pacotes. O Viper fornece progresso por bytes;
    /// o setup.exe do ODT não imprime progresso incremental, então o item fica
    /// indeterminado até terminar.
    /// </summary>
    public static async Task<bool> RunOfficeInstallAsync(
        OperationsQueueService queue,
        OfficeDeploymentToolService officeService,
        OfficeInstallRequest request,
        CancellationToken cancellationToken = default,
        bool applyChanges = false)
    {
        var item = queue.Enqueue(applyChanges ? $"Aplicar alterações em {request.Plan.DisplayName}" : request.Plan.DisplayName, OperationKind.Install);
        item.PackageId = "Office";
        item.ManagerOverride = "office";
        item.Snapshot = new OperationSnapshot(1, "office", "office", "Office", item.AppName,
            OperationKind.Install, null, null, new PackageInstallOptions(), OfficeInstall: request);
        using CancellationTokenRegistration cancellation = cancellationToken.Register(item.Cancel);
        item.SetStatusMessage("operations.message.preparing");

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            var officeResult = await officeService.RunConfigureDetailedAsync(
                request,
                onStatus: line => { item.StatusText = line; item.AppendLog(line); },
                cancellationToken: item.CancellationTokenSource.Token,
                onProgress: update => ReportInstallProgress(item, update),
                applyChanges: applyChanges);

            bool success = officeResult.Success;
            item.RebootRequired = officeResult.RebootRequired;
            item.Progress = 100;
            if (officeResult.AlreadyInstalled)
                item.SetStatusMessage("operations.message.office.alreadyInstalled");
            if (officeResult.ElevationCanceled) item.Cancel();
            item.State = success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            return success;
        }
        catch (OperationCanceledException exception) when (item.CancellationTokenSource.IsCancellationRequested || exception is OfficeElevationCanceledException)
        {
            item.Cancel();
            item.State = OperationState.Canceled;
            throw;
        }
        catch (Exception)
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>
    /// Mesmo padrão de RunOfficeInstallAsync, para o pipeline de remoção (ODT +
    /// configuration.xml com &lt;Remove&gt;/RemoveAll, mais limpeza opcional da edição
    /// catálogo visual).
    /// </summary>
    public static async Task<bool> RunOfficeRemoveAsync(
        OperationsQueueService queue,
        OfficeDeploymentToolService officeService,
        OfficeRemoveRequest request,
        string label)
    {
        var item = queue.Enqueue(label, OperationKind.Uninstall);
        item.PackageId = "Office";
        item.ManagerOverride = "office";
        item.Snapshot = new OperationSnapshot(1, "office", "office", "Office", label,
            OperationKind.Uninstall, null, null, new PackageInstallOptions(), OfficeRemove: request);
        item.SetStatusMessage("operations.message.preparing");

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            bool success = await officeService.RunRemoveAsync(
                request,
                onStatus: line => item.StatusText = line,
                cancellationToken: item.CancellationTokenSource.Token);

            item.Progress = 100;
            item.State = success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            return success;
        }
        catch (Exception)
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    /// <summary>Enfileira a aplicação isolada da política &lt;Updates Enabled="TRUE|FALSE"/&gt;, sem reinstalar nada.</summary>
    public static async Task<bool> RunOfficeSetAutoUpdateAsync(
        OperationsQueueService queue,
        OfficeDeploymentToolService officeService,
        bool enabled)
    {
        var item = queue.Enqueue(enabled ? "Ativar atualizações automáticas do Office" : "Desativar atualizações automáticas do Office", OperationKind.Update);
        item.PackageId = "Office";
        item.ManagerOverride = "office";
        item.Snapshot = new OperationSnapshot(1, "office", "office", "Office", item.AppName,
            OperationKind.Update, null, null, new PackageInstallOptions(), OfficeAutoUpdatesEnabled: enabled);
        item.SetStatusMessage("operations.message.preparing");

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            bool success = await officeService.RunSetAutoUpdateAsync(
                enabled,
                onStatus: line => item.StatusText = line,
                cancellationToken: item.CancellationTokenSource.Token);

            item.Progress = 100;
            item.State = success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            return success;
        }
        catch (Exception)
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            throw;
        }
    }

    private static void ReportProgress(OperationItem item, string logLine)
    {
        if (string.IsNullOrWhiteSpace(logLine))
        {
            return;
        }

        var trimmed = logLine.Trim();
        item.DetailText = trimmed;
        if (trimmed.Contains("fallback para winget.exe", StringComparison.OrdinalIgnoreCase))
            item.IsFallback = true;

        if (trimmed.Contains("[WinProvisionAPI]", StringComparison.Ordinal) ||
            trimmed.Contains("API própria", StringComparison.Ordinal))
        {
            item.Method = WingetMethod.OwnApi;
        }
        else if (trimmed.Contains("API COM", StringComparison.Ordinal))
        {
            item.Method = WingetMethod.ComApi;
        }
        else if (trimmed.Contains("winget.exe", StringComparison.OrdinalIgnoreCase))
        {
            item.Method = WingetMethod.WingetExe;
        }

        var isLayerMessage =
            trimmed.Contains("API própria", StringComparison.Ordinal) ||
            trimmed.Contains("API COM", StringComparison.Ordinal) ||
            trimmed.Contains("[WinProvisionAPI]", StringComparison.Ordinal) ||
            trimmed.Contains("winget.exe", StringComparison.OrdinalIgnoreCase);

        string activity = item.Kind switch
        {
            OperationKind.Install => "Instalando",
            OperationKind.Update => "Atualizando",
            OperationKind.Uninstall => "Removendo",
            _ => "Processando"
        };
        if (item.Method == WingetMethod.WingetExe)
        {
            // A saída capturada do cliente de pacotes não expõe uma medição confiável dos bytes.
            // Mantém a barra indeterminada em vez de interpretar percentuais arbitrários.
            item.StatusText = $"{activity}...";
        }
        else if (item.Method == WingetMethod.Unknown && item.IsIndeterminate)
        {
            item.StatusText = isLayerMessage ? $"{activity}..." : trimmed;
        }

        // O ODT fornece progresso real em suas mensagens; não inferir progresso dos logs
        // do gerenciador de pacotes, COM ou da API própria (esses métodos publicam progresso tipado).
        var match = item.Method == WingetMethod.Unknown ? PercentRegex().Match(logLine) : Match.Empty;
        if (match.Success && int.TryParse(match.Groups[1].Value, out int percent))
        {
            item.IsIndeterminate = false;
            item.Progress = Math.Clamp(percent, 0, 100);
        }
    }

    private static void AppendResultLog(OperationItem item, string source, WingetExecutionResult result)
    {
        item.RebootRequired = result.RebootRequired;
        item.ExitCode = result.ExitCode;
        uint exitCode = unchecked((uint)result.ExitCode);
        string exitText = result.ExitCode < 0
            ? $"{result.ExitCode} (0x{exitCode:X8})"
            : result.ExitCode.ToString();
        item.AppendLog($"Resultado final: {(result.Success ? "sucesso" : "falha")}; fonte={source}; método={item.Method}; código={exitText}; motivo={result.FailureReason}.");

        if (result.RebootRequired)
            item.AppendLog(InstallerExitCodes.Describe(result.ExitCode));

        string output = result.Output?.Trim() ?? string.Empty;
        if (output.Length > 0 && !item.GetFullLog().Contains(output, StringComparison.Ordinal))
            item.AppendLog($"Detalhes retornados pelo instalador: {output}");
    }

    private static void SetHistoryMetadata(OperationItem item, string packageId, string? source,
        string? versionFrom = null, string? versionTo = null)
    {
        item.PackageId = packageId;
        item.Manager = source?.Contains("chocolatey", StringComparison.OrdinalIgnoreCase) == true ? "chocolatey" : "winget";
        item.SourceName = item.Manager == "chocolatey"
            ? "community"
            : source?.Contains("msstore", StringComparison.OrdinalIgnoreCase) == true ? "Microsoft Store"
            : source?.Contains("winget", StringComparison.OrdinalIgnoreCase) == true ? "winget" : null;
        item.VersionFrom = versionFrom;
        item.VersionTo = versionTo;
    }

    private static string NormalizeReplaySource(string? source, string manager) =>
        manager.Equals("chocolatey", StringComparison.OrdinalIgnoreCase) ? "chocolatey"
        : source?.Contains("msstore", StringComparison.OrdinalIgnoreCase) == true ? "msstore"
        : string.IsNullOrWhiteSpace(source) ? "winget" : source;

    private static WingetExecutionResult MarkInstallUnconfirmedIfNeeded(WingetExecutionResult result)
    {
        if (!result.Success || result.InstallationVerified || result.IsUnconfirmed)
            return result;

        string originalOutput = result.Output;
        result.Success = false;
        result.InstallerStarted = true;
        result.VerificationStatus = OperationVerificationStatus.Unconfirmed;
        result.FailureReason = WingetFailureReason.Unknown;
        result.Output = string.IsNullOrWhiteSpace(originalOutput)
            ? "Resultado não confirmado. O executor direto não verificou a identidade instalada."
            : $"Resultado não confirmado. O executor direto não verificou a identidade instalada.{Environment.NewLine}{originalOutput}";
        return result;
    }

    private static bool IsCanceledResult(OperationItem item, WingetExecutionResult result) =>
        item.CancellationTokenSource.IsCancellationRequested
        || result.FailureReason is WingetFailureReason.ElevationCanceled or WingetFailureReason.OperationCanceled;

    private static void SetTranslatedFailureStatus(OperationItem item, WingetFailureReason reason, string verb, string appName)
    {
        item.SetFailureReason(reason);
        item.StatusText = WingetErrorTranslator.ToMessage(reason, verb, appName);
    }

    internal static void ReportInstallProgress(OperationItem item, InstallProgressUpdate update)
    {
        // Atualiza o método se fornecido no update
        if (update.Method != WingetMethod.Unknown)
        {
            item.Method = update.Method;
        }
        if (update.IsFallback) item.IsFallback = true;

        switch (update.Phase)
        {
            case InstallProgressPhase.Downloading when update.Percent is double percent:
                item.IsIndeterminate = false;
                item.Progress = Math.Clamp(percent, 0, 100);
                item.SetStatusMessage("operations.message.downloadProgress", item.Progress.ToString("0.00", System.Globalization.CultureInfo.CurrentCulture));
                break;
            case InstallProgressPhase.Downloading:
                item.IsIndeterminate = true;
                item.SetStatusMessage("operations.message.downloading");
                break;
            case InstallProgressPhase.Preparing:
                item.IsIndeterminate = true;
                item.SetStatusMessage(update.Method == WingetMethod.WingetExe
                    ? "operations.message.awaitingWinget"
                    : "operations.message.preparingInstallation");
                break;
            case InstallProgressPhase.Installing when update.Percent is double installPercent && installPercent > 0:
                item.IsIndeterminate = false;
                item.Progress = Math.Clamp(installPercent, 0, 100);
                item.SetStatusMessage("operations.message.installProgress", item.Progress.ToString("0", System.Globalization.CultureInfo.CurrentCulture));
                break;
            case InstallProgressPhase.Installing:
                item.IsIndeterminate = true;
                item.SetStatusMessage("operations.message.installing");
                break;
        }
        item.LiveLine = item.StatusText;
    }

    [GeneratedRegex(@"(\d{1,3})\s?%")]
    private static partial Regex PercentRegex();
}
