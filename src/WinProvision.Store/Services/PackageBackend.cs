using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services;
namespace WinProvision.Store.Services;
public interface IPackageBackend
{
    IDisposable? BeginBatch() => null;
    Task<WingetExecutionResult> InstallAsync(AppEntry app, PackageInstallOptions? options = null);
    Task<WingetExecutionResult> UpdateAsync(UpgradablePackage package, PackageInstallOptions? options = null);
    Task<WingetExecutionResult> UninstallAsync(AppEntry app, bool interactive = false, bool requiresElevation = false);
    Task<WingetExecutionResult> DownloadAsync(string id, string? source, string directory, CancellationToken token);
    Task<List<UpgradablePackage>> ScanUpdatesAsync(CancellationToken token);
    Task ShutdownAsync();
    void CancelAll();
}
public sealed partial class PackageBackend : IPackageBackend, IAsyncDisposable
{
    public IDisposable BeginBatch() => _queue.BeginBatch();
    private readonly OperationsQueueService _queue;
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
    public PackageBackend(OperationsQueueService queue, WingetExecutor executor, InstalledAppsService installed, PackageInstallOptionsService options, OfficeDeploymentToolService office, WinProvision.PackageManagement.ApplicationEngine engine)
    {
        _queue = queue; _executor = executor; _installed = installed;
        _engine = engine; _office = office;
        OperationRunner.ConfigureInstallHandler(engine.InstallPreferredAsync);
        OperationRunner.ConfigureUpdateHandler(engine.UpdateAsync);
        OperationRunner.ConfigureUpdateVerifier(engine.VerifySuccessfulUpgradeAsync);
        OperationRunner.ConfigureInstallWarmupHandler((includeStore, log, token) => engine.PrepareAsync(token, includeStore, log));
        OperationRunner.ConfigurePackageInstallOptionsProvider(options.Get);
    }
    private static Task<WingetExecutionResult> SelfManagementBlocked() => Task.FromResult(new WingetExecutionResult
        { Success = false, ExitCode = 1, Output = ApplicationPackagePolicy.SelfManagementMessage });
    public Task<WingetExecutionResult> InstallAsync(AppEntry app, PackageInstallOptions? options = null) => app.IsSelf ? SelfManagementBlocked() : TrackAsync($"{app.Source}:{app.Id}", async token =>
    {
        token.ThrowIfCancellationRequested();
        return await OperationRunner.RunInstallAsync(_queue, _executor, app.Id, app.Name, app.IconUrl, _installed, installLocation: options?.InstallLocation, source: app.Source, interactive: options?.Interactive == true, scope: options?.Scope, architecture: options?.Architecture, ignoreSecurityHash: options?.IgnoreSecurityHash == true, useSavedPackageOptions: options is null, requiresElevation: options?.RequiresElevation == true, version: options?.Version, customArguments: options?.CustomArguments, preInstallScript: options?.PreInstallScript, postInstallScript: options?.PostInstallScript, runInstallHooks: options?.RunInstallHooks == true);
    });
    public Task<WingetExecutionResult> UpdateAsync(UpgradablePackage package, PackageInstallOptions? options = null) => ApplicationPackagePolicy.IsSelf(package.Id, package.Name) ? SelfManagementBlocked() : TrackAsync($"{package.Source}:{package.Id}", async token =>
    {
        token.ThrowIfCancellationRequested();
        return await OperationRunner.RunUpdateAsync(_queue, _executor, package.Id, package.Name, package.IconUrl, source: package.Source, elevated: options?.RequiresElevation == true, interactive: options?.Interactive == true, scope: options?.Scope, architecture: options?.Architecture, ignoreSecurityHash: options?.IgnoreSecurityHash == true, useSavedPackageOptions: options is null, version: options?.Version, offeredPackage: package, packageOptions: options);
    });
    public Task<WingetExecutionResult> UninstallAsync(AppEntry app, bool interactive = false, bool requiresElevation = false) => app.IsSelf ? SelfManagementBlocked() : TrackAsync(InstalledPackagesService.IsOffice(app.Id, app.Name) ? "office:removeall" : $"{app.Source}:{app.Id}", async token =>
    {
        token.ThrowIfCancellationRequested();
        if (InstalledPackagesService.IsOffice(app.Id, app.Name))
        {
            var request = new OfficeRemoveRequest(true,
                DisplayLevel: interactive ? OfficeDisplayLevel.Visible : OfficeDisplayLevel.Silent,
                CleanStoreEdition: true, UseRemoveMSI: true, UseAggressiveUninstall: true);
            bool removed = await OperationRunner.RunOfficeRemoveAsync(_queue, _office, request, "Remoção completa do Office (RemoveAll)");
            if (removed) _installed.MarkUninstalled();
            return new WingetExecutionResult { Success = removed };
        }
        await _installed.EnsureLoadedAsync(token);
        var match = _installed.GetAllInstalledApps().FirstOrDefault(item => item.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase) || item.DisplayName.Equals(app.Name, StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return await _engine.UninstallAsync(app.Id, app.Source, _ => { }, token, interactive, requiresElevation);

        WingetExecutionResult result = await OperationRunner.RunUninstallWithHandlerAsync(
                _queue,
                match,
                (log, ct) => _engine.UninstallAsync(app.Id, app.Source, log, ct, interactive, requiresElevation),
                _installed);
        return result;

    });
    private Task<WingetExecutionResult> TrackAsync(string key, Func<CancellationToken, Task<WingetExecutionResult>> action)
    {
        Task<WingetExecutionResult> task;
        lock (_gate)
        {
            if (_stopping) return Task.FromCanceled<WingetExecutionResult>(new CancellationToken(true));
            if (!_packages.Add(key)) return Task.FromException<WingetExecutionResult>(new InvalidOperationException("Este aplicativo já possui uma operação em andamento."));
            task = action(_waiting.Token);
            _tasks.Add(task);
        }
        _ = ObserveAsync(task, key);
        return task;
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
    public Task<WingetExecutionResult> DownloadAsync(string id, string? source, string directory, CancellationToken token) =>
        _engine.DownloadInstallerAsync(id, source, directory, null, token);

    public Task<List<UpgradablePackage>> ScanUpdatesAsync(CancellationToken token) =>
        _engine.ScanUpdatesAsync(null, token);
}
