using System.Windows.Input;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    public IReadOnlyList<SettingsCatalogGroupOption> GeneralSettingsGroups { get; private set; } = [];

    public void LoadGeneralSettings()
    {
        IReadOnlyList<ElevationSettingDefinition> definitions = _elevationCatalog.Catalog.Settings;
        var values = definitions
            .Where(definition => definition.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(definition => definition.Key, GetGeneralSettingValue, StringComparer.OrdinalIgnoreCase);
        GeneralSettingsGroups = _elevationCatalog.Catalog.GetGroups("general")
            .Select(group =>
            {
                ElevationSettingDefinition[] groupDefinitions =
                    _elevationCatalog.Catalog.GetSettings(group.Key).ToArray();
                SettingsCatalogSettingOption[] options = groupDefinitions
                    .Select(definition =>
                    {
                        var option = new SettingsCatalogSettingOption(
                            definition,
                            _elevationCatalog.ResolveText(definition.TitleTextKey),
                            _elevationCatalog.ResolveText(definition.DescriptionTextKey),
                            null,
                            values.GetValueOrDefault(definition.Key, definition.DefaultTextValue),
                            _elevationCatalog.Text,
                            ResolveGeneralCatalogAction(definition),
                            secondaryDescription: definition.RequiresUpdateCapability && !_updater.CanSelfUpdate
                                && definition.CapabilityDescriptionTextKey is { } capabilityTextKey
                                    ? _elevationCatalog.ResolveText(capabilityTextKey)
                                    : null);
                        option.RefreshPresentation(values, groupDefinitions);
                        return option;
                    })
                    .ToArray();

                return new SettingsCatalogGroupOption(
                    _elevationCatalog.ResolveText(group.TitleTextKey),
                    group.HelpTextKey is null ? null : _elevationCatalog.ResolveText(group.HelpTextKey),
                    group.DetailsTitleTextKey is null ? null : _elevationCatalog.ResolveText(group.DetailsTitleTextKey),
                    group.DetailsTextKey is null ? null : _elevationCatalog.ResolveText(group.DetailsTextKey),
                    options,
                    group.Icon);
            })
            .ToArray();

        OnPropertyChanged(nameof(GeneralSettingsGroups));
        RefreshGeneralUpdateAction();
    }

    public async Task ApplyGeneralCatalogSettingAsync(SettingsCatalogSettingOption option, string value)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (!option.IsEnabled || !option.Definition.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase)
            || !option.Definition.AppliesImmediately || string.IsNullOrWhiteSpace(option.Definition.StorageKey))
            return;

        if (!bool.TryParse(value, out bool enabled))
        {
            RefreshGeneralOption(option);
            StatusText = "O valor selecionado não está disponível nesta configuração.";
            return;
        }

        try
        {
            switch (option.Definition.StorageKey)
            {
                case "AutomaticAppUpdateChecksEnabled":
                    _preferences.SetAutomaticAppUpdateChecksEnabled(enabled);
                    break;
                case "IncludePrereleaseAppUpdates":
                    _preferences.SetIncludePrereleaseAppUpdates(enabled);
                    break;
                default:
                    throw new InvalidOperationException("A preferência declarada não tem um armazenamento compatível.");
            }

            RefreshGeneralOption(option);
            _appLog.Write(AppLogLevel.Debug, "App", "Settings.GeneralPreferences",
                "Preferência geral alterada.", eventName: "PreferenceChanged");
            StatusText = "Preferência aplicada.";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        {
            RefreshGeneralOption(option);
            WinProvisionLog.Warning(exception, "Settings.GeneralPreferences.Save");
            StatusText = "Não foi possível salvar. A preferência anterior foi mantida.";
            if (_toastMessages is not null)
            {
                await _toastMessages.PublishAsync(new Notification(
                    Guid.NewGuid().ToString("N"),
                    NotificationCategories.BlockingError,
                    NotificationSeverity.Error,
                    "Não foi possível aplicar a configuração",
                    "A preferência anterior foi mantida."));
            }
        }
    }

    public void ClearStatus() => StatusText = string.Empty;

    partial void OnUpdateSubtitleChanged(string value)
    {
        RefreshGeneralUpdateAction();
    }

    private string GetGeneralSettingValue(ElevationSettingDefinition definition) =>
        definition.StorageKey switch
        {
            "AutomaticAppUpdateChecksEnabled" => _preferences.AutomaticAppUpdateChecksEnabled.ToString(),
            "IncludePrereleaseAppUpdates" => _preferences.IncludePrereleaseAppUpdates.ToString(),
            _ => definition.DefaultTextValue
        };

    private ICommand? ResolveGeneralCatalogAction(ElevationSettingDefinition definition) =>
        definition.TargetPage is not null ? null : definition.ActionKey switch
        {
            "checkAppUpdates" => CheckUpdateCommand,
            "resetPreferences" => ResetPreferencesCommand,
            _ => null
        };

    private void RefreshGeneralUpdateAction()
    {
        SettingsCatalogSettingOption? option = GeneralSettingsGroups
            .SelectMany(group => group.Settings)
            .FirstOrDefault(setting => setting.HasUpdateCapability);
        if (option is null)
            return;

        string textKey;
        System.Windows.Input.ICommand? command;
        bool actionEnabled;
        switch (UpdateState)
        {
            case AppUpdateWorkflowState.Checking:
                textKey = "general.update.checking.button";
                command = CheckUpdateCommand;
                actionEnabled = false;
                break;
            case AppUpdateWorkflowState.UpdateAvailable when _updater.CanSelfUpdate:
                textKey = "general.update.download.button";
                command = DownloadUpdateCommand;
                actionEnabled = CanDownloadUpdate;
                break;
            case AppUpdateWorkflowState.UpdateAvailable:
                textKey = "general.update.release.button";
                command = OpenReleaseCommand;
                actionEnabled = true;
                break;
            case AppUpdateWorkflowState.Downloading:
                textKey = "general.update.downloading.button";
                command = DownloadUpdateCommand;
                actionEnabled = false;
                break;
            case AppUpdateWorkflowState.ReadyToApply:
                textKey = "general.update.apply.button";
                command = ApplyUpdateCommand;
                actionEnabled = CanApplyUpdate;
                break;
            case AppUpdateWorkflowState.Applying:
                textKey = "general.update.applying.button";
                command = ApplyUpdateCommand;
                actionEnabled = false;
                break;
            default:
                textKey = "general.update.check.button";
                command = CheckUpdateCommand;
                actionEnabled = CanCheckUpdate;
                break;
        }

        option.SetDescription($"{VersionText} · {UpdateSubtitle}");
        option.SetSecondaryDescription(!_updater.CanSelfUpdate
            && option.Definition.CapabilityDescriptionTextKey is { } key
                ? _elevationCatalog.ResolveText(key)
                : string.Empty);
        option.SetAuxiliaryAction(
            _elevationCatalog.ResolveText(textKey),
            command,
            actionEnabled,
            isChecking: IsCheckingUpdate,
            isDownloading: IsDownloadingUpdate,
            progress: UpdateProgress,
            cancelText: CancelUpdateText,
            cancelCommand: CancelUpdateCommand);
    }

    private void RefreshGeneralOption(SettingsCatalogSettingOption option)
    {
        option.Value = GetGeneralSettingValue(option.Definition);
        option.RefreshBindings();
        option.RefreshPresentation(
            _elevationCatalog.Catalog.Settings
                .Where(definition => definition.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(definition => definition.Key, GetGeneralSettingValue, StringComparer.OrdinalIgnoreCase),
                _elevationCatalog.Catalog.GetSettings(option.Definition.Group),
                lockedKeys: null);
    }
}
