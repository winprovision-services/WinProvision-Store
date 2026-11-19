using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WinProvision.Core.Services;
using WinProvision.Core.Models;
using WinProvision.Providers.WinGet.Com;
using WinProvision.Providers.WinGet.Com.Contracts;
using WinProvision.WinGet.Contracts;
using ProviderOperationResult = WinProvision.Providers.WinGet.Com.Contracts.WinGetComOperationResult;

namespace WinProvision.PackageManagement;

/// <summary>Core orchestration around the isolated WinGet COM provider.</summary>
internal sealed class WinGetComOperationService
{
    private readonly AppLogTaskExecutor? _taskExecutor;
    private readonly IWinGetComClient _client;
    private readonly IWinGetComElevator _elevator;
    private readonly ILogger? _logger;
    private static readonly TimeSpan ComProgressTimeout = TimeSpan.FromSeconds(180);

    internal WinGetComOperationService(AppLogTaskExecutor? taskExecutor = null, IWinGetComClient? client = null,
        IWinGetComElevator? elevator = null, ILogger? logger = null)
    {
        _taskExecutor = taskExecutor;
        _logger = logger ?? CoreLogLogger.Instance;
        _client = client ?? new WinGetComClient(_logger);
        _elevator = elevator ?? new WinGetComElevatorAdapter();
    }

