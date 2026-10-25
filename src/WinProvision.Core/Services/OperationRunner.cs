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
/// Enfileira e executa uma operação de instalação/remoção via winget ou motor próprio,
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
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[OperationRunner] Métrica de instalação indisponível: {ex.Message}"); }
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
    /// Configura o handler de atualização (API COM/CLI do WinGet, com fallback interno para a
    /// API própria da WinProvision Store — ver WinGetService.UpdateAsync). Sem handler
    /// configurado, RunUpdateAsync usa WingetExecutor.UpdateAppAsync diretamente (winget.exe
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
    /// Instala usando o handler configurado (API COM do WinGet, com fallback interno para a
    /// CLI). Sem handler configurado, usa o <see cref="WingetExecutor"/> diretamente. Usado
    /// também pelo modo /auto, para que ele não rode sempre o winget.exe.
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
        Task<WingetExecutionResult> operation = _installHandler is null
            ? executor.InstallAppAsync(appId, onLogReceived, cancellationToken, installLocation, source)
            : _installHandler(appId, onLogReceived, cancellationToken, options, onProgress, source);
        WingetExecutionResult result = await operation.ConfigureAwait(false);
        if (result.Success)
            ReportSuccessfulInstall(appId, source);
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
        string? preInstallScript = null,
        string? postInstallScript = null,
        bool runInstallHooks = false)
    {
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
        preInstallScript ??= savedOptions?.PreInstallScript;
        postInstallScript ??= savedOptions?.PostInstallScript;

        var item = queue.Enqueue(appName, OperationKind.Install, iconUrl);
        item.StatusText = "Preparando...";

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            var onLogReceived = new Action<string>(line => ReportProgress(item, line));
            var onProgress = new Action<InstallProgressUpdate>(update => ReportInstallProgress(item, update));
            onLogReceived($"Instalando {appName} ({appId}) pela fonte {source}.");

            var installOptions = new PackageInstallOptions(
                Interactive: interactive,
                Scope: scope,
                Architecture: architecture,
                IgnoreSecurityHash: ignoreSecurityHash,
                InstallLocation: installLocation,
                RequiresElevation: requiresElevation,
                Version: version,
                CustomArguments: customArguments,
                PreInstallScript: preInstallScript,
                PostInstallScript: postInstallScript,
                RunInstallHooks: runInstallHooks);
            if (_installHandler is null)
            {
                item.Method = WingetMethod.WingetExe;
                // Envia um progresso inicial para garantir que a cor seja aplicada
                onProgress(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe));
            }

            if (runInstallHooks && !await PackageInstallHookRunner.RunAsync(preInstallScript, item.CancellationTokenSource.Token).ConfigureAwait(false))
            {
                var hookFailure = PackageInstallHookRunner.Failure("pré-instalação");
                AppendResultLog(item, source, hookFailure);
                item.State = OperationState.Failed;
                item.StatusText = hookFailure.Output;
                return hookFailure;
            }

            var result = _installHandler is null
                ? await executor.InstallAppAsync(
                    appId, onLogReceived, item.CancellationTokenSource.Token, installLocation,
                    source: source, requiresElevation: requiresElevation, scope: scope,
                    architecture: architecture, interactive: interactive, ignoreSecurityHash: ignoreSecurityHash,
                    version: version, customArguments: customArguments)
                : await _installHandler(
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

            AppendResultLog(item, source, result);

            if (result.Success)
            {
                ReportSuccessfulInstall(appId, source);
                item.IsIndeterminate = false;
                item.Progress = 100;
            }
            item.State = result.Success
                ? OperationState.Completed
                : IsCanceledResult(item, result)
                    ? OperationState.Canceled
                    : OperationState.Failed;

            if (result.Success)
            {
                InstalledPackageMethodHistory.Record(appId, item.Method);
                installedAppsService?.MarkInstalled();
                if (result.RebootRequired) item.StatusText = "Instalação concluída; reinicie o Windows.";
            }
            else if (result.InstallHookFailed && result.InstallerStarted)
            {
                // O instalador terminou, mas o pós-hook falhou; atualize o inventário
                // para impedir que a falha do hook provoque uma nova instalação.
                InstalledPackageMethodHistory.Record(appId, item.Method);
                installedAppsService?.MarkInstalled();
                item.StatusText = result.Output;
            }
            else if (item.State is OperationState.Failed or OperationState.Canceled)
            {
                item.StatusText = WingetErrorTranslator.ToMessage(result.FailureReason, "instalar", appName);
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
    /// já instalado (winget update) — usado pela tela Atualizações. OperationKind.Update já
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
        PackageInstallOptions? packageOptions = null)
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
        item.StatusText = "Preparando atualização...";

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            var updateHandler = _updateHandler;
            var onLogReceived = new Action<string>(line => ReportProgress(item, line));
            var onProgress = new Action<InstallProgressUpdate>(update => ReportInstallProgress(item, update));
            onLogReceived($"Atualizando {appName} ({appId}) pela fonte {source}.");

            // Se não há handler configurado, usa winget.exe diretamente
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

            var result = useWingetCli
                ? await executor.UpdateAppAsync(
                    appId,
                    onLogReceived,
                    cancellationToken: item.CancellationTokenSource.Token,
                    source: source,
                    scope: scope,
                    architecture: architecture,
                    requiresElevation: elevated,
                    interactive: interactive,
                    ignoreSecurityHash: ignoreSecurityHash,
                    version: version ?? offeredPackage?.AvailableVersion)
                : await updateHandler!(
                    appId,
                    onLogReceived,
                    item.CancellationTokenSource.Token,
                    source,
                    onProgress,
                    offeredPackage,
                    effectiveOptions);

            if (useWingetCli && offeredPackage is not null && _updateVerifier is not null)
                result = await _updateVerifier(offeredPackage, result, item.CancellationTokenSource.Token)
                    .ConfigureAwait(false);

            AppendResultLog(item, source, result);

            if (result.Success)
            {
                item.IsIndeterminate = false;
                item.Progress = 100;
            }
            item.State = result.Success
                ? OperationState.Completed
                : IsCanceledResult(item, result)
                    ? OperationState.Canceled
                    : OperationState.Failed;

            if (!result.Success && item.State is OperationState.Failed or OperationState.Canceled)
            {
                item.StatusText = result.Output.StartsWith("A atualização foi executada, mas não foi confirmada.", StringComparison.Ordinal)
                    ? result.Output
                    : WingetErrorTranslator.ToMessage(result.FailureReason, "atualizar", appName);
            }
            else if (result.Success)
            {
                InstalledPackageMethodHistory.Record(appId, item.Method);
                if (result.RebootRequired) item.StatusText = "Atualização concluída; reinicie o Windows.";
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
    /// painel flutuante, executa via WingetExecutor.UninstallAppAsync (winget uninstall
    /// silencioso) e, em caso de sucesso, marca o app como não-instalado no cache
    /// compartilhado (InstalledAppsService) para o botão da UI voltar a "Instalar".
    /// </summary>
    public static async Task<WingetExecutionResult> RunUninstallAsync(
        OperationsQueueService queue,
        WingetExecutor executor,
        string appId,
        string appName,
        string? iconUrl = null,
        InstalledAppsService? installedAppsService = null,
        string? source = null,
        string? installedVersion = null,
        bool interactive = false,
        bool requiresElevation = false)
    {
        var item = queue.Enqueue(appName, OperationKind.Uninstall, iconUrl);
        item.StatusText = "Preparando remoção...";

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            var result = await executor.UninstallAppAsync(
                appId,
                onLogReceived: line => ReportProgress(item, line),
                cancellationToken: item.CancellationTokenSource.Token,
                source: source,
                installedVersion: installedVersion,
                interactive: interactive,
                requiresElevation: requiresElevation);

            AppendResultLog(item, source ?? "automática", result);

            if (result.Success)
            {
                item.IsIndeterminate = false;
                item.Progress = 100;
            }
            item.State = result.Success
                ? OperationState.Completed
                : item.CancellationTokenSource.IsCancellationRequested
                    ? OperationState.Canceled
                    : OperationState.Failed;

            if (result.Success)
            {
                InstalledPackageMethodHistory.Forget(appId);
                installedAppsService?.MarkUninstalled();
            }
            else if (item.State == OperationState.Failed)
            {
                item.StatusText = WingetErrorTranslator.ToMessage(result.FailureReason, "desinstalar", appName);
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
    /// Executa desinstalação com fallback automático: tenta desinstalação padrão primeiro,
    /// e se falhar, tenta desinstalação agressiva. Cria apenas UM item na fila de operações.
    /// </summary>
    public static async Task<bool> RunUninstallWithFallbackAsync(
        OperationsQueueService queue,
        WingetExecutor executor,
        UninstallerEngineService uninstallerEngine,
        string appId,
        string appName,
        string iconUrl,
        string uninstallString,
        string quietUninstallString,
        string installLocation,
        InstalledAppsService? installedAppsService = null,
        string? source = null,
        string? installedVersion = null,
        WinProvisionApiService? apiService = null,
        bool requiresElevation = false)
    {
        var item = queue.Enqueue(appName, OperationKind.Uninstall, iconUrl);
        item.StatusText = "Preparando remoção...";

        var appDetail = new InstalledAppDetail
        {
            Id = appId,
            DisplayName = appName,
            DisplayVersion = installedVersion ?? string.Empty,
            UninstallString = uninstallString,
            QuietUninstallString = quietUninstallString,
            InstallLocation = installLocation
        };

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            if (SteamUninstallService.IsGameUninstall(uninstallString))
            {
                item.StatusText = "Aguardando a remoção na Steam...";
                bool removed = await SteamUninstallService.UninstallAsync(uninstallString, item.CancellationTokenSource.Token);
                item.IsIndeterminate = false;
                item.Progress = removed ? 100 : 0;
                item.State = removed ? OperationState.Completed : OperationState.Failed;
                item.StatusText = removed ? "Remoção concluída." : "A Steam não confirmou a remoção. Verifique o jogo na biblioteca.";
                item.AppendLog(item.StatusText);
                if (removed) installedAppsService?.MarkUninstalled();
                return removed;
            }
            if (!requiresElevation && apiService is not null && OperatingSystem.IsWindows())
            {
                bool? apiUninstall = await apiService.UninstallAsync(
                    appDetail,
                    uninstallerEngine,
                    onStatus: status =>
                    {
                        item.StatusText = status;
                        item.IsIndeterminate = true;
                    },
                    ct: item.CancellationTokenSource.Token);

                if (apiUninstall.HasValue)
                {
                    item.IsIndeterminate = false;
                    item.Progress = apiUninstall.Value ? 100 : item.Progress;
                    item.State = apiUninstall.Value
                        ? OperationState.Completed
                        : item.CancellationTokenSource.IsCancellationRequested
                            ? OperationState.Canceled
                            : OperationState.Failed;
                    item.StatusText = apiUninstall.Value
                        ? "Remoção concluída."
                        : item.State == OperationState.Canceled
                            ? "Remoção cancelada."
                            : "Não foi possível remover o aplicativo pela API da WinProvision Store.";
                    if (apiUninstall.Value)
                        installedAppsService?.MarkUninstalled();
                    return apiUninstall.Value;
                }
            }

            // WinGet --silent não repassa necessariamente /SUPPRESSMSGBOXES ao
            // desinstalador Inno. Use o comando registrado com os switches Inno
            // antes de entregar a remoção ao WinGet, para suprimir perguntas
            // personalizadas e manter a resposta padrão do fabricante.
            if (UninstallerEngineService.HasInnoSetupUninstaller(appDetail))
            {
                item.StatusText = "Desinstalando silenciosamente...";
                bool removed = await uninstallerEngine.RunSilentUninstallAsync(appDetail, item.CancellationTokenSource.Token);
                item.IsIndeterminate = false;
                item.Progress = removed ? 100 : 0;
                item.State = removed ? OperationState.Completed : OperationState.Failed;
                item.StatusText = removed ? "Remoção concluída." : "Não foi possível remover o aplicativo silenciosamente.";
                if (removed) installedAppsService?.MarkUninstalled();
                return removed;
            }

            // Tenta desinstalação padrão primeiro
            var result = await executor.UninstallAppAsync(
                appId,
                onLogReceived: line => ReportProgress(item, line),
                cancellationToken: item.CancellationTokenSource.Token,
                source: source,
                installedVersion: installedVersion,
                requiresElevation: requiresElevation);

            if (result.Success)
            {
                // Mantém a remoção pelo gerenciador como fonte de verdade e só então
                // remove resíduos locais do pacote, como na limpeza agressiva existente.
                item.StatusText = "Limpando resíduos...";
                item.IsIndeterminate = true;
                await uninstallerEngine.PerformAggressiveCleanupAsync(appDetail, item.CancellationTokenSource.Token);
                item.IsIndeterminate = false;
                item.Progress = 100;
                item.State = OperationState.Completed;
                item.StatusText = result.RebootRequired ? "Remoção concluída; reinicie o Windows." : "Remoção concluída.";
                installedAppsService?.MarkUninstalled();
                return true;
            }

            // Recusa de UAC e conflito de escopo são decisões de segurança/contexto,
            // não falhas do desinstalador. Não iniciar o fallback agressivo evita um
            // segundo prompt de elevação após o usuário já ter recusado o primeiro.
            if (result.FailureReason is WingetFailureReason.ElevationCanceled
                or WingetFailureReason.ElevationProhibited
                or WingetFailureReason.UserScopeElevationConflict)
            {
                item.State = result.FailureReason == WingetFailureReason.ElevationCanceled
                    ? OperationState.Canceled
                    : OperationState.Failed;
                item.StatusText = WingetErrorTranslator.ToMessage(result.FailureReason, "desinstalar", appName);
                item.IsIndeterminate = false;
                return false;
            }

            // Fallback para desinstalação agressiva
            item.StatusText = "Tentando método agressivo...";
            item.IsIndeterminate = true;

            bool aggressiveSuccess = await uninstallerEngine.UninstallAggressivelyAsync(appDetail, item.CancellationTokenSource.Token);

            item.IsIndeterminate = false;
            item.Progress = 100;

            if (aggressiveSuccess)
            {
                item.State = OperationState.Completed;
                item.StatusText = "Remoção concluída.";
                installedAppsService?.MarkUninstalled();
                return true;
            }
            else
            {
                item.State = OperationState.Failed;
                item.StatusText = WingetErrorTranslator.ToMessage(result.FailureReason, "desinstalar", appName);
                return false;
            }
        }
        catch
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;
            item.StatusText = item.State == OperationState.Canceled
                ? "Acompanhamento cancelado. Confira a remoção no desinstalador."
                : "Não foi possível desinstalar. Confira os detalhes da operação.";
            item.IsIndeterminate = false;
            throw;
        }
    }

    /// <summary>
    /// Mesmo padrão de <see cref="RunUninstallAsync"/>, mas utiliza o novo motor agressivo.
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

        item.StatusText = "Forçando remoção agressiva...";
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
                item.StatusText = "Remoção agressiva concluída.";
                installedAppsService?.MarkUninstalled();
            }
            else if (item.State == OperationState.Failed)
            {
                item.StatusText = "Falha ao forçar a remoção ou permissão negada.";
            }

            return success;
        }
        catch
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;

            item.StatusText = "Não foi possível remover o aplicativo. Confira os detalhes da operação.";
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

        item.StatusText = "Executando Force Uninstall...";
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
                item.StatusText = "Force Uninstall concluído.";
                installedAppsService?.MarkUninstalled();
            }
            else if (item.State == OperationState.Failed)
            {
                item.StatusText = "Falha no Force Uninstall.";
            }

            return success;
        }
        catch
        {
            item.State = item.CancellationTokenSource.IsCancellationRequested
                ? OperationState.Canceled
                : OperationState.Failed;

            item.StatusText = "Não foi possível remover o aplicativo. Confira os detalhes da operação.";
            item.IsIndeterminate = false;

            throw;
        }
    }

    /// <summary>
    /// Mesmo padrão de enfileiramento do RunInstallAsync, só que para o pipeline do
    /// Office (ODT + configuration.xml) em vez do winget. O Viper fornece progresso por bytes;
    /// o setup.exe do ODT não imprime progresso incremental, então o item fica
    /// indeterminado até terminar.
    /// </summary>
    public static async Task<bool> RunOfficeInstallAsync(
        OperationsQueueService queue,
        OfficeDeploymentToolService officeService,
        OfficeInstallRequest request,
        CancellationToken cancellationToken = default)
    {
        var item = queue.Enqueue(request.Plan.DisplayName, OperationKind.Install);
        using CancellationTokenRegistration cancellation = cancellationToken.Register(item.Cancel);
        item.StatusText = "Preparando...";

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            var officeResult = await officeService.RunConfigureDetailedAsync(
                request,
                onStatus: line => { item.StatusText = line; item.AppendLog(line); },
                cancellationToken: item.CancellationTokenSource.Token,
                onProgress: update => ReportInstallProgress(item, update));

            bool success = officeResult.Success;
            item.RebootRequired = officeResult.RebootRequired;
            item.Progress = 100;
            if (officeResult.AlreadyInstalled)
                item.StatusText = "Os produtos do Office selecionados já estão instalados.";
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
    /// Microsoft Store).
    /// </summary>
    public static async Task<bool> RunOfficeRemoveAsync(
        OperationsQueueService queue,
        OfficeDeploymentToolService officeService,
        OfficeRemoveRequest request,
        string label)
    {
        var item = queue.Enqueue(label, OperationKind.Uninstall);
        item.StatusText = "Preparando...";

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

    /// <summary>Enfileira uma verificação/aplicação de atualizações do Click-to-Run (OfficeC2RClient.exe /update user).</summary>
    public static async Task<bool> RunOfficeUpdateCheckAsync(
        OperationsQueueService queue,
        OfficeDeploymentToolService officeService,
        bool silent = true)
    {
        var item = queue.Enqueue("Verificar atualizações do Office", OperationKind.Update);
        item.StatusText = "Preparando...";

        try
        {
            using var slot = await OperationLimits.Packages.EnterAsync(item.CancellationTokenSource.Token);
            item.State = OperationState.Running;
            bool success = await officeService.RunUpdateNowAsync(
                silent,
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
        item.StatusText = "Preparando...";

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
            // A saída capturada do winget.exe não expõe uma medição confiável dos bytes.
            // Mantém a barra indeterminada em vez de interpretar percentuais arbitrários.
            item.StatusText = $"{activity}...";
        }
        else if (item.Method == WingetMethod.Unknown && item.IsIndeterminate)
        {
            item.StatusText = isLayerMessage ? $"{activity}..." : trimmed;
        }

        // O ODT fornece progresso real em suas mensagens; não inferir progresso dos logs
        // do WinGet, COM ou da API própria (esses métodos publicam progresso tipado).
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

    private static bool IsCanceledResult(OperationItem item, WingetExecutionResult result) =>
        item.CancellationTokenSource.IsCancellationRequested
        || result.FailureReason is WingetFailureReason.ElevationCanceled or WingetFailureReason.OperationCanceled;

    internal static void ReportInstallProgress(OperationItem item, InstallProgressUpdate update)
    {
        // Atualiza o método se fornecido no update
        if (update.Method != WingetMethod.Unknown)
        {
            item.Method = update.Method;
        }

        switch (update.Phase)
        {
            case InstallProgressPhase.Downloading when update.Percent is double percent:
                item.IsIndeterminate = false;
                item.Progress = Math.Clamp(percent, 0, 100);
                item.StatusText = $"Baixando... {item.Progress:0.00}%";
                break;
            case InstallProgressPhase.Downloading:
                item.IsIndeterminate = true;
                item.StatusText = "Baixando...";
                break;
            case InstallProgressPhase.Preparing:
                item.IsIndeterminate = true;
                item.StatusText = update.Method == WingetMethod.WingetExe
                    ? "Aguardando o WinGet..."
                    : "Verificando e preparando a instalação...";
                break;
            case InstallProgressPhase.Installing when update.Percent is double installPercent && installPercent > 0:
                item.IsIndeterminate = false;
                item.Progress = Math.Clamp(installPercent, 0, 100);
                item.StatusText = $"Instalando... {item.Progress:0}%";
                break;
            case InstallProgressPhase.Installing:
                item.IsIndeterminate = true;
                item.StatusText = "Instalando...";
                break;
        }
        item.LiveLine = item.StatusText;
    }

    [GeneratedRegex(@"(\d{1,3})\s?%")]
    private static partial Regex PercentRegex();
}
