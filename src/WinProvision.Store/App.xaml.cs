using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Views;
namespace WinProvision.Store;
public partial class App : Application
{
    public static App CurrentApp => (App)Current;
#if VALIDATION_HARNESS
    public static bool IsSmokeTest => Environment.GetCommandLineArgs().Contains("--smoke-test");
#else
    public static bool IsSmokeTest => false;
#endif
    partial void ConfigureValidationDebug();
    partial void ConfigureValidationServices(ServiceCollection services);
    partial void TryLaunchValidation(ref bool handled);
    partial void ConfigureValidationWindow();
    private ServiceProvider? _services;
    private MainWindow? _window;
    private Task? _shutdown;
    private Task? _backgroundUpdateTask;
    public bool ShutdownComplete { get; private set; }
    public bool IsShuttingDown => _shutdown is not null;
    public App()
    {
        InitializeComponent();
        ConfigureValidationDebug();
        UnhandledException += (_, args) => WinProvisionLog.Error(args.Exception, "UnhandledException");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(DispatcherQueue.GetForCurrentThread()));
        services.AddSingleton<StoreService>();
        services.AddSingleton<WingetBootstrapper>();
        services.AddSingleton<WingetExecutor>();
        services.AddSingleton<WinProvisionApiService>();
        services.AddSingleton<PackageMetricsService>();
        services.AddSingleton<IPackageMetricsService>(provider => provider.GetRequiredService<PackageMetricsService>());
        services.AddSingleton<PowerShell7Bootstrapper>();
        services.AddSingleton<PowerShellScriptAnalyzerService>();
        services.AddSingleton<IOfflineCollectionSource, OfflineCollectionSource>();
        services.AddSingleton<IOfflineOfficeSource, OfflineOfficeSource>();
        services.AddSingleton<OfflineCollectionExportService>();
        services.AddSingleton<InstalledAppsService>();
        services.AddSingleton<OperationHistoryService>();
        services.AddSingleton<OperationsQueueService>();
        services.AddSingleton<InstallationPreferencesService>();
        services.AddSingleton<LocalBackupService>();
        services.AddSingleton<GitHubBackupService>();
        services.AddSingleton<IAccountBackupService, AccountBackupService>();
        WinProvision.PackageManagement.PackageDiagnostics.Configure(WinProvisionLog.Write, WinProvisionLog.WriteComServerInfo);
        services.AddSingleton<WinGetService>(p => new WinGetService(
            p.GetRequiredService<WingetExecutor>(), p.GetRequiredService<WinProvisionApiService>(),
            p.GetRequiredService<WingetBootstrapper>(), () => p.GetRequiredService<InstallationPreferencesService>().PreferredMethod));
        services.AddSingleton<IPackageBackend, PackageBackend>();
        services.AddSingleton<PresentationService>();
        services.AddSingleton<IPresentationService>(provider => provider.GetRequiredService<PresentationService>());
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<StorefrontViewModel>();
        services.AddSingleton<FeaturedCatalogService>();
        services.AddSingleton<IconService>();
        services.AddSingleton<WinProvision.Core.Services.Indexing.MsStoreCatalogService>();
        services.AddSingleton<WindowsAppNotificationService>();
        services.AddSingleton<CollectionsViewModel>();
        services.AddSingleton<OfficeDeploymentToolService>();
        services.AddSingleton<IOfficeCollectionInstaller, OfficeCollectionInstaller>();
        services.AddSingleton<OfficeInstalledProductsDetector>();
        services.AddSingleton<IOfficeConfigurationExportService, OfficeConfigurationExportService>();
        services.AddSingleton<IDesktopIntegrationService, DesktopIntegrationService>();
        services.AddSingleton<OfficeViewModel>();
        services.AddSingleton<InstalledIconResolver>();
        services.AddSingleton<IInstalledPackagesService, InstalledPackagesService>();
        services.AddSingleton<ApplicationPreferencesService>();
        services.AddSingleton<ApplicationThemeService>();
        services.AddSingleton<CacheService>();
        services.AddSingleton(_ => new AppUpdateService(
            "winprovision-services/WinProvision-Store",
            "WinProvision.Store-Setup.exe",
            "71B03A9D-2C64-4E81-BF6D-3A90257F1C49"));
        services.AddSingleton<SettingsViewModel>();
        services.AddTransient<SettingsPage>();
        services.AddSingleton<IBackgroundUpdateStartupService>(p => new BackgroundUpdateStartupService(p.GetRequiredService<ApplicationPreferencesService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundUpdateStartupService>.Instance));
        services.AddSingleton<IUpdatesIgnoreStore, UpdatesIgnoreStore>();
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<InstalledPackagesViewModel>();
        services.AddSingleton<BackgroundUpdateRunner>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<UninstallerEngineService>();
        services.AddSingleton<PackageInstallOptionsService>();
        services.AddSingleton<WingetPackageDetailsService>();
        services.AddSingleton<IPackageDetailsProvider, PackageDetailsProvider>();
        services.AddSingleton<IDetailsImageService, DetailsImageService>();
        services.AddSingleton<AppDetailsViewModel>();
        services.AddSingleton<Controls.AppDetailsOverlay>();
        services.AddSingleton<OperationsViewModel>();
        services.AddTransient<HomePage>();
        services.AddTransient<CollectionsPage>();
        services.AddTransient<UpdatesPage>();
        services.AddTransient<InstalledPackagesPage>();
        services.AddTransient<OfficePage>();
        services.AddSingleton<MoreViewModel>();
        services.AddTransient<MorePage>();
        services.AddSingleton<WinUiBackupSyncService>(provider => new WinUiBackupSyncService(
            provider.GetRequiredService<CollectionsViewModel>(),
            provider.GetRequiredService<IAccountBackupService>(),
            enableAutomaticSync: !IsSmokeTest));
        services.AddSingleton<AccountSyncViewModel>();
        services.AddTransient<AccountSyncPage>();
        ConfigureValidationServices(services);
        _services = services.BuildServiceProvider();
        bool validationHandled = false;
        TryLaunchValidation(ref validationHandled);
        if (validationHandled) return;
        if (Environment.GetCommandLineArgs().Contains("--background-update"))
        {
            _backgroundUpdateTask = RunBackgroundUpdatesAsync();
            _ = _backgroundUpdateTask.ContinueWith(
                task => WinProvisionLog.Error(task.Exception!, "BackgroundUpdateTask"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return;
        }
        _services.GetRequiredService<WinUiBackupSyncService>();
        _window = new MainWindow(_services);
        _services.GetRequiredService<SettingsViewModel>().RestartRequested += Settings_RestartRequested;
        ConfigureValidationWindow();
        _window.Activate();
    }
    private async void Settings_RestartRequested(object? sender, EventArgs e)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        if (_window is null) return;
        await ShutdownAsync();
        _window.Close();
    }
    private async Task RunBackgroundUpdatesAsync()
    {
        int code = 1;
        bool acquired = false;
        Mutex? gate = null;
        var notifications = new WindowsAppNotificationService();
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            gate = new Mutex(false, @"Local\WinProvision.WinUI3.BackgroundUpdates." + identity.User!.Value);
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }

