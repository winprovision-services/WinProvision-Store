using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly WingetSourcesService _sources = new();
    [ObservableProperty] public partial string SourceName { get; set; } = "";
    [ObservableProperty] public partial string SourceUrl { get; set; } = "";
    [ObservableProperty] public partial string SourcesText { get; private set; } = "";
    [ObservableProperty] public partial bool WarnInstallerHostChange { get; set; } = true;

    public IAsyncRelayCommand RefreshSourcesCommand => field ??= new AsyncRelayCommand(RefreshSourcesAsync);
    public IAsyncRelayCommand AddSourceCommand => field ??= new AsyncRelayCommand(AddSourceAsync);
    public IAsyncRelayCommand RemoveSourceCommand => field ??= new AsyncRelayCommand(RemoveSourceAsync);
    public IAsyncRelayCommand ExportSettingsCommand => field ??= new AsyncRelayCommand(ExportSettingsAsync);
    public IAsyncRelayCommand ImportSettingsCommand => field ??= new AsyncRelayCommand(ImportSettingsAsync);

    public void LoadPortabilitySettings() => WarnInstallerHostChange = _operationPreferences.Load().WarnInstallerHostChange;

    private void SaveInstallerHostWarning() => Run(() =>
        _operationPreferences.Save(_operationPreferences.Load() with { WarnInstallerHostChange = WarnInstallerHostChange }),
        "A opção de aviso sobre mudança de domínio foi salva.");

    private async Task RefreshSourcesAsync(CancellationToken token)
    {
        try { SourcesText = string.Join(Environment.NewLine, await _sources.ListAsync(token)); StatusText = "Fontes do WinGet atualizadas."; }
        catch (Exception ex) when (ex is not OperationCanceledException) { WinProvisionLog.Warning(ex, "Settings.Sources.List"); StatusText = "Não foi possível listar as fontes do WinGet."; }
    }
    private async Task AddSourceAsync(CancellationToken token)
    {
        try { await _sources.AddAsync(SourceName, SourceUrl, token); SourceName = SourceUrl = ""; await RefreshSourcesAsync(token); StatusText = "Fonte WinGet adicionada."; }
        catch (Exception ex) when (ex is not OperationCanceledException) { WinProvisionLog.Warning(ex, "Settings.Sources.Add"); StatusText = ex.Message; }
    }
    private async Task RemoveSourceAsync(CancellationToken token)
    {
        try { await _sources.RemoveAsync(SourceName, token); await RefreshSourcesAsync(token); StatusText = "Fonte WinGet removida."; }
        catch (Exception ex) when (ex is not OperationCanceledException) { WinProvisionLog.Warning(ex, "Settings.Sources.Remove"); StatusText = ex.Message; }
    }
    private async Task ExportSettingsAsync(CancellationToken token)
    {
        try
        {
            string? path = await _presentation.PickSaveAsync("WinProvision-configuracoes", ".json");
            if (path is null) return;
            var payload = new PortableSettings(1, _operationPreferences.Load(), _preferences.ExportPortablePreferences());
            await AtomicFile.WriteAllTextAsync(path, JsonSerializer.Serialize(payload, PortableSettingsJson.Default.PortableSettings), token);
            StatusText = "Preferências de operações exportadas.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { WinProvisionLog.Warning(ex, "Settings.Export"); StatusText = "Não foi possível exportar as configurações."; }
    }
    private async Task ImportSettingsAsync(CancellationToken token)
    {
        try
        {
            string? path = await _presentation.PickOpenAsync();
            if (path is null) return;
            var payload = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token), PortableSettingsJson.Default.PortableSettings)
                ?? throw new InvalidDataException("O arquivo de configurações está vazio ou inválido.");
            if (payload.Version != 1 || payload.Operations is null || payload.Application is null) throw new InvalidDataException("Versão ou conteúdo de configurações não compatível.");
            payload.Operations.Validate();
            _preferences.ImportPortablePreferences(payload.Application);
            _operationPreferences.Save(payload.Operations);
            _themeService.SetTheme(payload.Application.Theme);
            SelectedThemeOption = ThemeOptions.First(option => option.Preference == payload.Application.Theme);
            SearchOnDemand = _preferences.SearchOnDemand;
            LoadExecutionOptions(); LoadPortabilitySettings(); OnPropertyChanged(nameof(UseViperOfficeDownload));
            ReloadScheduledMaintenance();
            StatusText = "Configurações importadas. As opções de inicialização foram atualizadas para esta conta do Windows.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { WinProvisionLog.Warning(ex, "Settings.Import"); StatusText = "Não foi possível importar. O arquivo precisa ser um JSON WinProvision compatível."; }
    }
}

public sealed record PortableSettings(int Version, OperationPreferences Operations, PortableApplicationPreferences Application);
[System.Text.Json.Serialization.JsonSerializable(typeof(PortableSettings))]
internal partial class PortableSettingsJson : System.Text.Json.Serialization.JsonSerializerContext;
