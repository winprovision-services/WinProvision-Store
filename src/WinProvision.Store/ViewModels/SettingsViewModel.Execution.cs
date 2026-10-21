using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    private OperationPreferencesService _operationPreferences = new();
    [ObservableProperty] public partial bool NotifyUpdates { get; set; } = true;
    [ObservableProperty] public partial bool NotifyErrors { get; set; } = true;
    [ObservableProperty] public partial bool NotifySuccess { get; set; } = true;
    [ObservableProperty] public partial bool NotifyBatchSummary { get; set; } = true;
    [ObservableProperty] public partial string ParallelOperations { get; set; } = "1";
    [ObservableProperty] public partial string ParallelDownloads { get; set; } = "3";
    [ObservableProperty] public partial string DownloadDirectory { get; set; } = "";
    [ObservableProperty] public partial string InstallerNames { get; set; } = "PackageVersion";
    public IRelayCommand SaveNotificationsCommand => field ??= new RelayCommand(SaveNotifications);
    public IRelayCommand SaveExecutionOptionsCommand => field ??= new RelayCommand(SaveExecutionOptions);
    public IRelayCommand DefaultDownloadDirectoryCommand => field ??= new RelayCommand(() => DownloadDirectory = "");
    public IAsyncRelayCommand PickDownloadDirectoryCommand => field ??= new AsyncRelayCommand(PickDownloadDirectoryAsync);
    public void LoadExecutionOptions()
    {
        var options = _operationPreferences.Load();
        NotifyUpdates = options.NotifyUpdates; NotifyErrors = options.NotifyErrors;
        NotifySuccess = options.NotifySuccess; NotifyBatchSummary = options.NotifyBatchSummary;
        ParallelOperations = options.ParallelOperations.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ParallelDownloads = options.ParallelDownloads.ToString(System.Globalization.CultureInfo.InvariantCulture);
        DownloadDirectory = options.DownloadDirectory ?? ""; InstallerNames = options.InstallerNames.ToString();
    }
    private void SaveNotifications() => Run(() => _operationPreferences.Save(_operationPreferences.Load() with
    {
        NotifyUpdates = NotifyUpdates, NotifyErrors = NotifyErrors, NotifySuccess = NotifySuccess, NotifyBatchSummary = NotifyBatchSummary
    }), "Categorias de notificação salvas. As permissões do Windows também se aplicam.");
    private void SaveExecutionOptions()
    {
        if (!int.TryParse(ParallelOperations, out int operations) || !int.TryParse(ParallelDownloads, out int downloads)
            || !Enum.TryParse<InstallerNameStyle>(InstallerNames, out var style))
        { StatusText = "Selecione limites e formato de nome válidos."; return; }
        try
        {
            _operationPreferences.Save(_operationPreferences.Load() with { ParallelOperations = operations,
                ParallelDownloads = downloads, InstallerNames = style, WarnInstallerHostChange = WarnInstallerHostChange,
                DownloadDirectory = string.IsNullOrWhiteSpace(DownloadDirectory) ? null : DownloadDirectory.Trim() });
            StatusText = "Opções salvas. Os novos limites não interrompem operações em andamento.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { Services.WinProvisionLog.Warning(ex, "Settings.Execution.Save"); StatusText = "Não foi possível salvar. Verifique os limites e a pasta escolhida."; }
    }
    private async Task PickDownloadDirectoryAsync()
    {
        try { if (await _presentation.PickFolderAsync() is { } folder) DownloadDirectory = folder; }
        catch (Exception ex) { Services.WinProvisionLog.Warning(ex, "Settings.Execution.PickFolder"); StatusText = "Não foi possível selecionar a pasta."; }
    }
}