    internal async Task<WingetExecutionResult?> UninstallAsync(string id, string? source, Action<string>? log, CancellationToken token)
    {
        async Task<WingetExecutionResult?> Run(CancellationToken innerToken)
        {
            var installed = await _client.ListInstalledAsync(new(source), innerToken).ConfigureAwait(false);
            if (installed.Status != WinGetComStatus.Success || installed.Value is null) return null;
            var match = installed.Value.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                && (source is null || item.Source.Equals(source, StringComparison.OrdinalIgnoreCase)));
            if (match is null) return null;
            if (!WinGetComClient.InProcComEnabled && !string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase)
                && match.InstalledScope?.Equals("machine", StringComparison.OrdinalIgnoreCase) == true)
                return await WinGetElevatedComService.RunElevatedComWorkerAsync(ComOperationKind.Uninstall, id, null,
                    log, null, innerToken, new ComAttemptState()).ConfigureAwait(false);
            var result = await _client.UninstallAsync(new(id, source, Silent: true), token: innerToken).ConfigureAwait(false);
            bool started = WasComUninstallerDispatched(result.Status, result.ExtendedHResult ?? result.HResult);
            PackageDiagnostics.Write($"WinGet COM uninstall provider result; packageId={id}; status={result.Status}; hresult=0x{result.HResult:X8}; extendedHresult=0x{result.ExtendedHResult.GetValueOrDefault():X8}; installerExit={result.InstallerExitCode}; phase={result.FurthestPhase}; installerStarted={started}; wasElevated=false");
            return ToExecutionResult(result, started);
        }
        return _taskExecutor is not null
            ? await _taskExecutor.RunNativeAsync(new AppLogTaskDescriptor(PackageManagerLogCategories.Winget, "WinGet.COM", "Uninstall", "WinGet"), Run, token).ConfigureAwait(false)
            : await Run(token).ConfigureAwait(false);
    }

    internal Task<WinGetComOperationResult> ExecuteAsync(ComOperationKind opKind, string packageId,
        Action<string>? onLogReceived, Action<InstallProgressUpdate>? onProgress, CancellationToken cancellationToken,
        string? installLocation, string source, bool forceComOnly, PackageInstallOptions options)
    {
        string taskType = opKind == ComOperationKind.Install ? "Install" : "Update";
        Task<WinGetComOperationResult> Run(CancellationToken innerToken) => ExecuteCoreAsync(opKind, packageId,
            onLogReceived, onProgress, innerToken, installLocation, source, forceComOnly, options);
        return _taskExecutor is not null
            ? _taskExecutor.RunNativeAsync(new AppLogTaskDescriptor(PackageManagerLogCategories.Winget, "WinGet.COM", taskType, "WinGet"), Run, cancellationToken)
            : Run(cancellationToken);
    }

    private async Task<WinGetComOperationResult> ExecuteCoreAsync(ComOperationKind opKind, string packageId,
        Action<string>? log, Action<InstallProgressUpdate>? progress, CancellationToken token,
        string? installLocation, string source, bool forceComOnly, PackageInstallOptions options)
    {
        token.ThrowIfCancellationRequested();
        string tag = opKind == ComOperationKind.Install ? "INSTALL COM" : "UPDATE COM";
        WinGetComPackage? installed = null;
        if (opKind == ComOperationKind.Upgrade)
        {
            try
            {
                var installedResult = await _client.ListInstalledAsync(new(source), token).ConfigureAwait(false);
                installed = installedResult.Value?.SingleOrDefault(item => item.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase)
                    && item.Source.Equals(source, StringComparison.OrdinalIgnoreCase));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { PackageDiagnostics.Warning(exception, "WinGetComOperationService.InstalledVariant"); }
        }
        var selection = WinGetVariantSelectionBuilder.Build(options, installed?.InstalledScope, installed?.InstallerType);
        selection.MarkUnfixed("installerId", "hash", "installerType", "architecture", "locale");
        if (opKind == ComOperationKind.Upgrade && options.Scope is not null && installed?.InstalledScope is { } installedScope
            && !options.Scope.Equals(installedScope, StringComparison.OrdinalIgnoreCase))
            log?.Invoke($"A atualização preservará o escopo instalado ({installedScope}); o escopo solicitado ({options.Scope}) não será aplicado.");
        bool installedUpgradeScopeKnown = opKind != ComOperationKind.Upgrade
            || installed?.InstalledScope is "user" or "machine";
        if (!installedUpgradeScopeKnown)
        {
            selection.Scope = null;
            selection.MarkUnfixed("scope");
            log?.Invoke("O escopo instalado do upgrade não foi obtido; a seleção atual do WinGet será preservada e a elevação por metadados não será aplicada.");
        }
        var preferences = installedUpgradeScopeKnown
            ? await TryGetInstallerPreferencesAsync(packageId, source, token, selection, log).ConfigureAwait(false)
            : null;
        if (opKind == ComOperationKind.Upgrade && selection.InstalledScope is { } upgradeScope)
        {
            selection.Scope = upgradeScope;
            if (selection.InstalledInstallerType is { } installedType)
                selection.InstallerType = installedType;
            if (preferences is not null)
            {
                ResolvedInstallerElevation upgradeElevation = InstallerElevationResolver.Resolve(
                    selection.InstallerType, upgradeScope, preferences.ElevationRequirement);
                preferences = preferences with { Scope = upgradeScope, InstallerType = selection.InstallerType,
                    RequiresElevation = upgradeElevation.RequiresElevation, Selection = selection };
            }
        }
        string variantLog = $"id={packageId}; version={selection.Version}; installerId=not-confirmed; variantConfirmed=false; fixed={string.Join(',', selection.FixedFields)}; unfixed={string.Join(',', selection.UnfixedFields)}";
        CoreLog.Information("WinGetVariantSelection", variantLog);
        log?.Invoke($"Seleção WinGet: {variantLog}");
        bool isSystem = WindowsIdentityContext.IsRunningAsSystem();
        string? requestedScope = selection.Scope ?? (isSystem ? "machine" : preferences?.Scope);
        if (preferences is not null)
        {
            string? catalogScope = preferences.Scope;
            if (opKind == ComOperationKind.Install && selection.RequestedScope is { } requested
                && catalogScope is { } examined && !requested.Equals(examined, StringComparison.OrdinalIgnoreCase))
            {
                const string message = "O escopo solicitado não corresponde ao escopo disponível para este instalador. A operação não foi iniciada.";
                log?.Invoke(message);
                var mismatch = new WingetExecutionResult { Success = false, ExitCode = -1, Output = message,
                    FailureReason = WingetFailureReason.NoApplicableInstallers };
                ApplyVariantSelection(mismatch, selection);
                return new(mismatch, preferences, "requested-scope-mismatch");
            }
            ResolvedInstallerElevation resolvedElevation = InstallerElevationResolver.Resolve(
                preferences.InstallerType, requestedScope, preferences.ElevationRequirement);
            preferences = preferences with { Scope = requestedScope, Architecture = selection.Architecture ?? preferences.Architecture,
                RequiresElevation = resolvedElevation.RequiresElevation, ElevationProhibited = resolvedElevation.Prohibited, Selection = selection };
            if (isSystem && catalogScope == "user")
            {
                log?.Invoke("O catálogo indicou instalador somente para usuário, incompatível com o contexto SYSTEM.");
                return new(new WingetExecutionResult { Success = false, ExitCode = -1, Output = "O instalador exige escopo de máquina; a instalação COM não foi iniciada." }, preferences with { Scope = "machine" }, "SYSTEM-requer-escopo-machine");
            }
            if (source.Equals("winget", StringComparison.OrdinalIgnoreCase)
                && preferences.RequiresElevation)
            {
                CoreLog.Information("WinGetVariantSelection", $"id={packageId}; version={selection.Version}; installerId=not-confirmed; variantConfirmed=false; fixed={string.Join(',', selection.FixedFields)}; unfixed={string.Join(',', selection.UnfixedFields)}; reason={resolvedElevation.Reason}");
                log?.Invoke("A operação COM será executada no host local elevado pelo WinProvision Elevator.");
                var elevated = await _elevator.RunAsync(opKind, packageId, installLocation, selection,
                    log, progress, token).ConfigureAwait(false);
                ApplyVariantSelection(elevated, selection);
                return new(elevated, preferences);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var watchdogCts = new CancellationTokenSource();
        using var heartbeatCts = new CancellationTokenSource();
        long lastProgressAt = Stopwatch.GetTimestamp();
        int watchdogTimedOut = 0;
        WinGetComInstallProgress? latest = null;
        var syncProgress = new SynchronousProgress<WinGetComInstallProgress>(item =>
        {
            latest = item; Volatile.Write(ref lastProgressAt, Stopwatch.GetTimestamp());
            MapProgress(item, progress);
        });
        var watchdog = MonitorWatchdogAsync(watchdogCts.Token, operationCts,
            () => Stopwatch.GetElapsedTime(Volatile.Read(ref lastProgressAt)), () => Interlocked.Exchange(ref watchdogTimedOut, 1), stopwatch, tag);
        var heartbeat = MonitorHeartbeatAsync(tag, heartbeatCts.Token, () => latest,
            () => Stopwatch.GetElapsedTime(Volatile.Read(ref lastProgressAt)), stopwatch);
        ProviderOperationResult providerResult;
        try
        {
            log?.Invoke("Comunicando com a API COM do WinGet...");
            providerResult = opKind == ComOperationKind.Install
                ? await _client.InstallAsync(new(packageId, source, installLocation, requestedScope,
                    null, options.Interactive, selection), syncProgress, token: operationCts.Token).ConfigureAwait(false)
                : await _client.UpgradeAsync(new(packageId, source, Selection: selection), syncProgress, token: operationCts.Token).ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception exception) when (exception.NativeErrorCode == 740)
        {
            log?.Invoke("O WinGet COM foi recusado antes do início (740); repetindo uma vez pelo WinProvision Elevator.");
            var elevated = await _elevator.RunAsync(opKind, packageId, installLocation, selection,
                log, progress, token).ConfigureAwait(false);
            ApplyVariantSelection(elevated, selection);
            return new(elevated, preferences);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref watchdogTimedOut) == 1 && !token.IsCancellationRequested)
        {
            providerResult = new(WinGetComStatus.OutcomeUnknown, unchecked((int)0x800705B4), TechnicalMessage: "WinGet COM progress timeout after dispatch.", FurthestPhase: WinGetComPhase.Dispatched);
        }
        finally
        {
            watchdogCts.Cancel(); heartbeatCts.Cancel();
            await watchdog.ConfigureAwait(false); await heartbeat.ConfigureAwait(false);
        }

        bool installerStarted = providerResult.FurthestPhase >= WinGetComPhase.Installing
            || providerResult.Status == WinGetComStatus.OutcomeUnknown && providerResult.FurthestPhase >= WinGetComPhase.Dispatched;
        var execution = ToExecutionResult(providerResult, installerStarted);
        ApplyVariantSelection(execution, selection);
        if (execution.Success)
        {
            string action = opKind == ComOperationKind.Install ? "Instalação" : "Atualização";
            log?.Invoke(execution.RebootRequired ? $"{action} concluída via API COM; reinicialização necessária." : $"{action} concluída via API COM do WinGet.");
            return new(execution, preferences);
        }

        bool fallback = CanFallback(providerResult, installerStarted, forceComOnly, token);
        string? reason = fallback ? $"COM-status-{providerResult.Status}(0x{providerResult.ExtendedHResult ?? providerResult.HResult:X8})" : null;
        if (providerResult.Status == WinGetComStatus.OutcomeUnknown)
            log?.Invoke("A API COM não confirmou o resultado após o despacho; não será iniciada outra rota.");
        else if (fallback)
            log?.Invoke("A API COM falhou antes de iniciar o instalador. Tentando a próxima rota.");
        PackageDiagnostics.Write($"{tag} status={providerResult.Status} phase={providerResult.FurthestPhase} hresult=0x{providerResult.HResult:X8} elapsed={stopwatch.Elapsed}");
        return new(execution, preferences, reason);
    }

    private async Task<InstallerPreferences?> TryGetInstallerPreferencesAsync(string id, string source, CancellationToken token,
        WinGetVariantSelectionOptions selection, Action<string>? log = null)
    {
        try
        {
            var result = await _client.GetInstallerMetadataAsync(id, source, selection, token).ConfigureAwait(false);
            if (result.Status != WinGetComStatus.Success || result.Value is not { } metadata) return null;
            string? scope = metadata.Scope?.Equals("System", StringComparison.OrdinalIgnoreCase) == true ? "machine"
                : metadata.Scope?.Equals("User", StringComparison.OrdinalIgnoreCase) == true ? "user" : null;
            selection.Scope = scope ?? selection.InstalledScope ?? selection.RequestedScope;
            string rawScope = metadata.Scope ?? "null";
            string applicableInstaller = $"scope={rawScope},architecture={metadata.Architecture ?? "unknown"},type={metadata.InstallerType ?? "unknown"}";
            CoreLog.Information("WinGetComInstallerScope",
                $"packageId={id}; version={metadata.Version ?? selection.Version ?? "unknown"}; applicableInstallers=[{{{applicableInstaller}}}]; requestedScope={selection.RequestedScope ?? "none"}; effectiveScope={selection.Scope ?? "none"}; scopeUndeclared={rawScope.Equals("Unknown", StringComparison.OrdinalIgnoreCase)}");
            log?.Invoke($"Instalador aplicável COM: scope={rawScope}; architecture={metadata.Architecture ?? "unknown"}; type={metadata.InstallerType ?? "unknown"}");
            if (scope is null)
            {
                selection.Scope = selection.InstalledScope ?? selection.RequestedScope;
                CoreLog.Information("WinGetComInstallerScope",
                    $"escopo não declarado pelo manifesto; requestedScope={selection.RequestedScope ?? "none"}; effectiveScope={selection.Scope ?? "none"}; action=preserve-request-or-installed-scope");
                log?.Invoke("escopo não declarado pelo manifesto");
            }
            else
                selection.Scope = scope;
            selection.Architecture = WinGetComQueryService.ReadInstallerArchitecture(metadata);
            selection.InstallerType = metadata.InstallerType;
            selection.Version = metadata.Version ?? selection.Version;
            selection.MarkFixed("version");
            if (selection.Scope is null) selection.MarkUnfixed("scope");
            else selection.MarkFixed("scope");
            selection.MarkUnfixed("installerId", "hash", "installerType", "architecture", "locale");
            var elevation = InstallerElevationResolver.Resolve(metadata.InstallerType, selection.Scope, metadata.ElevationRequirement);
            return new(selection.Scope, selection.Architecture, elevation.RequiresElevation, elevation.Prohibited,
                metadata.InstallerType, metadata.ElevationRequirement, selection);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { PackageDiagnostics.Warning(ex, "WinGetComOperationService.InstallerMetadata"); return null; }
    }

    private static void ApplyVariantSelection(WingetExecutionResult result, WinGetVariantSelectionOptions selection)
    {
        result.VariantConfirmed = false;
        result.VariantFixedFields = selection.FixedFields.ToArray();
        result.VariantUnfixedFields = selection.UnfixedFields.ToArray();
    }

    internal static bool CanFallback(ProviderOperationResult result, bool installerStarted, bool forceComOnly, CancellationToken token)
    {
        int code = result.InstallerExitCode is { } installerCode && installerCode != 0
            ? unchecked((int)installerCode) : result.ExtendedHResult ?? result.HResult;
        if (forceComOnly || token.IsCancellationRequested || !WinGetFactoryHelper.CliFallbackAllowed || installerStarted
            || result.Status is WinGetComStatus.AccessDenied or WinGetComStatus.BlockedByPolicy or WinGetComStatus.OutcomeUnknown
                or WinGetComStatus.UnexpectedError or WinGetComStatus.ManifestFailed
                or WinGetComStatus.AgreementsNotAccepted)
            return false;
        if (result.Status == WinGetComStatus.BlockedByPolicy
            || InstallerExitCodes.Classify(code) == WingetFailureReason.BlockedByPolicy
            || code == unchecked((int)0x800704C7)) return false;
        if (result.Status == WinGetComStatus.InvalidOptions && code == unchecked((int)0x8A150002)) return true;
        if (result.InstallerExitCode is { } nativeCode && nativeCode != 0)
            return nativeCode == 740 && result.ExtendedHResult is null or 0;
        if (code == unchecked((int)0x800702E4)) return true;
        if (result.Status == WinGetComStatus.InstallerFailed) return false;
        return result.Status is WinGetComStatus.PackageNotFound or WinGetComStatus.CatalogOrSourceFailed
            or WinGetComStatus.InvalidOptions or WinGetComStatus.NoApplicableInstallers
            or WinGetComStatus.DownloadFailed or WinGetComStatus.ComUnavailable or WinGetComStatus.ClassNotRegistered
            or WinGetComStatus.ActivationFailed or WinGetComStatus.ApartmentError;
    }

    private static WingetExecutionResult ToExecutionResult(ProviderOperationResult result, bool installerStarted = false)
    {
        int exitCode = result.InstallerExitCode is { } code && code != 0
            ? unchecked((int)code) : result.ExtendedHResult ?? result.HResult;
        var failure = InstallerExitCodes.Classify(exitCode);
        if (failure == WingetFailureReason.Unknown)
            failure = result.Status switch
            {
                WinGetComStatus.BlockedByPolicy => WingetFailureReason.BlockedByPolicy,
                WinGetComStatus.NoApplicableInstallers => WingetFailureReason.NoApplicableInstallers,
                WinGetComStatus.NoApplicableUpgrade => WingetFailureReason.NoApplicableUpgrade,
                WinGetComStatus.DownloadFailed => WingetFailureReason.DownloadError,
                WinGetComStatus.AccessDenied => WingetFailureReason.ElevationRequired,
                WinGetComStatus.Canceled => WingetFailureReason.OperationCanceled,
                _ => result.Status == WinGetComStatus.Success ? WingetFailureReason.Unknown : WingetFailureReason.InternalError
            };
        return new()
        {
            Success = result.Status == WinGetComStatus.Success,
            ExitCode = result.Status == WinGetComStatus.Success ? 0 : exitCode,
            RebootRequired = result.RebootRequired == true,
            Output = result.Status == WinGetComStatus.NoApplicableUpgrade
                ? "A API COM informou que a atualização oferecida não é mais aplicável. Atualize a lista e tente novamente."
                : result.TechnicalMessage ?? result.Status.ToString(),
            FailureReason = failure,
            InstallerStarted = installerStarted || result.Status == WinGetComStatus.OutcomeUnknown
        };
    }

    private static void MapProgress(WinGetComInstallProgress item, Action<InstallProgressUpdate>? callback)
    {
        if (callback is null) return;
        double? raw = item.State == WinGetComInstallState.Downloading ? item.DownloadProgress : item.InstallationProgress;
        if (item.State == WinGetComInstallState.Downloading && raw is null && item.BytesRequired is > 0)
            raw = (double)item.BytesDownloaded.GetValueOrDefault() / item.BytesRequired.Value;
        int? percent = raw is { } value && double.IsFinite(value) ? (int)Math.Clamp(Math.Round(value * 100, MidpointRounding.AwayFromZero), 0, 100) : null;
        var phase = item.State switch
        {
            WinGetComInstallState.Downloading => InstallProgressPhase.Downloading,
            WinGetComInstallState.Installing => InstallProgressPhase.Installing,
            WinGetComInstallState.Queued => InstallProgressPhase.Preparing,
            WinGetComInstallState.PostInstall => InstallProgressPhase.Installing,
            _ => InstallProgressPhase.Installing
        };
        callback(new(phase, percent, WingetMethod.ComApi));
    }

    internal static bool WasComUninstallerDispatched(WinGetComStatus status, int extendedErrorCode) =>
        status is not (WinGetComStatus.InvalidOptions or WinGetComStatus.CatalogOrSourceFailed
            or WinGetComStatus.ManifestFailed or WinGetComStatus.BlockedByPolicy or WinGetComStatus.PackageNotFound
            or WinGetComStatus.ComUnavailable or WinGetComStatus.ClassNotRegistered or WinGetComStatus.AccessDenied
            or WinGetComStatus.ApartmentError or WinGetComStatus.ActivationFailed or WinGetComStatus.UnexpectedError)
        && unchecked((uint)extendedErrorCode) != 0x8A15002F;

    internal static int GetComUninstallFailureCode(uint uninstallerErrorCode, int extendedErrorCode) =>
        uninstallerErrorCode != 0 ? unchecked((int)uninstallerErrorCode) : extendedErrorCode;

    internal static int GetComFailureExitCode(int installerErrorCode, int extendedErrorCode) =>
        installerErrorCode != 0 ? installerErrorCode : extendedErrorCode;

    internal static bool IsPreExecutionComFailure(int installerErrorCode, int extendedErrorCode) =>
        installerErrorCode == 0 && extendedErrorCode is unchecked((int)0x8A150002) or unchecked((int)0x8A150010);

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    private static async Task MonitorWatchdogAsync(CancellationToken stop, CancellationTokenSource operation,
        Func<TimeSpan> elapsed, Action timedOut, Stopwatch stopwatch, string tag)
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stop).ConfigureAwait(false);
                if (elapsed() < ComProgressTimeout) continue;
                CoreLog.Debug($"{tag}: no progress for {ComProgressTimeout.TotalSeconds:0} seconds at {stopwatch.Elapsed}; cancelling.");
                timedOut(); operation.Cancel(); return;
            }
        }
        catch (OperationCanceledException) { PackageDiagnostics.Write($"{tag} watchdog stopped after {stopwatch.Elapsed}."); }
    }

    private static async Task MonitorHeartbeatAsync(string tag, CancellationToken token,
        Func<WinGetComInstallProgress?> read, Func<TimeSpan> elapsed, Stopwatch stopwatch)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                var p = read();
                PackageDiagnostics.Write($"{tag} heartbeat state={p?.State} download={p?.DownloadProgress} install={p?.InstallationProgress} sinceLastCallback={elapsed()} elapsed={stopwatch.Elapsed}");
            }
        }
        catch (OperationCanceledException) { PackageDiagnostics.Write($"{tag} heartbeat stopped after {stopwatch.Elapsed}."); }
    }
}
