using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Services;
using WinProvision.Elevation;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store;

public partial class App
{
    public Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        _operationsPanelReload?.Dispose();
        _operationsPanelReload = null;
        if (_services is null) { ShutdownComplete = true; return; }
        _automaticAppUpdateCancellation?.Cancel();
        if (_automaticAppUpdateTask is not null)
        {
            try { await _automaticAppUpdateTask; }
            catch (OperationCanceledException) when (_automaticAppUpdateCancellation?.IsCancellationRequested == true) { }
        }
        _automaticAppUpdateCancellation?.Dispose();
        _services.GetRequiredService<PresentationService>().CancelDialogs();
        _services.GetRequiredService<HomeViewModel>().RefreshCommand.Cancel();
        await _services.GetRequiredService<StorefrontViewModel>().OnNavigatedFromAsync();
        var detailsOverlay = _window?.CreatedDetailsOverlay;
        if (detailsOverlay is not null)
            await detailsOverlay.ShutdownAsync();
        _services.GetRequiredService<UpdatesViewModel>().ScanCommand.Cancel();
        _services.GetRequiredService<UpdatesViewModel>().UpdateSelectedCommand.Cancel();
        _services.GetRequiredService<InstalledPackagesViewModel>().RefreshCommand.Cancel();
        _services.GetRequiredService<InstalledPackagesViewModel>().RemoveSelectedCommand.Cancel();
        _services.GetRequiredService<InstalledPackagesViewModel>().DownloadSelectedCommand.Cancel();
        _services.GetRequiredService<CollectionsViewModel>().InstallSelectedCommand.Cancel();
        _services.GetRequiredService<CollectionsViewModel>().DownloadSelectedCommand.Cancel();
        _services.GetRequiredService<CollectionsViewModel>().ExportOfflineCommand.Cancel();
        await _services.GetRequiredService<CollectionsViewModel>().StopMetricsAsync();
        var account = _services.GetRequiredService<AccountSyncViewModel>();
        account.ConnectCommand.Cancel();
        account.SaveBackupCommand.Cancel();
        account.RestoreFromCloudCommand.Cancel();
        account.ImportJsonCommand.Cancel();
        account.ExportJsonCommand.Cancel();
        _services.GetRequiredService<OperationsQueueService>().CancelAll();
        await _services.GetRequiredService<IPackageBackend>().ShutdownAsync();
        await _services.GetRequiredService<UpdatesViewModel>().ShutdownAsync();
        await _services.GetRequiredService<SettingsViewModel>().ShutdownAsync();
        var details = _window?.CreatedDetailsViewModel;
        var installed = _services.GetRequiredService<InstalledPackagesViewModel>();
        var collections = _services.GetRequiredService<CollectionsViewModel>();
        var commands = new[] { _services.GetRequiredService<HomeViewModel>().RefreshCommand.ExecutionTask, _services.GetRequiredService<UpdatesViewModel>().ScanCommand.ExecutionTask, _services.GetRequiredService<UpdatesViewModel>().UpdateSelectedCommand.ExecutionTask, installed.RefreshCommand.ExecutionTask, installed.RemoveSelectedCommand.ExecutionTask, installed.DownloadSelectedCommand.ExecutionTask, collections.InstallSelectedCommand.ExecutionTask, collections.DownloadSelectedCommand.ExecutionTask, collections.ExportOfflineCommand.ExecutionTask, details?.InstallCommand.ExecutionTask, details?.UpdateCommand.ExecutionTask, details?.UninstallCommand.ExecutionTask, account.ConnectCommand.ExecutionTask, account.SaveBackupCommand.ExecutionTask, account.RestoreFromCloudCommand.ExecutionTask, account.RefreshCloudFilesCommand.ExecutionTask, account.RestoreSelectedCloudFileCommand.ExecutionTask, account.DeleteSelectedCloudFileCommand.ExecutionTask, account.DeleteAllAppGistsCommand.ExecutionTask, account.ImportJsonCommand.ExecutionTask, account.ExportJsonCommand.ExecutionTask };
        foreach (var command in commands) if (command is not null) { try { await command; } catch (Exception exception) { WinProvisionLog.Warning(exception, "ShutdownCommand"); } }
        await AsyncImage.ShutdownAsync();
        await _services.GetRequiredService<OperationsQueueService>().WaitForIdleAsync();
        try { await _services.GetRequiredService<IElevationCacheManager>().StopAsync(); }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "ShutdownModuleElevationCache"); }
        if (_legacyLogMigrationTask is not null)
            await _legacyLogMigrationTask;
        await _services.DisposeAsync();
        await AppLogRuntime.DisposeAsync();
        ShutdownComplete = true;
    }
}
