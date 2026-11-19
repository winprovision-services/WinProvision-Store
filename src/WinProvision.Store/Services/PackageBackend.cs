using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services;
using System.Text.RegularExpressions;
namespace WinProvision.Store.Services;
public interface IPackageBackend
{
    IDisposable? BeginBatch() => null;
    Task<WingetExecutionResult> InstallAsync(AppEntry app, PackageInstallOptions? options = null);
    Task<WingetExecutionResult> UpdateAsync(UpgradablePackage package, PackageInstallOptions? options = null);
    Task<WingetExecutionResult> UninstallAsync(AppEntry app, bool interactive = false, bool requiresElevation = false, PackageInstallOptions? options = null);
    Task<WingetExecutionResult> DownloadAsync(string id, string? source, string directory, CancellationToken token, string? version = null);
    Task<List<UpgradablePackage>> ScanUpdatesAsync(CancellationToken token,
        Func<UpdateSourceScanResult, Task>? onSourceCompleted = null);
    Task ShutdownAsync();
    void CancelAll();
}

public sealed record UpdateSourceScanResult(string Source, IReadOnlyList<UpgradablePackage> Packages,
    string? ErrorMessage = null, int? ExitCode = null, TimeSpan? Duration = null);

public sealed partial class PackageBackend : IPackageBackend, IAsyncDisposable
{
    private static readonly TimeSpan WinGetUpdateTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ChocolateyUpdateTimeout = TimeSpan.FromSeconds(120);
    private static readonly Regex ChocolateyProgressRegex = new(
        @"^\s*Progress:\s*(?<percent>\d{1,3})%\s*(?:-\s*(?<status>.*))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static string? NormalizeVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) || version.Trim() == "-" ? null : version.Trim();

    public IDisposable BeginBatch() => _queue.BeginBatch();
    private readonly OperationsQueueService _queue;
    private readonly PackageInstallOptionsService _packageOptions;
    private readonly ApplicationPreferencesService _applicationPreferences;
    private readonly ChocolateyService _chocolatey;
    private readonly WingetExecutor _executor;
    private readonly InstalledAppsService _installed;
    private readonly WinProvision.PackageManagement.ApplicationEngine _engine;
    private readonly OfficeDeploymentToolService _office;
    private CancellationTokenSource _waiting = new();
    private readonly List<CancellationTokenSource> _retiredTokens = [];
    private readonly object _gate = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly HashSet<string> _packages = new(StringComparer.OrdinalIgnoreCase);
    private bool _stopping;
    public PackageBackend(OperationsQueueService queue, WingetExecutor executor, InstalledAppsService installed, PackageInstallOptionsService options, OfficeDeploymentToolService office, WinProvision.PackageManagement.ApplicationEngine engine, ApplicationPreferencesService applicationPreferences, ChocolateyService chocolatey)
    {
        _queue = queue; _executor = executor; _installed = installed; _packageOptions = options;
        _applicationPreferences = applicationPreferences;
        _chocolatey = chocolatey;
        _engine = engine; _office = office;
        OperationRunner.ConfigureInstallHandler(engine.InstallPreferredAsync);
        OperationRunner.ConfigureUpdateHandler(engine.UpdateAsync);
        OperationRunner.ConfigureUpdateVerifier(engine.VerifySuccessfulUpgradeAsync);
        OperationRunner.ConfigureInstallWarmupHandler((includeStore, log, token) => engine.PrepareAsync(token, includeStore, log));
        OperationRunner.ConfigurePackageInstallOptionsProvider(options.Get);
        OperationRunner.ConfigureInstalledVersionResolver(ResolveInstalledVersionAsync);
    }

    private async Task<string?> ResolveInstalledVersionAsync(string manager, string packageId, string? sourceName, CancellationToken token)
    {
        if (manager.Equals("chocolatey", StringComparison.OrdinalIgnoreCase))
        {
            IReadOnlyList<InstalledPackageInfo> installed = await _chocolatey.ListInstalledAsync(token).ConfigureAwait(false);
            return installed.FirstOrDefault(package => package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))?.Version;
        }

        string? source = sourceName?.Equals("Microsoft Store", StringComparison.OrdinalIgnoreCase) == true ? "msstore"
            : sourceName?.Equals("winget", StringComparison.OrdinalIgnoreCase) == true ? "winget"
            : null;
        List<UpgradablePackage> packages = await _executor.ListInstalledPackagesAsync(token, source).ConfigureAwait(false);
        return packages.FirstOrDefault(package => package.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase))?.CurrentVersion;
    }
    private static Task<WingetExecutionResult> SelfManagementBlocked() => Task.FromResult(new WingetExecutionResult
        { Success = false, ExitCode = 1, Output = ApplicationPackagePolicy.SelfManagementMessage });
    private bool IsWingetDisabled(string? source) => !_applicationPreferences.WingetEnabled
        && (string.IsNullOrWhiteSpace(source) || source.Equals("winget", StringComparison.OrdinalIgnoreCase)
            || source.Equals("msstore", StringComparison.OrdinalIgnoreCase));
    private bool IsChocolatey(string? source) => source?.Equals("chocolatey", StringComparison.OrdinalIgnoreCase) == true;
    private bool IsChocolateyDisabled(string? source) => IsChocolatey(source) && !_applicationPreferences.ChocolateyEnabled;
    private static Task<WingetExecutionResult> WingetDisabledBlocked() => Task.FromResult(new WingetExecutionResult
        { Success = false, ExitCode = 1, Output = "WinGet está desativado nas preferências de Gerenciadores de Pacotes." });
    private static Task<WingetExecutionResult> ChocolateyDisabledBlocked() => Task.FromResult(new WingetExecutionResult
        { Success = false, ExitCode = 1, Output = "Chocolatey está desativado nas preferências de Gerenciadores de Pacotes." });
    private PackageInstallOptions GetChocolateyOptions(string id, PackageInstallOptions? requested)
    {
        PackageInstallOptions defaults = _packageOptions.Get(id, "chocolatey") ?? new PackageInstallOptions(RequiresElevation: true);
        if (requested is null) return defaults;
        return defaults with
        {
            Interactive = defaults.Interactive || requested.Interactive,
            RequiresElevation = defaults.RequiresElevation || requested.RequiresElevation,
            IgnoreSecurityHash = defaults.IgnoreSecurityHash || requested.IgnoreSecurityHash,
            AllowPrerelease = defaults.AllowPrerelease || requested.AllowPrerelease,
            UninstallPrevious = defaults.UninstallPrevious || requested.UninstallPrevious,
            Architecture = requested.Architecture ?? defaults.Architecture,
            Version = requested.Version ?? defaults.Version,
            CustomArguments = requested.CustomArguments ?? defaults.CustomArguments,
            UpdateArguments = requested.UpdateArguments ?? defaults.UpdateArguments,
            UninstallArguments = requested.UninstallArguments ?? defaults.UninstallArguments
        };
    }
    private Task<WingetExecutionResult> RunChocolateyQueuedAsync(string id, string name, string? icon, OperationKind kind,
        PackageInstallOptions options, CancellationToken outerToken, string? versionFrom = null, string? versionTo = null)
    {
        var operation = _queue.Enqueue(name, kind, icon);
        operation.PackageId = id;
        operation.ManagerOverride = "chocolatey";
        operation.SourceNameOverride = "community";
        operation.VersionFrom = versionFrom;
        operation.VersionTo = versionTo ?? (kind == OperationKind.Install ? options.Version : null);
        operation.Snapshot = new OperationSnapshot(1, "chocolatey", "community", id, name, kind,
            versionFrom, versionTo ?? options.Version, options, options.InstallLocation, IconUrl: operation.IconUrl);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outerToken, operation.CancellationTokenSource.Token);
        operation.State = OperationState.Running;
        operation.SetStatusMessage(kind switch
        {
            OperationKind.Install => "operations.message.chocolatey.install",
            OperationKind.Update => "operations.message.chocolatey.update",
            _ => "operations.message.chocolatey.uninstall"
        });
        return CompleteChocolateyOperationAsync(operation, id, options, kind, linked.Token);
    }

    private async Task<WingetExecutionResult> CompleteChocolateyOperationAsync(OperationItem operation, string id,
        PackageInstallOptions options, OperationKind kind, CancellationToken token)
    {
        bool receivedOutput = false;
        void HandleOutput(string line)
        {
            receivedOutput = true;
            operation.AppendLog(line);
            WinProvisionLog.WritePackageOperation("chocolatey", line);
            Match progressMatch = ChocolateyProgressRegex.Match(line);
            if (progressMatch.Success && int.TryParse(progressMatch.Groups["percent"].Value, out int percent))
            {
                percent = Math.Clamp(percent, 0, 100);
                operation.Progress = percent;
                operation.IsIndeterminate = false;
                string detail = progressMatch.Groups["status"].Value.Trim();
                operation.SetStatusMessage(detail.Length == 0
                    ? "operations.message.chocolatey.download"
                    : "operations.message.chocolatey.downloadDetails", detail.Length == 0
                        ? percent.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : $"{percent}% · {detail}");
                return;
            }

            if (line.Contains("download", StringComparison.OrdinalIgnoreCase)
                || line.Contains("install", StringComparison.OrdinalIgnoreCase)
                || line.Contains("extract", StringComparison.OrdinalIgnoreCase)
                || line.Contains("verify", StringComparison.OrdinalIgnoreCase))
            {
                operation.IsIndeterminate = true;
                operation.StatusText = line.Trim();
            }
        }

        try
        {
            WingetExecutionResult result = kind switch
            {
                OperationKind.Install => await _chocolatey.InstallAsync(id, options, token, HandleOutput).ConfigureAwait(false),
                OperationKind.Update => await _chocolatey.UpgradeAsync(id, options, token, HandleOutput).ConfigureAwait(false),
                _ => await _chocolatey.UninstallAsync(id, options, token, HandleOutput).ConfigureAwait(false)
            };
            if (result.Success && kind is OperationKind.Install or OperationKind.Update)
            {
                try
                {
                    string? installedVersion = await ResolveInstalledVersionAsync("chocolatey", id, "community", token).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(installedVersion)) operation.VersionTo = installedVersion.Trim();
                }
                catch (Exception exception)
                {
                    WinProvisionLog.Warning(exception, "Chocolatey.VersionReconcile");
                }
            }
            if (!receivedOutput)
                foreach (string line in result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    operation.AppendLog(line);
            operation.SetStatusMessage(result.RebootRequired
                ? "operations.message.chocolatey.reboot"
                : result.Success ? "operations.message.chocolatey.completed" : "operations.message.chocolatey.failed");
            operation.RebootRequired = result.RebootRequired;
            operation.State = result.FailureReason == WingetFailureReason.ElevationCanceled
                ? OperationState.Canceled
                : result.Success ? OperationState.Completed : OperationState.Failed;
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            operation.State = OperationState.Canceled;
            operation.AppendLog("Operação cancelada.");
            throw;
        }
        catch (Exception exception)
        {
            operation.AppendLog(exception.Message);
            operation.StatusText = exception.Message;
            operation.State = OperationState.Failed;
            return new WingetExecutionResult { Success = false, ExitCode = 1, Output = exception.Message };
        }
    }
    public Task<WingetExecutionResult> InstallAsync(AppEntry app, PackageInstallOptions? options = null) => IsChocolateyDisabled(app.Source) ? ChocolateyDisabledBlocked() : IsWingetDisabled(app.Source) ? WingetDisabledBlocked() : app.IsSelf ? SelfManagementBlocked() : IsChocolatey(app.Source)
        ? TrackAsync($"{app.Source}:{app.Id}", token =>
        {
            PackageInstallOptions effectiveOptions = GetChocolateyOptions(app.Id, options);
            return RunChocolateyQueuedAsync(app.Id, app.Name, app.IconUrl, OperationKind.Install, effectiveOptions, token,
                versionTo: effectiveOptions.Version ?? NormalizeVersion(app.Version));
        })
        : TrackAsync($"{app.Source}:{app.Id}", async token =>
    {
        token.ThrowIfCancellationRequested();
        return await OperationRunner.RunInstallAsync(_queue, _executor, app.Id, app.Name, app.IconUrl, _installed, installLocation: options?.InstallLocation, source: app.Source, interactive: options?.Interactive == true, scope: options?.Scope, architecture: options?.Architecture, ignoreSecurityHash: options?.IgnoreSecurityHash == true, useSavedPackageOptions: options is null, requiresElevation: options?.RequiresElevation == true, version: options?.Version, customArguments: options?.CustomArguments, overrideArguments: options?.OverrideArguments, preInstallScript: options?.PreInstallScript, postInstallScript: options?.PostInstallScript, runInstallHooks: options?.RunInstallHooks == true, abortInstallOnPreScriptFailure: options?.AbortInstallOnPreScriptFailure ?? true, processesToClose: options?.ProcessesToClose, forceCloseProcesses: options?.ForceCloseProcesses == true, uninstallPrevious: options?.UninstallPrevious == true, requestedVersion: NormalizeVersion(app.Version), packageOptions: options);
    });
    public Task<WingetExecutionResult> UpdateAsync(UpgradablePackage package, PackageInstallOptions? options = null) => IsChocolateyDisabled(package.Source) ? ChocolateyDisabledBlocked() : IsWingetDisabled(package.Source) ? WingetDisabledBlocked() : ApplicationPackagePolicy.IsSelf(package.Id, package.Name) ? SelfManagementBlocked() : IsChocolatey(package.Source)
        ? TrackAsync($"{package.Source}:{package.Id}", token => RunChocolateyQueuedAsync(package.Id, package.Name, package.IconUrl,
            OperationKind.Update, GetChocolateyOptions(package.Id, options ?? new PackageInstallOptions(RequiresElevation: true,
                Version: package.AvailableVersion)), token, package.CurrentVersion, package.AvailableVersion))
        : TrackAsync($"{package.Source}:{package.Id}", async token =>
    {
        token.ThrowIfCancellationRequested();
        PackageInstallOptions? effectiveOptions = options ?? _packageOptions.Get(package.Id, package.Source);
        if (_applicationPreferences.ForceWinGetInstallLocationOnUpdate
            && _packageOptions.GetPackageOverride(package.Id, package.Source) is { InstallLocation: { Length: > 0 } savedLocation })
            effectiveOptions = (effectiveOptions ?? new PackageInstallOptions()) with { InstallLocation = savedLocation };
        return await OperationRunner.RunUpdateAsync(_queue, _executor, package.Id, package.Name, package.IconUrl, source: package.Source, elevated: effectiveOptions?.RequiresElevation == true, interactive: effectiveOptions?.Interactive == true, scope: effectiveOptions?.Scope, architecture: effectiveOptions?.Architecture, ignoreSecurityHash: effectiveOptions?.IgnoreSecurityHash == true, useSavedPackageOptions: effectiveOptions is null, version: effectiveOptions?.Version, offeredPackage: package, packageOptions: effectiveOptions);
    });
    public Task<WingetExecutionResult> UninstallAsync(AppEntry app, bool interactive = false, bool requiresElevation = false, PackageInstallOptions? options = null) => IsChocolateyDisabled(app.Source) ? ChocolateyDisabledBlocked() : IsWingetDisabled(app.Source) ? WingetDisabledBlocked() : app.IsSelf ? SelfManagementBlocked() : IsChocolatey(app.Source)
        ? TrackAsync($"{app.Source}:{app.Id}", token => RunChocolateyQueuedAsync(app.Id, app.Name, app.IconUrl, OperationKind.Uninstall,
            GetChocolateyOptions(app.Id, options) with { Interactive = interactive || options?.Interactive == true,
                RequiresElevation = true, Version = NormalizeVersion(app.Version) }, token, app.Version))
        : TrackAsync(InstalledPackagesService.IsOffice(app.Id, app.Name) ? "office:removeall" : $"{app.Source}:{app.Id}", async token =>
    {
        token.ThrowIfCancellationRequested();
        options ??= _packageOptions.Get(app.Id, app.Source);
        options ??= new PackageInstallOptions();
        interactive |= options.Interactive;
        requiresElevation |= options.RequiresElevation;
        if (InstalledPackagesService.IsOffice(app.Id, app.Name))
        {
            var request = new OfficeRemoveRequest(true,
                DisplayLevel: interactive ? OfficeDisplayLevel.Visible : OfficeDisplayLevel.Silent,
                CleanStoreEdition: true, UseAggressiveUninstall: true);
            bool removed = await OperationRunner.RunOfficeRemoveAsync(_queue, _office, request, "Remoção completa do Office (RemoveAll)");
            if (removed) _installed.MarkUninstalled();
            return new WingetExecutionResult { Success = removed };
        }
        await _installed.EnsureLoadedAsync(token);
        var match = _installed.GetAllInstalledApps().FirstOrDefault(item => item.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase) || item.DisplayName.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
        match ??= new InstalledAppDetail { Id = app.Id, DisplayName = app.Name };
        match.IconUrl = app.IconUrl;

        WingetExecutionResult result = await OperationRunner.RunUninstallWithHandlerAsync(
                _queue,
                match,
                (log, ct) => _engine.UninstallAsync(app.Id, app.Source, log, ct, interactive, requiresElevation, options),
                _installed,
                options,
                app.IconUrl);
        return result;

    });
    private Task<WingetExecutionResult> TrackAsync(string key, Func<CancellationToken, Task<WingetExecutionResult>> action)
    {
        string? source = key.Split(':', 2)[0];
        string? category = PackageManagerLogCategories.ForSource(source);
        string packageId = key.Contains(':') ? key[(key.IndexOf(':') + 1)..] : key;
        Task<WingetExecutionResult> task;
        lock (_gate)
        {
            if (_stopping) return Task.FromCanceled<WingetExecutionResult>(new CancellationToken(true));
            if (!_packages.Add(key)) return Task.FromException<WingetExecutionResult>(new InvalidOperationException("Este aplicativo já possui uma operação em andamento."));
            task = RunAndLogAsync(action, _waiting.Token, source, category, packageId);
            _tasks.Add(task);
        }
        _ = ObserveAsync(task, key);
        return task;
    }

    private static async Task<WingetExecutionResult> RunAndLogAsync(Func<CancellationToken, Task<WingetExecutionResult>> action,
        CancellationToken token, string? source, string? category, string packageId)
    {
        if (category is not null)
            WinProvisionLog.WritePackageOperation(source, $"Iniciando operação para {packageId}.");
        try
        {
            WingetExecutionResult result = await action(token).ConfigureAwait(false);
            if (category is not null)
            {
                string output = string.IsNullOrWhiteSpace(result.Output) ? string.Empty : Environment.NewLine + result.Output;
                WinProvisionLog.WritePackageOperation(source,
                    $"Operação para {packageId}: {(result.Success ? "concluída" : "falhou")} (código {result.ExitCode}).{output}",
                    failed: !result.Success);
            }
            return result;
        }
        catch (OperationCanceledException)
        {
            if (category is not null) WinProvisionLog.WritePackageOperation(source, $"Operação para {packageId} cancelada.", failed: true);
            throw;
        }
        catch (Exception exception)
        {
            if (category is not null) WinProvisionLog.WritePackageOperation(source,
                $"Operação para {packageId} falhou: {exception.Message}", failed: true);
            throw;
        }
    }
    private async Task ObserveAsync(Task task, string key)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "PackageOperation"); }
        finally { lock (_gate) { _tasks.Remove(task); _packages.Remove(key); } }
    }
    public async Task ShutdownAsync()
    {
        Task[] tasks;
        lock (_gate) { _stopping = true; tasks = _tasks.ToArray(); }
        CancelAll();
        foreach (var task in tasks)
        {
            try { await task; }
            catch (OperationCanceledException) { }
            catch (Exception exception) { WinProvisionLog.Warning(exception, "PackageShutdown"); }
        }
    }
    public void CancelAll()
    {
        CancellationTokenSource canceled;
        lock (_gate)
        {
            canceled = _waiting;
            _retiredTokens.Add(canceled);
            _waiting = new CancellationTokenSource();
        }
        canceled.Cancel();
        _queue.CancelAll();
    }
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync();
        _waiting.Dispose();
        foreach (var token in _retiredTokens) token.Dispose();
    }
    public async Task<WingetExecutionResult> DownloadAsync(string id, string? source, string directory, CancellationToken token, string? version = null)
    {
        if (IsChocolateyDisabled(source)) return await ChocolateyDisabledBlocked().ConfigureAwait(false);
        if (IsWingetDisabled(source)) return await WingetDisabledBlocked().ConfigureAwait(false);
        if (IsChocolatey(source)) return new WingetExecutionResult { Success = false, ExitCode = 1, Output = "O Chocolatey não oferece download isolado pelo fluxo atual." };

        OperationItem operation = _queue.Enqueue(id, OperationKind.Download);
        operation.PackageId = id;
        operation.Manager = "winget";
        operation.SourceName = source?.Equals("msstore", StringComparison.OrdinalIgnoreCase) == true ? "Microsoft Store" : "winget";
        operation.VersionTo = NormalizeVersion(version);
        operation.State = OperationState.Running;
        operation.SetStatusMessage("operations.message.packageDownloadStart");
        try
        {
            WingetExecutionResult result = await _engine.DownloadInstallerAsync(id, source, directory,
                line => { operation.AppendLog(line); WinProvisionLog.WritePackageOperation("winget", line); }, token).ConfigureAwait(false);
            operation.ExitCode = result.ExitCode;
            operation.AppendLog($"Resultado do download: {(result.Success ? "sucesso" : "falha")}; código={result.ExitCode}.");
            operation.State = result.Success ? OperationState.Completed : OperationState.Failed;
            operation.SetStatusMessage(result.Success ? "operations.message.packageDownloadCompleted" : "operations.message.packageDownloadFailed");
            return result;
        }
        catch (OperationCanceledException)
        {
            operation.State = OperationState.Canceled;
            operation.AppendLog("Download cancelado.");
            throw;
        }
        catch (Exception exception)
        {
            operation.AppendLog(exception.Message);
            operation.StatusText = exception.Message;
            operation.State = OperationState.Failed;
            throw;
        }
    }

    public async Task<List<UpgradablePackage>> ScanUpdatesAsync(CancellationToken token,
        Func<UpdateSourceScanResult, Task>? onSourceCompleted = null)
    {
        var packages = new List<UpgradablePackage>();
        var pending = new List<Task<UpdateSourceScanResult>>(2);
        if (_applicationPreferences.WingetEnabled)
            pending.Add(QuerySourceAsync("WinGet", "WinGetService.ScanUpdatesAsync", WinGetUpdateTimeout,
                token, sourceToken => _engine.ScanUpdatesAsync(null, sourceToken), exitCode: null));
        if (_applicationPreferences.ChocolateyEnabled)
        {
            string? chocolateyPath = _chocolatey.ExecutablePath;
            if (string.IsNullOrWhiteSpace(chocolateyPath) || !File.Exists(chocolateyPath))
            {
                const string command = "choco outdated --limit-output";
                WinProvisionLog.Write($"UPDATE SOURCE start source=Chocolatey command=\"{command}\" timeout={ChocolateyUpdateTimeout.TotalSeconds:0}s");
                LogSourceResult("Chocolatey", command, TimeSpan.Zero, null, 0, "CLI não encontrado");
                pending.Add(Task.FromResult(new UpdateSourceScanResult("Chocolatey", [], "CLI não encontrado.")));
            }
            else
                pending.Add(QueryChocolateyAsync(Path.GetFullPath(chocolateyPath), token));
        }

        try
        {
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                Task<UpdateSourceScanResult> completed = await Task.WhenAny(pending).ConfigureAwait(false);
                pending.Remove(completed);
                UpdateSourceScanResult result = await completed.ConfigureAwait(false);
                packages.AddRange(result.Packages);
                if (onSourceCompleted is not null) await onSourceCompleted(result).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            foreach (Task<UpdateSourceScanResult> task in pending) _ = ObserveLateSourceAsync(task);
            throw;
        }
        return packages;
    }

    private async Task<UpdateSourceScanResult> QueryChocolateyAsync(string executable, CancellationToken token)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        const string command = "choco outdated --limit-output";
        WinProvisionLog.Write($"UPDATE SOURCE start source=Chocolatey executable=\"{executable}\" command=\"{command}\" timeout={ChocolateyUpdateTimeout.TotalSeconds:0}s");
        using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        sourceTimeout.CancelAfter(ChocolateyUpdateTimeout);
        Task<ChocolateyOutdatedQueryResult> worker = Task.Run(
            () => _chocolatey.GetOutdatedWithExitCodeAsync(sourceTimeout.Token), CancellationToken.None);
        try
        {
            ChocolateyOutdatedQueryResult query = await worker.WaitAsync(sourceTimeout.Token).ConfigureAwait(false);
            WinProvisionLog.Write($"UPDATE SOURCE data source=Chocolatey executable=\"{executable}\" linesRead={query.LinesRead} interpreted={query.Packages.Count} stdout=\"{query.StandardOutput.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}\" stderr=\"{query.StandardError.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}\"");
            string? failure = query.ExitCode is 1 or -1 ? $"choco outdated retornou código {query.ExitCode}" : null;
            LogSourceResult("Chocolatey", command, timer.Elapsed, query.ExitCode, query.Packages.Count, failure);
            return new UpdateSourceScanResult("Chocolatey", query.Packages, failure, query.ExitCode, timer.Elapsed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = ObserveLateQueryAsync(worker, "Chocolatey");
            throw;
        }
        catch (OperationCanceledException)
        {
            _ = ObserveLateQueryAsync(worker, "Chocolatey");
            LogSourceResult("Chocolatey", command, timer.Elapsed, null, 0, "tempo esgotado; processo cancelado");
            return new UpdateSourceScanResult("Chocolatey", [], "tempo esgotado.", Duration: timer.Elapsed);
        }
        catch (TimeoutException exception)
        {
            LogSourceResult("Chocolatey", command, timer.Elapsed, null, 0, exception.Message);
            return new UpdateSourceScanResult("Chocolatey", [], "tempo esgotado.", Duration: timer.Elapsed);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "ChocolateyUpdates");
            LogSourceResult("Chocolatey", command, timer.Elapsed, null, 0, $"{exception.GetType().Name}: {exception.Message}");
            return new UpdateSourceScanResult("Chocolatey", [], exception.Message, Duration: timer.Elapsed);
        }
    }

    private static async Task<UpdateSourceScanResult> QuerySourceAsync(string source, string command, TimeSpan timeout,
        CancellationToken token, Func<CancellationToken, Task<List<UpgradablePackage>>> query, int? exitCode)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        WinProvisionLog.Write($"UPDATE SOURCE start source={source} command=\"{command}\" timeout={timeout.TotalSeconds:0}s");
        using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        sourceTimeout.CancelAfter(timeout);
        Task<List<UpgradablePackage>> worker = Task.Run(() => query(sourceTimeout.Token), CancellationToken.None);
        try
        {
            List<UpgradablePackage> found = await worker.WaitAsync(sourceTimeout.Token).ConfigureAwait(false);
            LogSourceResult(source, command, timer.Elapsed, exitCode, found.Count, null);
            return new UpdateSourceScanResult(source, found, ExitCode: exitCode, Duration: timer.Elapsed);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = ObserveLateQueryAsync(worker, source);
            throw;
        }
        catch (OperationCanceledException)
        {
            _ = ObserveLateQueryAsync(worker, source);
            LogSourceResult(source, command, timer.Elapsed, exitCode, 0, "tempo esgotado; cancelamento solicitado");
            return new UpdateSourceScanResult(source, [], "tempo esgotado.", ExitCode: exitCode, Duration: timer.Elapsed);
        }
        catch (TimeoutException exception)
        {
            LogSourceResult(source, command, timer.Elapsed, exitCode, 0, $"tempo esgotado: {exception.Message}");
            return new UpdateSourceScanResult(source, [], "tempo esgotado.", ExitCode: exitCode, Duration: timer.Elapsed);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, source + "Updates");
            LogSourceResult(source, command, timer.Elapsed, exitCode, 0, $"{exception.GetType().Name}: {exception.Message}");
            return new UpdateSourceScanResult(source, [], exception.Message, ExitCode: exitCode, Duration: timer.Elapsed);
        }
    }

    private static void LogSourceResult(string source, string command, TimeSpan elapsed, int? exitCode, int count, string? failure) =>
        WinProvisionLog.Write($"UPDATE SOURCE finish source={source} command=\"{command}\" durationMs={elapsed.TotalMilliseconds:0} exitCode={(exitCode?.ToString() ?? "n/a")} packages={count} failure=\"{failure ?? "none"}\"");

    private static async Task ObserveLateQueryAsync(Task task, string source)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) { WinProvisionLog.Warning(exception, source + "Updates.LateCompletion"); }
    }

    private static async Task ObserveLateSourceAsync(Task<UpdateSourceScanResult> task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "Updates.ScanCancelledSource"); }
    }
}
