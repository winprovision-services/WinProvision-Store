using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    private OperationPreferencesService _operationPreferences = new();
    private ToastMessageService? _toastMessages;

    [ObservableProperty] public partial string NotificationLevel { get; set; } = nameof(WinProvision.Core.Services.NotificationLevel.All);
    [ObservableProperty] public partial bool BackgroundWindowsNotificationsEnabled { get; set; } = true;
    public ObservableCollection<NotificationHistoryEntry> NotificationHistory { get; } = [];
    public IReadOnlyList<SettingsCatalogGroupOption> OperationSettingsGroups { get; private set; } = [];
    public IRelayCommand ClearNotificationHistoryCommand => field ??= new RelayCommand(ClearNotificationHistory);
    public IRelayCommand SaveNotificationsCommand => field ??= new RelayCommand(SaveNotifications);

    public void LoadExecutionOptions(ToastMessageService? toastMessages = null)
    {
        if (toastMessages is not null && !ReferenceEquals(_toastMessages, toastMessages))
        {
            if (_toastMessages is not null)
                _toastMessages.HistoryChanged -= ToastMessages_HistoryChanged;
            _toastMessages = toastMessages;
            _toastMessages.HistoryChanged += ToastMessages_HistoryChanged;
        }

        OperationPreferences options = _operationPreferences.Load();
        NotificationLevel = options.NotificationLevel.ToString();
        BackgroundWindowsNotificationsEnabled = options.BackgroundWindowsNotificationsEnabled;
        SyncNotificationHistory();
        LoadOperationSettings();
    }

    public void LoadOperationSettings()
    {
        OperationPreferences preferences = _operationPreferences.Load();
        OperationSettingsGroups = _elevationCatalog.Catalog.GetGroups("operations")
            .Select(group =>
            {
                SettingsCatalogSettingOption[] settings = _elevationCatalog.Catalog.GetSettings(group.Key)
                    .Select(definition => new SettingsCatalogSettingOption(
                        definition,
                        _elevationCatalog.ResolveText(definition.TitleTextKey),
                        _elevationCatalog.ResolveText(definition.DescriptionTextKey),
                        definition.RiskWarningTextKey is null ? null : _elevationCatalog.ResolveText(definition.RiskWarningTextKey),
                        definition.StorageKey is null ? definition.DefaultTextValue : _operationPreferences.GetCatalogValue(definition.StorageKey),
                        _elevationCatalog.Text,
                        ResolveCatalogAction(definition.ActionKey)))
                    .ToArray();
                return new SettingsCatalogGroupOption(
                    _elevationCatalog.ResolveText(group.TitleTextKey),
                    group.HelpTextKey is null ? null : _elevationCatalog.ResolveText(group.HelpTextKey),
                    group.DetailsTitleTextKey is null ? null : _elevationCatalog.ResolveText(group.DetailsTitleTextKey),
                    group.DetailsTextKey is null ? null : _elevationCatalog.ResolveText(group.DetailsTextKey),
                    settings);
            })
            .ToArray();
        OnPropertyChanged(nameof(OperationSettingsGroups));
    }

    public async Task ApplyCatalogSettingAsync(SettingsCatalogSettingOption option, string value)
    {
        ArgumentNullException.ThrowIfNull(option);
        ArgumentNullException.ThrowIfNull(value);
        ElevationSettingDefinition definition = option.Definition;
        if (definition.PreferenceProviderId is not null)
        {
            await ApplyCatalogPreferenceProviderAsync(option, value);
            return;
        }
        if (!definition.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase)
            || !definition.AppliesImmediately || string.IsNullOrWhiteSpace(definition.StorageKey))
            return;

        string previous = option.Value;
        if (previous.Equals(value, StringComparison.Ordinal))
        {
            await RefreshCatalogOptionAsync(option, previous);
            return;
        }

        if (!IsCatalogValueValid(option, value))
        {
            await RefreshCatalogOptionAsync(option, previous);
            StatusText = "O valor selecionado não está disponível nesta configuração.";
            return;
        }

        try
        {
            if (option.IsFolder)
                ValidateDownloadFolder(value);

            if (RequiresCatalogConfirmation(definition, previous, value))
            {
                bool confirmed = await _presentation.ConfirmAsync(
                    option.Title,
                    option.RiskWarning ?? option.Description,
                    "Continuar");
                if (!confirmed)
                {
                    await RefreshCatalogOptionAsync(option, previous);
                    return;
                }
            }

            _operationPreferences.SetCatalogValue(definition.StorageKey, value);
            await RefreshCatalogOptionAsync(option, value);
            bool relaxesSecurity = IsSecurityRelaxation(definition, previous, value);
            _appLog.Write(AppLogLevel.Debug, relaxesSecurity ? "Security" : "App",
                "Settings.OperationPreferences", "Preferência de operação alterada.",
                eventName: "PreferenceChanged");
            StatusText = "Preferência aplicada.";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            await RefreshCatalogOptionAsync(option, previous);
            WinProvisionLog.Warning(exception, "Settings.OperationPreferences.Save");
            StatusText = option.IsFolder
                ? "Não foi possível usar essa pasta. Verifique se ela existe e permite gravação."
                : "Não foi possível salvar. A preferência anterior foi mantida.";
            if (_toastMessages is not null)
            {
                await _toastMessages.PublishAsync(new Notification(
                    Guid.NewGuid().ToString("N"),
                    NotificationCategories.BlockingError,
                    NotificationSeverity.Error,
                    "Não foi possível aplicar a configuração",
                    option.IsFolder
                        ? "A pasta escolhida não foi aplicada. Verifique se ela existe e permite gravação."
                        : "A preferência anterior foi mantida."));
            }
        }
    }

    public async Task PickCatalogFolderAsync(SettingsCatalogSettingOption option, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(option);
        string? folder;
        try
        {
            folder = await _presentation.PickFolderAsync().WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (folder is not null)
            await ApplyCatalogSettingAsync(option, folder);
    }

    public Task UseCatalogFolderDefaultAsync(SettingsCatalogSettingOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        return ApplyCatalogSettingAsync(option, option.Definition.DefaultTextValue);
    }

    private ICommand? ResolveCatalogAction(string? actionKey) => actionKey switch
    {
        "exportSettings" => ExportSettingsCommand,
        "importSettings" => ImportSettingsCommand,
        "clearImagesCache" => ClearImagesCacheCommand,
        "releaseImageMemory" => ReleaseImageMemoryCommand,
        "clearInstallerCache" => ClearInstallerCacheCommand,
        _ => null
    };

    private static bool IsCatalogValueValid(SettingsCatalogSettingOption option, string value) =>
        option.Control switch
        {
            "toggle" => bool.TryParse(value, out _),
            "combo" => option.Options.Any(choice => choice.Value.Equals(value, StringComparison.Ordinal)),
            "folder" => true,
            _ => false
        };

    private static bool RequiresCatalogConfirmation(ElevationSettingDefinition definition, string previous, string value)
    {
        if (!definition.RequiresConfirmation)
            return false;
        return definition.ConfirmationWhen switch
        {
            "disabled" => bool.TryParse(previous, out bool wasEnabled) && wasEnabled
                && bool.TryParse(value, out bool isEnabled) && !isEnabled,
            "enabled" or null => bool.TryParse(value, out bool isEnabled) && isEnabled
                && !previous.Equals(value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsSecurityRelaxation(ElevationSettingDefinition definition, string previous, string value) =>
        definition.Risk.Equals("elevated", StringComparison.OrdinalIgnoreCase)
        && definition.ConfirmationWhen == "disabled"
        && bool.TryParse(previous, out bool wasEnabled) && wasEnabled
        && bool.TryParse(value, out bool isEnabled) && !isEnabled;

    private async Task RefreshCatalogOptionAsync(SettingsCatalogSettingOption option, string value)
    {
        if (_lanDispatcher is null)
        {
            option.Value = value;
            option.RefreshBindings();
            return;
        }

        await _lanDispatcher.InvokeAsync(() =>
        {
            option.Value = value;
            option.RefreshBindings();
        });
    }

    private static void ValidateDownloadFolder(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        if (!Path.IsPathFullyQualified(value)
            || value.StartsWith(@"\\?\", StringComparison.Ordinal)
            || value.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Escolha uma pasta com caminho absoluto.", nameof(value));
        if (!Directory.Exists(value))
            throw new DirectoryNotFoundException("A pasta escolhida não existe.");

        string probe = Path.Combine(value, $".winprovision-access-{Guid.NewGuid():N}.tmp");
        using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.DeleteOnClose);
    }

    private void SaveNotifications()
    {
        if (!Enum.TryParse(NotificationLevel, ignoreCase: true, out WinProvision.Core.Services.NotificationLevel level)
            || !Enum.IsDefined(level))
        {
            StatusText = "Selecione um nível de notificação válido.";
            return;
        }

        Run(() => _operationPreferences.Save(_operationPreferences.Load() with
        {
            NotificationLevel = level,
            BackgroundWindowsNotificationsEnabled = BackgroundWindowsNotificationsEnabled
        }), "Preferências de notificação salvas. O modo Não incomodar do Windows também se aplica.");
    }

    private void ClearNotificationHistory()
    {
        try
        {
            _toastMessages?.ClearHistory();
            NotificationHistory.Clear();
            StatusText = "Histórico de notificações limpo.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Services.WinProvisionLog.Warning(exception, "Settings.Notifications.ClearHistory");
            StatusText = "Não foi possível limpar o histórico.";
        }
    }

    private void ToastMessages_HistoryChanged(object? sender, EventArgs args) => SyncNotificationHistory();

    private void SyncNotificationHistory()
    {
        NotificationHistory.Clear();
        if (_toastMessages is not null)
            foreach (NotificationHistoryEntry entry in _toastMessages.History)
                NotificationHistory.Add(entry);
    }
}
