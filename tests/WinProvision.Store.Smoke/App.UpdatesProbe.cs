using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Views;

namespace WinProvision.Store;

public partial class App
{
    private async Task RunUpdatesProbeAsync()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "updates-probe-results");
        Directory.CreateDirectory(directory);
        int exitCode = 1;
        try
        {
            var services = _services ?? throw new InvalidOperationException("Os serviços não foram inicializados.");
            if (services.GetRequiredService<IPackageBackend>() is not PackageBackend)
                throw new InvalidOperationException("O diagnóstico exige os serviços reais de pacotes.");

            _window = new MainWindow(services);
            var rootLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _window.RootElement.Loaded += (_, _) => rootLoaded.TrySetResult();
            _window.Activate();
            await rootLoaded.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var timer = Stopwatch.StartNew();
            _window.Navigate("updates");
            var page = _window.CurrentPage as UpdatesPage
                ?? throw new InvalidOperationException("A navegação não abriu Atualizações.");
            var pageLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            page.Loaded += (_, _) => pageLoaded.TrySetResult();
            if (page.IsLoaded) pageLoaded.TrySetResult();
            await pageLoaded.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await page.InitialContentReady.WaitAsync(TimeSpan.FromSeconds(65));

            var model = services.GetRequiredService<UpdatesViewModel>();
            EnsureUpdatesFinished(model);
            double firstSeconds = timer.Elapsed.TotalSeconds;
            timer.Restart();
            await model.CheckUpdatesAsync().WaitAsync(TimeSpan.FromSeconds(65));
            EnsureUpdatesFinished(model);
            double reloadSeconds = timer.Elapsed.TotalSeconds;
            var updateMethod = services.GetRequiredService<InstallationPreferencesService>().PreferredUpdateMethod;
            var plans = new List<Dictionary<string, object?>>();
            string? verifyInstaller = Environment.GetCommandLineArgs().FirstOrDefault(argument => argument.StartsWith("--probe-verify-installer=", StringComparison.Ordinal))?.Split('=', 2)[1];
            bool installerVerified = false;
            if (updateMethod == WinProvision.Core.Models.PackageUpdateMethod.WinProvisionApi)
            {
                foreach (var package in model.Packages)
                {
                    var plan = await services.GetRequiredService<WinGetService>().PrepareApiUpdateAsync(package.Id, package.AvailableVersion);
                    var report = new Dictionary<string, object?>
                    {
                        ["id"] = package.Id, ["target"] = plan.Manifest.Version,
                        ["scope"] = plan.InstalledPackage.InstalledScope,
                        ["architecture"] = plan.Installer.Architecture, ["installerType"] = plan.Installer.Type,
                        ["url"] = plan.Installer.Url, ["sha256"] = plan.Installer.Sha256
                    };
                    if (package.Id.Equals(verifyInstaller, StringComparison.OrdinalIgnoreCase))
                    {
                        string extension = Path.GetExtension(new Uri(plan.Installer.Url).AbsolutePath);
                        string destination = Path.Combine(directory, $"verified-{Guid.NewGuid():N}{extension}");
                        using var downloadTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                        await services.GetRequiredService<WinProvisionApiService>().DownloadVerifiedInstallerAsync(
                            plan.Installer, destination, ct: downloadTimeout.Token);
                        report["downloadVerified"] = true;
                        report["downloadBytes"] = new FileInfo(destination).Length;
                        installerVerified = true;
                    }
                    plans.Add(report);
                }
            }
            if (!string.IsNullOrWhiteSpace(verifyInstaller) && !installerVerified)
                throw new InvalidOperationException("O instalador solicitado não foi oferecido e validado pela API.");

            _window.RootElement.RequestedTheme = ElementTheme.Dark;
            await SmokeTest.CaptureAsync(_window.RootElement, "updates-real.png", directory);
            File.WriteAllText(Path.Combine(directory, "result.json"), AotJson.Serialize(new Dictionary<string, object?>
            {
                ["success"] = true,
                ["mode"] = WinGetFactoryHelper.Mode.ToString(),
                ["updateMethod"] = updateMethod.ToString(),
                ["apiUpdatePlans"] = plans,
                ["registryInventory"] = updateMethod == WinProvision.Core.Models.PackageUpdateMethod.WinProvisionApi
                    ? InstalledAppsService.GetRegistryInventory().Select(package => new Dictionary<string, object?>
                    {
                        ["registryId"] = package.InstalledRegistryId, ["name"] = package.Name, ["version"] = package.CurrentVersion,
                        ["architecture"] = package.InstalledArchitecture, ["scope"] = package.InstalledScope
                    }).ToArray() : null,
                ["executable"] = Environment.ProcessPath,
                ["firstScanSeconds"] = firstSeconds,
                ["reloadSeconds"] = reloadSeconds,
                ["count"] = model.Packages.Count,
                ["loading"] = model.IsInitialLoading || model.IsScanning || model.IsBusy,
                ["packages"] = model.Packages.Select(package => new Dictionary<string, object?>
                {
                    ["id"] = package.Id, ["name"] = package.Name,
                    ["installedVersion"] = package.CurrentVersion,
                    ["availableVersion"] = package.AvailableVersion, ["source"] = package.UpdateSource,
                    ["sourceLabel"] = package.SourceLabel
                }).ToArray(),
                ["note"] = "Busca e recarga reais na interface; nenhuma instalação ou remoção solicitada."
            }));
            exitCode = 0;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Error(exception, "UpdatesProbe");
            File.WriteAllText(Path.Combine(directory, "result.json"), AotJson.Serialize(new Dictionary<string, object?>
                { ["success"] = false, ["error"] = exception.ToString() }));
        }
        finally
        {
            if (_services is not null) await _services.DisposeAsync();
            Environment.ExitCode = exitCode;
            Exit();
        }
    }

    private static void EnsureUpdatesFinished(UpdatesViewModel model)
    {
        if (model.HasError || model.IsBusy || model.IsScanning || model.IsInitialLoading)
            throw new InvalidOperationException($"A consulta real não concluiu corretamente: {model.StatusText} {model.ErrorMessage}");
    }
}