            if (acquired)
            {
                notifications.TryRegister();
                code = await _services!.GetRequiredService<BackgroundUpdateRunner>().RunAsync(notifications);
            }
        }
        catch (Exception ex)
        {
            WinProvisionLog.Error(ex, "BackgroundUpdate");
            notifications.ShowBackgroundUpdateFailure();
        }
        finally
        {
            try { notifications.Unregister(); }
            catch (Exception ex) { WinProvisionLog.Error(ex, "BackgroundUpdateNotificationCleanup"); code = 1; }

            if (acquired)
            {
                try { await _services!.GetRequiredService<IPackageBackend>().ShutdownAsync(); }
                catch (Exception ex) { WinProvisionLog.Error(ex, "BackgroundUpdateBackendCleanup"); code = 1; }
            }

            try { await _services!.DisposeAsync(); }
            catch (Exception ex) { WinProvisionLog.Error(ex, "BackgroundUpdateServiceCleanup"); code = 1; }

            try
            {
                if (acquired) gate?.ReleaseMutex();
                gate?.Dispose();
            }
            catch (Exception ex) { WinProvisionLog.Error(ex, "BackgroundUpdateMutexCleanup"); code = 1; }

            Environment.ExitCode = code;
            Exit();
        }
    }

    public Task ShutdownAsync() => _shutdown ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        if (_services is null) { ShutdownComplete = true; return; }
        _services.GetRequiredService<PresentationService>().CancelDialogs();
        _services.GetRequiredService<HomeViewModel>().RefreshCommand.Cancel();
        await _services.GetRequiredService<StorefrontViewModel>().OnNavigatedFromAsync();
        await _services.GetRequiredService<Controls.AppDetailsOverlay>().ShutdownAsync();
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
        var more = _services.GetRequiredService<MoreViewModel>();
        more.CheckStatusCommand.Cancel();
        more.CancelLogLoad();
        _services.GetRequiredService<OperationsQueueService>().CancelAll();
        await _services.GetRequiredService<IPackageBackend>().ShutdownAsync();
        await _services.GetRequiredService<UpdatesViewModel>().ShutdownAsync();
        await _services.GetRequiredService<SettingsViewModel>().ShutdownAsync();
        var details = _services.GetRequiredService<AppDetailsViewModel>();
        var installed = _services.GetRequiredService<InstalledPackagesViewModel>();
        var collections = _services.GetRequiredService<CollectionsViewModel>();
        var commands = new[] { _services.GetRequiredService<HomeViewModel>().RefreshCommand.ExecutionTask, _services.GetRequiredService<UpdatesViewModel>().ScanCommand.ExecutionTask, _services.GetRequiredService<UpdatesViewModel>().UpdateSelectedCommand.ExecutionTask, installed.RefreshCommand.ExecutionTask, installed.RemoveSelectedCommand.ExecutionTask, installed.DownloadSelectedCommand.ExecutionTask, collections.InstallSelectedCommand.ExecutionTask, collections.DownloadSelectedCommand.ExecutionTask, collections.ExportOfflineCommand.ExecutionTask, details.InstallCommand.ExecutionTask, details.UpdateCommand.ExecutionTask, details.UninstallCommand.ExecutionTask, account.ConnectCommand.ExecutionTask, account.SaveBackupCommand.ExecutionTask, account.RestoreFromCloudCommand.ExecutionTask, account.ImportJsonCommand.ExecutionTask, account.ExportJsonCommand.ExecutionTask, more.CheckStatusCommand.ExecutionTask, more.OpenLogCommand.ExecutionTask, more.RefreshLogCommand.ExecutionTask };
        foreach (var command in commands) if (command is not null) { try { await command; } catch (Exception exception) { WinProvisionLog.Warning(exception, "ShutdownCommand"); } }
        await AsyncImage.ShutdownAsync();
        await _services.GetRequiredService<OperationsQueueService>().WaitForIdleAsync();
        await _services.DisposeAsync();
        ShutdownComplete = true;
    }
}
