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
            var engine = services.GetRequiredService<InstallationPreferencesService>().PreferredWinGetEngine;

            _window.RootElement.RequestedTheme = ElementTheme.Dark;
            await SmokeTest.CaptureAsync(_window.RootElement, "updates-real.png", directory);
            File.WriteAllText(Path.Combine(directory, "result.json"), AotJson.Serialize(new Dictionary<string, object?>
            {
                ["success"] = true,
                ["mode"] = WinGetFactoryHelper.Mode.ToString(),
                ["enginePreference"] = engine.ToString(),
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
