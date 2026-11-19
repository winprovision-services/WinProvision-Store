using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;
using WinProvision.Elevation;
using WinProvision.Providers.WinGet.Cli.Client;
using WinProvision.Providers.WinGet.Cli.Contracts;
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
        ServiceProvider provider = services.BuildServiceProvider();
        ElevationRuntime.Configure(provider.GetRequiredService<IElevatedRunner>());
        return provider;
    }

    private void RegisterCoreServices(ServiceCollection services)
    {
        services.AddSingleton(AppLogRuntime.Settings);
        services.AddSingleton<IAppLog>(_ => AppLogRuntime.Log);
        services.AddSingleton<ManagerTaskLogStore>();
        services.AddSingleton<IManagerTaskLogStore>(provider => provider.GetRequiredService<ManagerTaskLogStore>());
        services.AddSingleton<AppLogTaskExecutor>();
        services.AddSingleton<IUiDispatcher>(new UiDispatcher(DispatcherQueue.GetForCurrentThread()));
        services.AddSingleton<NotificationHistoryStore>();
        services.AddSingleton<ElevationPreferencesService>();
        services.AddSingleton(provider => GsudoRunnerOptions.BundledDefault() with
        {
            DiagnosticLog = WinProvisionLog.Write,
            IsElevationAllowed = () => provider.GetRequiredService<ElevationPreferencesService>().AllowElevation
        });
        services.AddSingleton<IElevationPolicy>(_ => new ElevationPathPolicy(WinProvisionLog.Write));
        services.AddSingleton<DirectRunner>();
        services.AddSingleton<GsudoCacheController>();
        services.AddSingleton<GsudoRunner>();
        services.AddSingleton<IElevatedRunner>(provider => new ElevationRunner(
            provider.GetRequiredService<IElevationPolicy>(),
            provider.GetRequiredService<DirectRunner>(),
            provider.GetRequiredService<GsudoRunner>()));
        services.AddSingleton<IElevationCacheManager>(provider =>
            provider.GetRequiredService<IElevatedRunner>() as IElevationCacheManager
            ?? throw new InvalidOperationException("Runner sem controle do cache de elevação."));
        services.AddSingleton<NoticeCatalogService>();
        services.AddSingleton<NoticeDismissalStore>();
        services.AddSingleton(p => new ToastMessageService(
            DispatcherQueue.GetForCurrentThread(),
            windowsChannel: p.GetRequiredService<WindowsAppNotificationService>(),
            operationPreferences: p.GetRequiredService<OperationPreferencesService>(),
            historyStore: p.GetRequiredService<NotificationHistoryStore>(),
            isAppFocused: () => _window?.IsFocused == true));
        services.AddSingleton<INotificationSink>(p => p.GetRequiredService<ToastMessageService>());
        services.AddSingleton<HttpClient>(_ => NetworkClientFactory.Create(TimeSpan.FromSeconds(30)));
        services.AddSingleton<StoreService>(provider => new StoreService(
            provider.GetRequiredService<HttpClient>(),
            provider.GetRequiredService<IconService>()));
        services.AddSingleton<IStoreCatalog>(provider => provider.GetRequiredService<StoreService>());
        services.AddSingleton<WingetBootstrapper>();
        services.AddSingleton<IWinGetCliClient>(_ => new WinGetCliClient());
        services.AddSingleton<ChocolateyService>(provider =>
        {
            var chocolatey = new ChocolateyService(provider.GetRequiredService<HttpClient>(),
                NetworkClientFactory.Create(TimeSpan.FromMinutes(5), allowAutoRedirect: true),
                provider.GetRequiredService<AppLogTaskExecutor>());
            chocolatey.SetExecutablePathOverride(provider.GetRequiredService<ApplicationPreferencesService>().ChocolateyExecutablePath);
            return chocolatey;
        });
        services.AddSingleton<WingetExecutor>(provider => new WingetExecutor(
            provider.GetRequiredService<WingetBootstrapper>(),
            provider.GetRequiredService<OperationPreferencesService>(),
            provider.GetRequiredService<AppLogTaskExecutor>(),
            provider.GetRequiredService<IWinGetCliClient>()));
        services.AddSingleton<LanInstallersServerModule>();
        services.AddSingleton<IServerModule>(provider => provider.GetRequiredService<LanInstallersServerModule>());
        services.AddSingleton<IClientModuleHandler, LanInstallersClientModuleHandler>();
        services.AddSingleton<ILanTransportSecurityPolicy, LanHttpTransportSecurityPolicy>();
        services.AddSingleton<LanInstallService>(provider => new LanInstallService(
            provider.GetRequiredService<ILanTransportSecurityPolicy>(),
            provider.GetServices<IClientModuleHandler>(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinProvisionStore", "lan-cache"),
            diagnosticLog: WinProvisionLog.Write));
        services.AddSingleton<LanProvisioningService>();
        services.AddSingleton<IInstallerProcessStarter, InstallerProcessStarter>();
        services.AddSingleton<IInstallerElevationInspector, InstallerElevationInspector>();
        services.AddSingleton<WinProvisionApiService>(provider => new WinProvisionApiService(
            operationPreferences: provider.GetRequiredService<OperationPreferencesService>(),
            installerProcessStarter: provider.GetRequiredService<IInstallerProcessStarter>(),
            installerElevationInspector: provider.GetRequiredService<IInstallerElevationInspector>()));
        services.AddSingleton<IUninstallerProcessStarter, UninstallerProcessStarter>();
        services.AddSingleton<UninstallerEngineService>(provider => new UninstallerEngineService(
            provider.GetService<PowerShell7Bootstrapper>(), provider.GetService<WingetExecutor>(),
            provider.GetRequiredService<IUninstallerProcessStarter>()));
        services.AddSingleton<PackageMetricsService>();
        services.AddSingleton<IPackageMetricsService>(provider => provider.GetRequiredService<PackageMetricsService>());
        services.AddSingleton<PowerShell7Bootstrapper>();
        services.AddSingleton<PowerShellScriptAnalyzerService>();
        services.AddSingleton<OfflineCollectionSource>();
        services.AddSingleton<IOfflineCollectionSource>(provider => provider.GetRequiredService<OfflineCollectionSource>());
        services.AddSingleton<IOfflineChocolateyPackageSource, OfflineChocolateyPackageSource>();
        services.AddSingleton<IOfflineOfficeSource, OfflineOfficeSource>();
        services.AddSingleton<OfflineCollectionExportService>();
        services.AddSingleton<InstalledAppsService>();
        services.AddSingleton<OperationHistoryService>();
        services.AddSingleton<HistoryStore>(provider => provider.GetRequiredService<OperationHistoryService>());
        services.AddSingleton<OperationsPanelCatalog>();
        services.AddSingleton<IOperationsPanelCatalog>(provider => provider.GetRequiredService<OperationsPanelCatalog>());
        services.AddSingleton<OperationTextProvider>();
        services.AddSingleton<IOperationTextProvider>(provider => provider.GetRequiredService<OperationTextProvider>());
        services.AddTransient<IOperationCommandRegistry, OperationCommandRegistry>();
        services.AddSingleton<IOperationConditionEvaluator, OperationConditionEvaluator>();
        services.AddSingleton<OperationsQueueService>();
        services.AddSingleton<OperationPreferencesService>();
        services.AddSingleton<InstallationPreferencesService>();
        services.AddSingleton<LocalBackupService>();
        services.AddSingleton<GitHubBackupService>();
        services.AddSingleton<ICloudBackupProvider>(provider => provider.GetRequiredService<GitHubBackupService>());
        services.AddSingleton<IAccountBackupService, AccountBackupService>();
        services.AddSingleton<FeaturedCatalogService>();
        services.AddSingleton<IconService>();
        services.AddSingleton<WinGetManifestIconService>();
        services.AddSingleton<WinProvision.Core.Services.Indexing.MsStoreCatalogService>();
        services.AddSingleton<ProvisioningWallpaperCatalogService>();
        services.AddSingleton<OfficeDeploymentToolService>();
        services.AddSingleton<IOfficeCollectionInstaller, OfficeCollectionInstaller>();
        services.AddSingleton<OfficeInstalledProductsDetector>();
        services.AddSingleton<OfficeCatalogRemoteService>();
        services.AddSingleton<IDesktopIntegrationService, DesktopIntegrationService>();
        services.AddSingleton<IElevatorSettingsService, ElevatorSettingsService>();
        services.AddSingleton<ICacheSettingsService, CacheSettingsService>();
        services.AddSingleton<InstalledIconResolver>();
        services.AddSingleton<IInstalledPackagesService, InstalledPackagesService>();
        services.AddSingleton<ApplicationPreferencesService>();
        services.AddSingleton<PackageVisualPreferencesService>();
        services.AddSingleton<ApplicationThemeService>();
        services.AddSingleton<CacheService>();
        services.AddSingleton(_ => new AppUpdateService(
            "winprovision-services/WinProvision-Store",
            "WinProvision.Store-Setup.exe",
            "71B03A9D-2C64-4E81-BF6D-3A90257F1C49"));
        services.AddSingleton<IUpdatesIgnoreStore, UpdatesIgnoreStore>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<IManualWinGetCommandService, ManualWinGetCommandService>();
        services.AddSingleton<PackageUninstallService>();
        services.AddSingleton<PackageInstallOptionsService>();
        services.AddSingleton<WingetSourcesService>();
        services.AddSingleton<IPackageManagerSettingsAdapter, WingetPackageManagerSettingsAdapter>();
        services.AddSingleton<IPackageManagerSettingsAdapter, ChocolateyPackageManagerSettingsAdapter>();
        services.AddSingleton<PackageManagerSettingsAdapterRegistry>();
        services.AddSingleton<PackageScreenSourceRegistry>();
        services.AddSingleton<WingetPackageDetailsService>();
        services.AddSingleton<IPackageDetailsAdapter, ChocolateyPackageDetailsAdapter>();
        services.AddSingleton<IPackageDetailsAdapter, MicrosoftStorePackageDetailsAdapter>();
        services.AddSingleton<IPackageDetailsAdapter, CuratedPackageDetailsAdapter>();
        services.AddSingleton<IPackageDetailsAdapter, WingetPackageDetailsAdapter>();
        services.AddSingleton<IPackageDetailsAdapter, GenericPackageDetailsAdapter>();
        services.AddSingleton<IPackageDetailsProvider, PackageDetailsProvider>();
        services.AddSingleton<IDetailsImageService, DetailsImageService>();
    }

    private static void RegisterPackageServices(ServiceCollection services)
    {
        WinProvision.PackageManagement.PackageDiagnostics.Configure(WinProvisionLog.Write, WinProvisionLog.WriteComServerInfo);
        WinProvisionLog.Write($"APP version={System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).FileVersion} path={Environment.ProcessPath} elevator={ElevationRuntime.CacheManager.IsHelperPresent}");
        services.AddSingleton<WinGetService>(p => new WinGetService(
            p.GetRequiredService<WingetExecutor>(),
            p.GetRequiredService<WingetBootstrapper>(),
            preferredEngine: () => p.GetRequiredService<InstallationPreferencesService>().PreferredWinGetEngine,
            taskExecutor: p.GetRequiredService<AppLogTaskExecutor>()));
        services.AddSingleton(p => new WinProvision.PackageManagement.ApplicationEngine(
            p.GetRequiredService<WinGetService>(), p.GetRequiredService<WingetExecutor>(), p.GetRequiredService<WinProvisionApiService>(),
            p.GetRequiredService<WingetBootstrapper>(), WinProvisionLog.Write,
            p.GetRequiredService<OperationPreferencesService>(),
            preferredEngine: () => p.GetRequiredService<InstallationPreferencesService>().PreferredWinGetEngine));
        services.AddSingleton<IPackageBackend, PackageBackend>();
    }

    private void RegisterViewModels(ServiceCollection services)
    {
        services.AddSingleton<PresentationService>();
        services.AddSingleton<IPresentationService>(provider => provider.GetRequiredService<PresentationService>());
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<StorefrontViewModel>();
        services.AddSingleton(p => new WindowsAppNotificationService()
        { SuppressNotifications = IsSmokeTest, Log = WinProvisionLog.Write });
        services.AddSingleton<CollectionsViewModel>();
        services.AddSingleton<ProvisioningViewModel>();
        services.AddSingleton<OfficeViewModel>();
        services.AddSingleton<OfficePageCatalogService>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<IBackgroundUpdateStartupService>(p => new BackgroundUpdateStartupService(p.GetRequiredService<ApplicationPreferencesService>(), Microsoft.Extensions.Logging.Abstractions.NullLogger<BackgroundUpdateStartupService>.Instance));
        services.AddSingleton<UpdatesViewModel>();
        services.AddSingleton<UpdatesPackageScreenProvider>();
        services.AddSingleton<InstalledPackagesViewModel>();
        services.AddSingleton<InstalledPackageScreenProvider>();
        services.AddSingleton(p => new PackageScreenProviderRegistration("installed", p.GetRequiredService<InstalledPackageScreenProvider>));
        services.AddSingleton(p => new PackageScreenProviderRegistration("updates", p.GetRequiredService<UpdatesPackageScreenProvider>));
        services.AddSingleton<PackageScreenProviderRegistry>();
        services.AddSingleton<CollectionsPackageScreenProvider>();
        services.AddSingleton<DiscoverPackageScreenProvider>();
        services.AddSingleton(p => new PackageScreenProviderRegistration("discover", p.GetRequiredService<DiscoverPackageScreenProvider>));
        services.AddSingleton(p => new PackageScreenProviderRegistration("collections", p.GetRequiredService<CollectionsPackageScreenProvider>));
        services.AddSingleton<AppDetailsViewModel>();
        services.AddSingleton<Controls.AppDetailsOverlay>();
        services.AddSingleton<OperationsViewModel>();
        services.AddSingleton<OperationHistoryViewModel>();
        services.AddSingleton<PackageManagersViewModel>();
        services.AddSingleton<WinUiBackupSyncService>();
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
        services.AddTransient<OfficePreferencesPage>();
        services.AddTransient<ProvisioningPage>();
        services.AddTransient<OperationHistoryPage>();
        services.AddTransient<AccountSyncPage>();
        services.AddTransient<PackageManagersPage>();
    }
}
