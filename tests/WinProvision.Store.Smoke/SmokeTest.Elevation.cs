using Microsoft.UI.Xaml;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Views;

namespace WinProvision.Store;

internal static partial class SmokeTest
{
    private static async Task VerifyElevationSettingsPageAsync(MainWindow window, IServiceProvider services)
    {
        window.Navigate("settings");
        await Task.Delay(150);
        Check(window.CurrentPage is SettingsPage, "Elevator: Configurações abre a página real");
        var settings = (SettingsPage)window.CurrentPage!;
        settings.NavigateToCategory("Elevator");
        await Task.Delay(100);
        await settings.ViewModel.RefreshElevatorCommand.ExecuteAsync(null);

        ElevationSettingOption[] options = settings.ViewModel.ElevationSettingGroups
            .SelectMany(group => group.Settings)
            .ToArray();
        string activePolicy = settings.ViewModel.GetActiveElevationPolicyText();
        Check(options.Length > 0
            && options.All(option => option.Definition.Kind == "preference" && option.ShowToggle)
            && activePolicy.Contains("Proteções fixas", StringComparison.Ordinal)
            && !activePolicy.Contains("assinatura digital", StringComparison.OrdinalIgnoreCase),
            "Elevator: página mostra preferências funcionais e mantém proteções na inspeção");
        Check(options.Where(option => option.Definition.Kind == "preference")
                .All(option => option.ShowToggle && option.IsToggleEnabled),
            "Elevator: preferências do catálogo usam o mesmo renderizador");
        Check(settings.ViewModel.ElevatorNotices.Count > 0,
            "Elevator: aviso do topo é projetado pelo catálogo de avisos");
        var lanEntry = new LanInstallerEntry
        {
            File = "synthetic.exe",
            Sha256 = new string('A', 64),
            PackageId = "synthetic.package",
            Version = "1.0",
            InstallerType = "nullsoft",
            SilentArgs = "/external",
            SilentSource = "manifest"
        };
        var cachedInstaller = new LanCachedInstaller(
            new LanInstallerAvailability(lanEntry, true, null),
            Path.Combine(Path.GetTempPath(), lanEntry.File),
            new LanInstallerDetection(LanInstallerKind.Nsis, "nullsoft", "NSIS"));
        var readOnlyArguments = new LanInstallerOptionViewModel(
            cachedInstaller, new LanInstallerSecurityInfo(lanEntry.Sha256, "Não verificada.", false),
            allowManualArguments: false, allowExternalArguments: false);
        readOnlyArguments.SilentArguments = "/manual";
        Check(!readOnlyArguments.CanEditArguments
            && readOnlyArguments.SilentArguments == "/S"
            && readOnlyArguments.CanInstall,
            "Elevator: preferência desligada bloqueia edição manual e ignora argumentos externos no Cliente LAN");
        var syntheticDefinition = new ElevationSettingDefinition(
            "synthetic-dangerous", "synthetic-group", "preference", "dangerous", false,
            null, null, true, true, "Título sintético", "Descrição sintética", "Risco sintético");
        var syntheticOption = new ElevationSettingOption(
            syntheticDefinition, "Preferência sintética", "Descrição sintética", "Aviso sintético",
            null, false, new Dictionary<string, bool>());
        await settings.ViewModel.ChangeElevationPreferenceAsync(syntheticOption, true);
        int confirmationCount = services.GetRequiredService<IPresentationService>() is SmokePresentationService smoke
            ? smoke.ConfirmCallCount
            : -1;
        Check(syntheticDefinition.RequiresConfirmation && confirmationCount > 0 && syntheticOption.IsEnabled,
            $"Elevator: preferência perigosa sintética confirma e persiste sem chave especial (calls={confirmationCount})");

        string preferencesPath = services.GetRequiredService<ElevationPreferencesService>().FilePath;
        using (new FileStream(preferencesPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await settings.ViewModel.ChangeElevationPreferenceAsync(syntheticOption, false);
        Check(syntheticOption.IsEnabled
            && new ElevationPreferencesService(preferencesPath).GetBoolean(syntheticDefinition.Key, false),
            "Elevator: falha de gravação reverte o toggle e preserva o valor persistido");

        await CaptureAsync(window.RootElement, "settings-elevator-Dark.png");
        settings.GoBack();
        window.Navigate("discover");
        await Task.Delay(100);
    }
}
