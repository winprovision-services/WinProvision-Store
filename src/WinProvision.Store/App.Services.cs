using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Views;

namespace WinProvision.Store;

public partial class App
{
    private ServiceProvider BuildServiceProvider()
    {
        var services = new ServiceCollection();
        RegisterCoreServices(services);
        RegisterPackageServices(services);
        RegisterViewModels(services);
        RegisterViews(services);
        ConfigureValidationServices(services);
        return services.BuildServiceProvider();
    }

    private void RegisterCoreServices(ServiceCollection services)
    {
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(DispatcherQueue.GetForCurrentThread()));
        services.AddSingleton<HttpClient>(_ => NetworkClientFactory.Create(TimeSpan.FromSeconds(30)));
        services.AddSingleton<StoreService>(provider => new StoreService(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<IconService>()));
        services.AddSingleton<IStoreCatalog>(provider => provider.GetRequiredService<StoreService>());
        services.AddSingleton<WingetBootstrapper>();
        services.AddSingleton<WingetExecutor>(provider => new WingetExecutor(
            provider.GetRequiredService<WingetBootstrapper>(),
            provider.GetRequiredService<OperationPreferencesService>()));
        services.AddSingleton<WinProvisionApiService>(provider => new WinProvisionApiService(
            operationPreferences: provider.GetRequiredService<OperationPreferencesService>()));
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
        services.AddSingleton<OperationPreferencesService>();
        services.AddSingleton<InstallationPreferencesService>();
        services.AddSingleton<LocalBackupService>();
        services.AddSingleton<GitHubBackupService>();
        services.AddSingleton<ICloudBackupProvider>(provider => provider.GetRequiredService<GitHubBackupService>());
        services.AddSingleton<IAccountBackupService, AccountBackupService>();
        services.AddSingleton<FeaturedCatalogService>();
        services.AddSingleton<IconService>();
        services.AddSingleton<WinProvision.Core.Services.Indexing.MsStoreCatalogService>();
        services.AddSingleton<ProvisioningWallpaperCatalogService>();
        services.AddSingleton<OfficeDeploymentToolService>();
        services.AddSingleton<IOfficeCollectionInstaller, OfficeCollectionInstaller>();
        services.AddSingleton<OfficeInstalledProductsDetector>();
        services.AddSingleton<IOfficeConfigurationExportService, OfficeConfigurationExportService>();
        services.AddSingleton<IDesktopIntegrationService, DesktopIntegrationService>();
        services.AddSingleton<IElevatorSettingsService, ElevatorSettingsService>();
        services.AddSingleton<ICacheSettingsService, CacheSettingsService>();
        services.AddSingleton<InstalledIconResolver>();
        services.AddSingleton<IInstalledPackagesService, InstalledPackagesService>();
        services.AddSingleton<ApplicationPreferencesService>();
        services.AddSingleton<ApplicationThemeService>();
        services.AddSingleton<CacheService>();
        services.AddSingleton(_ => new AppUpdateService(
            "winprovision-services/WinProvision-Store",
            "WinProvision.Store-Setup.exe",
            "71B03A9D-2C64-4E81-BF6D-3A90257F1C49"));
        services.AddSingleton<IUpdatesIgnoreStore, UpdatesIgnoreStore>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<UninstallerEngineService>();
        services.AddSingleton<PackageUninstallService>();
        services.AddSingleton<PackageInstallOptionsService>();
        services.AddSingleton<WingetPackageDetailsService>();
        services.AddSingleton<IPackageDetailsProvider, PackageDetailsProvider>();
        services.AddSingleton<IDetailsImageService, DetailsImageService>();
    }

    private static void RegisterPackageServices(ServiceCollection services)
    {
        WinProvision.PackageManagement.PackageDiagnostics.Configure(WinProvisionLog.Write, WinProvisionLog.WriteComServerInfo);
        WinProvisionLog.Write($"APP version={System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion} path={Environment.ProcessPath} elevator={GsudoElevationService.IsBundled}");
        services.AddSingleton<WinGetService>(p => new WinGetService(
            p.GetRequiredService<WingetExecutor>(), p.GetRequiredService<WinProvisionApiService>(),
            p.GetRequiredService<WingetBootstrapper>(),
            () => p.GetRequiredService<InstallationPreferencesService>().PreferredMethod,
            preferredUpdateMethod: () => p.GetRequiredService<InstallationPreferencesService>().PreferredUpdateMethod));
        services.AddSingleton(p => new WinProvision.PackageManagement.ApplicationEngine(
            p.GetRequiredService<WinGetService>(), p.GetRequiredService<WingetExecutor>(), p.GetRequiredService<WinProvisionApiService>(),
            p.GetRequiredService<WingetBootstrapper>(), p.GetRequiredService<StoreService>(), WinProvisionLog.Write,
            p.GetRequiredService<OperationPreferencesService>(),
            p.GetRequiredService<PackageUninstallService>()));
        services.AddSingleton<IPackageBackend, PackageBackend>();
    }

    private void RegisterViewModels(ServiceCollection services)
    {
        services.AddSingleton<PresentationService>();
        services.AddSingleton<IPresentationService>(provider => provider.GetRequiredService<PresentationService>());
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<StorefrontViewModel>();
        services.AddSingleton(p => new WindowsAppNotificationService(p.GetRequiredService<OperationPreferencesService>()) { SuppressNotifications = IsSmokeTest, Log = WinProvisionLog.Write });
        services.AddSingleton<CollectionsViewModel>();
        services.AddSingleton<ProvisioningViewModel>();
        services.AddSingleton<OfficeViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<IBackgroundUpdateStartupService>(p => new BackgroundUpdateStartupService(p.GetRequiredService<ApplicationPreferencesService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundUpdateStartupService>.Instance));
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<InstalledPackagesViewModel>();
        services.AddSingleton<AppDetailsViewModel>();
        services.AddSingleton<Controls.AppDetailsOverlay>();
        services.AddSingleton<OperationsViewModel>();
        services.AddSingleton<MoreViewModel>();
        services.AddSingleton<WinUiBackupSyncService>(provider => new WinUiBackupSyncService(
            provider.GetRequiredService<CollectionsViewModel>(),
            provider.GetRequiredService<IAccountBackupService>(),
            provider.GetRequiredService<ApplicationPreferencesService>(),
            enableAutomaticSync: !IsSmokeTest));
        services.AddSingleton<AccountSyncViewModel>();
    }

    private static void RegisterViews(ServiceCollection services)
    {
        services.AddTransient<SettingsPage>();
        services.AddTransient<HomePage>();
        services.AddTransient<CollectionsPage>();
        services.AddTransient<UpdatesPage>();
        services.AddTransient<InstalledPackagesPage>();
        services.AddTransient<OfficePage>();
        services.AddTransient<ProvisioningPage>();
        services.AddTransient<MorePage>();
        services.AddTransient<AccountSyncPage>();
    }
}
