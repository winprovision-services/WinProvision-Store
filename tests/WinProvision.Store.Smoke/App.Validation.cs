using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Views;
namespace WinProvision.Store;
public partial class App
{
    partial void ConfigureValidationDebug()
    {
        if (!IsSmokeTest) return;
        DebugSettings.IsBindingTracingEnabled = true;
        DebugSettings.BindingFailed += (_, args) => SmokeTest.BindingErrors.Add(args.Message);
    }
    partial void ConfigureValidationServices(ServiceCollection services)
    {
        if (IsSmokeTest)
        {
            var historyPath = Path.Combine(SmokeTest.OutputDirectory, "operation-history-test.json");
            if (File.Exists(historyPath)) File.Delete(historyPath);
            services.AddSingleton<OperationHistoryService>(_ => new OperationHistoryService(historyPath));
            services.AddSingleton(new OperationPreferencesService(Path.Combine(SmokeTest.OutputDirectory, "operation-preferences-test.json")));
            var testCollections = Path.Combine(SmokeTest.OutputDirectory, "collections-test.json");
            if (File.Exists(testCollections)) File.Delete(testCollections);
            var testPreferences = Path.Combine(SmokeTest.OutputDirectory,"updates-preferences-test.json");
            if(File.Exists(testPreferences)) File.Delete(testPreferences);
            services.AddSingleton<ApplicationPreferencesService>(_=>new ApplicationPreferencesService(testPreferences,false));
            services.AddSingleton<IBackgroundUpdateStartupService, SmokeUpdateStartup>();
            services.AddSingleton<IElevatorSettingsService, SmokeElevatorSettings>();
            services.AddSingleton<ICacheSettingsService, SmokeCacheSettings>();
            services.AddSingleton<IUpdatesIgnoreStore, SmokeIgnoredUpdates>();
            services.AddSingleton<IInstalledPackagesService, SmokePackageBackend.SmokeInstalledPackagesService>();
            services.AddSingleton<IPackageBackend, SmokePackageBackend>();
            services.AddSingleton<IClipboardService, SmokeClipboardService>();
            services.AddSingleton<IPackageDetailsProvider, SmokeDetailsProvider>();
            services.AddSingleton<IDetailsImageService, SmokeDetailsImages>();
            services.AddSingleton<IPresentationService, SmokePresentationService>();
            var testOptions = Path.Combine(SmokeTest.OutputDirectory, "package-options-test.json");
            if (File.Exists(testOptions)) File.Delete(testOptions);
            services.AddSingleton<PackageInstallOptionsService>(_ => new PackageInstallOptionsService(testOptions));
            services.AddSingleton<OperationsQueueService>(_ => new OperationsQueueService());
            services.AddSingleton<IAccountBackupService, SmokeAccountBackupService>();
            services.AddSingleton<CollectionsViewModel>(provider => new CollectionsViewModel(
                provider.GetRequiredService<IPackageBackend>(),
                provider.GetRequiredService<IPresentationService>(),
                testCollections,
                officeInstaller: provider.GetRequiredService<IOfficeCollectionInstaller>()));
            services.AddSingleton<IOfficeCollectionInstaller, SmokePackageBackend.SmokeOfficeCollectionInstaller>();
        }
    }
    partial void TryLaunchValidation(ref bool handled)
    {
        if (Environment.GetCommandLineArgs().Contains("--probe-home"))
        {
            _backgroundUpdateTask = RunHomeProbeAsync();
            handled = true;
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--probe-inventory"))
        {
            _backgroundUpdateTask = RunInventoryProbeAsync();
            _ = _backgroundUpdateTask.ContinueWith(task => WinProvisionLog.Error(task.Exception!, "InventoryProbeTask"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            handled = true;
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--probe-com"))
        {
            _backgroundUpdateTask = RunComProbeAsync();
            _ = _backgroundUpdateTask.ContinueWith(
                task => WinProvisionLog.Error(task.Exception!, "ComProbeTask"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            handled = true;
            return;
        }
    }
    partial void ConfigureValidationWindow()
    {
        if (IsSmokeTest) _window!.RootElement.Loaded += RunSmokeTest;
    }

    private async Task RunHomeProbeAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "home-probe-results");
        Directory.CreateDirectory(directory);
        int code = 1;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            _window = new MainWindow(_services!);
            _window.Activate();
            var storefront = _services!.GetRequiredService<HomeViewModel>().Storefront;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && (_window.CurrentPage is not HomePage page
                || storefront.SelectedBanner is null || ((Microsoft.UI.Xaml.Controls.Image)page.FindName("HeroBannerImage")).Source is null))
                await Task.Delay(100);
            if (_window.CurrentPage is not HomePage home || storefront.SelectedBanner is null
                || ((Microsoft.UI.Xaml.Controls.Image)home.FindName("HeroBannerImage")).Source is null)
                throw new InvalidOperationException("O banner real não ficou disponível: " + storefront.StatusText);
            double bannerSeconds = watch.Elapsed.TotalSeconds;
            foreach (var theme in new[] { ElementTheme.Dark, ElementTheme.Light })
            {
                _window.RootElement.RequestedTheme = theme;
                await Task.Delay(250);
                await SmokeTest.CaptureAsync(_window.RootElement, $"home-real-{theme}.png", directory);
            }
            _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 800));
            await Task.Delay(300);
            await SmokeTest.CaptureAsync(_window.RootElement, "home-real-compact.png", directory);
            File.WriteAllText(Path.Combine(directory, "result.json"), AotJson.Serialize(new Dictionary<string, object?>
            {
                ["success"] = true, ["bannerSeconds"] = bannerSeconds, ["bannerId"] = storefront.SelectedBanner.Id,
                ["banners"] = storefront.StoreBannerApps.Select(app => app.Id).ToArray(),
                ["popular"] = storefront.PopularApps.Select(app => app.Id).ToArray(),
                ["note"] = "Catálogo e imagem reais; nenhuma instalação solicitada."
            }));
            code = 0;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "HomeProbe");
            File.WriteAllText(Path.Combine(directory, "result.json"), AotJson.Serialize(new Dictionary<string, object?>
                { ["success"] = false, ["error"] = exception.ToString() }));
        }
        finally
        {
            if (_services is not null) await _services.DisposeAsync();
            Environment.ExitCode = code;
            Exit();
        }
    }
    private async Task RunInventoryProbeAsync()
    {
        int code = 0;
        string directory = Path.Combine(AppContext.BaseDirectory, "inventory-probe-results");
        Directory.CreateDirectory(directory);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            var packages = await _services!.GetRequiredService<IInstalledPackagesService>().ListAsync(timeout.Token);
            if (packages.Count == 0) throw new InvalidOperationException("O inventário real não retornou aplicativos.");
            string? extracted = null;
            try
            {
                // Keep Windows' original path for MUI resources, with a unique cache identity.
                extracted = _services!.GetRequiredService<IconService>().ExtractIconResource(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shell32.dll"),
                    cacheIdentity: "inventory-probe-" + Guid.NewGuid().ToString("N"));
                if (extracted is null || !File.ReadAllBytes(extracted).Take(4).SequenceEqual(new byte[] { 137, 80, 78, 71 }))
                    throw new InvalidOperationException("A extração nativa do ícone não produziu PNG válido.");
            }
            finally
            {
                if (extracted is not null && File.Exists(extracted)) File.Delete(extracted);
            }
            var result = new Dictionary<string, object?> { ["success"] = true, ["count"] = packages.Count,
                ["nativeIconExtraction"] = true,
                ["icons"] = packages.Count(package => !string.IsNullOrWhiteSpace(package.IconUrl)),
                ["localIcons"] = packages.Count(package => File.Exists(package.IconUrl)),
                ["uninstallMetadata"] = packages.Count(package => !string.IsNullOrWhiteSpace(package.UninstallString)),
                ["note"] = "Inventário real e extração de ícones; nenhuma instalação ou remoção solicitada." };
            File.WriteAllText(Path.Combine(directory, "result.json"), AotJson.Serialize(result));
        }
        catch (Exception exception)
        {
            code = 1;
            WinProvisionLog.Error(exception, "InventoryProbe");
            File.WriteAllText(Path.Combine(directory, "result.json"), AotJson.Serialize(new Dictionary<string, object?>
                { ["success"] = false, ["error"] = exception.ToString() }));
        }
        finally
        {
            if (_services is not null) await _services.DisposeAsync();
            Environment.ExitCode = code;
            Exit();
        }
    }

    private async Task RunComProbeAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "com-probe-results");
        Directory.CreateDirectory(directory);
        int exitCode = 1;
        try
        {
            var manager = WinGetFactoryHelper.CreateResilientPackageManager();
            var install = WinGetFactoryHelper.CreateInstallOptions();
            install.AcceptPackageAgreements = true;
            install.AllowedArchitectures.Add(Windows.System.ProcessorArchitecture.X64);
            _ = WinGetFactoryHelper.CreateUninstallOptions();
            _ = WinGetFactoryHelper.CreateCompositePackageCatalogOptions();
            _ = WinGetFactoryHelper.CreateFindPackagesOptions();
            _ = WinGetFactoryHelper.CreatePackageMatchFilter();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var updates = await _services!.GetRequiredService<WinGetService>().GetUpgradablePackagesAsync(cancellationToken: timeout.Token);
            File.WriteAllText(Path.Combine(directory, "result.json"), AotJson.Serialize(new Dictionary<string, object?>
            {
                ["success"] = true, ["mode"] = WinGetFactoryHelper.Mode.ToString(),
                ["strategy"] = WinGetFactoryHelper.CurrentStrategy.ToString(),
                ["version"] = manager.Version, ["activationClasses"] = 6,
                ["cliFallbackAllowed"] = WinGetFactoryHelper.CliFallbackAllowed, ["updateCount"] = updates.Count,
                ["note"] = "Ativação, opções e consulta COM reais; nenhuma instalação ou remoção solicitada."
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            exitCode = 0;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "ComProbe");
            File.WriteAllText(Path.Combine(directory, "result.json"), AotJson.Serialize(new Dictionary<string, object?>
            {
                ["success"] = false, ["error"] = exception.ToString(), ["hresult"] = $"0x{exception.HResult:X8}"
            }));
        }
        finally
        {
            if (_services is not null) await _services.DisposeAsync();
            Environment.ExitCode = exitCode;
            Exit();
        }
    }
    private async void RunSmokeTest(object sender, RoutedEventArgs args)
    {
        _window!.RootElement.Loaded -= RunSmokeTest;
        try { await SmokeTest.RunAsync(_window, _services!); }
        catch (Exception exception) { WinProvisionLog.Error(exception, "SmokeTest"); SmokeTest.WriteFailure(exception); }
        finally
        {
            // Fixtures have no installer worker to observe cancellation after a failed assertion.
            foreach (var operation in _services!.GetRequiredService<OperationsQueueService>().Operations)
                if (operation.State is OperationState.Queued or OperationState.Running)
                    operation.State = OperationState.Canceled;
            await ShutdownAsync();
            _window.Close();
        }
    }
}
