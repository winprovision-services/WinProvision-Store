using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using System.Windows.Input;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly IBackgroundUpdateStartupService? _backgroundUpdates;
    private bool _isLoadingMaintenanceOptions;

    public IAsyncRelayCommand ApplyScheduledMaintenanceCommand =>
        field ??= new AsyncRelayCommand(ApplyScheduledMaintenanceAsync);
    public IAsyncRelayCommand RunScheduledMaintenanceCommand =>
        field ??= new AsyncRelayCommand(RunScheduledMaintenanceAsync);
    public IReadOnlyList<SettingsCatalogGroupOption> ScheduledMaintenanceGroups { get; private set; } = [];
    public event EventHandler? ScheduledMaintenanceOptionsChanged;

    [ObservableProperty] public partial bool ScheduledUpdatesEnabled { get; set; }
    [ObservableProperty] public partial string ScheduledUpdateMode { get; set; } = "NotifyOnly";
    [ObservableProperty] public partial string ScheduledUpdateScope { get; set; } = "AllSupportedPackages";
    [ObservableProperty] public partial string ScheduledUpdateFrequency { get; set; } = "Interval";
    [ObservableProperty] public partial string ScheduledUpdateInterval { get; set; } = "60";
    [ObservableProperty] public partial string ScheduledUpdateTime { get; set; } = "09:00";
    [ObservableProperty] public partial string ScheduledUpdateDay { get; set; } = "Monday";
    [ObservableProperty] public partial string ScheduledUpdateHistory { get; private set; } = "Ainda não executada.";
    [ObservableProperty] public partial string ScheduledUpdateNext { get; private set; } = "Agenda desativada.";
    [ObservableProperty] public partial bool IsMaintenanceBusy { get; private set; }
    public bool CanEditMaintenance => !IsMaintenanceBusy;
    public bool IsIntervalSchedule => ScheduledUpdateFrequency == "Interval";
    public bool IsCalendarSchedule => ScheduledUpdateFrequency is "Daily" or "Weekly";
    public bool IsWeeklySchedule => ScheduledUpdateFrequency == "Weekly";

    partial void OnIsMaintenanceBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditMaintenance));
        RefreshMaintenanceCatalogPresentation();
    }

    partial void OnScheduledUpdateFrequencyChanged(string value)
    {
        OnPropertyChanged(nameof(IsIntervalSchedule));
        OnPropertyChanged(nameof(IsCalendarSchedule));
        OnPropertyChanged(nameof(IsWeeklySchedule));
    }

    public void ReloadScheduledMaintenance()
    {
        ScheduledUpdatesEnabled = _preferences.AutoUpdateAtStartup;
        ScheduledUpdateMode = _preferences.UpdateMode.ToString();
        ScheduledUpdateScope = _preferences.UpdateScope.ToString();
        ScheduledUpdateFrequency = _preferences.UpdateSchedule.ToString();
        ScheduledUpdateInterval = _preferences.UpdateIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        ScheduledUpdateTime = _preferences.UpdateScheduleTime;
        ScheduledUpdateDay = _preferences.UpdateScheduleDay.ToString();
        RefreshMaintenanceHistory();
        LoadMaintenanceCatalogSettings();
    }

    private void LoadMaintenanceCatalogSettings()
    {
        _isLoadingMaintenanceOptions = true;
        try
        {
            IReadOnlyList<ElevationSettingDefinition> definitions =
                _elevationCatalog.Catalog.GetGroups("maintenance")
                    .SelectMany(group => _elevationCatalog.Catalog.GetSettings(group.Key))
                    .ToArray();
            IReadOnlyDictionary<string, string> storedValues =
                _preferences.GetScheduledMaintenanceCatalogValues();

            ScheduledMaintenanceGroups = _elevationCatalog.Catalog.GetGroups("maintenance")
                .Select(group =>
                {
                    ElevationSettingDefinition[] groupDefinitions =
                        _elevationCatalog.Catalog.GetSettings(group.Key).ToArray();
                    SettingsCatalogSettingOption[] options = groupDefinitions
                        .Select(definition => new SettingsCatalogSettingOption(
                            definition,
                            _elevationCatalog.ResolveText(definition.TitleTextKey),
                            _elevationCatalog.ResolveText(definition.DescriptionTextKey),
                            null,
                            definition.StorageKey is not null
                                ? storedValues.GetValueOrDefault(definition.StorageKey, definition.DefaultTextValue)
                                : definition.DefaultTextValue,
                            _elevationCatalog.Text,
                            ResolveMaintenanceAction(definition.ActionKey)))
                        .ToArray();
                    SettingsCatalogSettingOption? primary = options.FirstOrDefault(option => option.IsToggleCard);
                    primary?.SetDependencyMessage(primary.ToggleValue
                        ? string.Empty
                        : _elevationCatalog.ResolveText("settings.dependency.disabled"));
                    var values = options.ToDictionary(option => option.Key, option => option.Value,
                        StringComparer.OrdinalIgnoreCase);
                    foreach (SettingsCatalogSettingOption option in options)
                    {
                        option.RefreshPresentation(values, groupDefinitions,
                            GetStateValueLabel(option.Definition.StateValueKey, options),
                            _elevationCatalog.ResolveText("settings.dependency.disabled"),
                            IsMaintenanceBusy);
                        option.PropertyChanged += MaintenanceOption_PropertyChanged;
                    }
                    return new SettingsCatalogGroupOption(
                        _elevationCatalog.ResolveText(group.TitleTextKey),
                        group.HelpTextKey is null ? null : _elevationCatalog.ResolveText(group.HelpTextKey),
                        group.DetailsTitleTextKey is null ? null : _elevationCatalog.ResolveText(group.DetailsTitleTextKey),
                        group.DetailsTextKey is null ? null : _elevationCatalog.ResolveText(group.DetailsTextKey),
                        options.Where(option => !ReferenceEquals(option, primary)).ToArray(),
                        group.Icon,
                        primary);
                })
                .ToArray();
            OnPropertyChanged(nameof(ScheduledMaintenanceGroups));
            UpdateMaintenanceActionDescription();
        }
        finally
        {
            _isLoadingMaintenanceOptions = false;
        }
    }

    private void MaintenanceOption_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_isLoadingMaintenanceOptions || e.PropertyName != nameof(SettingsCatalogSettingOption.Value))
            return;

        RefreshMaintenanceCatalogPresentation();
        ScheduledMaintenanceOptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshMaintenanceCatalogPresentation()
    {
        foreach (SettingsCatalogGroupOption group in ScheduledMaintenanceGroups)
        {
            SettingsCatalogSettingOption[] options = group.Settings
                .Concat(group.PrimarySetting is { } primary
                    ? new[] { primary }
                    : Array.Empty<SettingsCatalogSettingOption>())
                .ToArray();
            var values = options.ToDictionary(option => option.Key, option => option.Value,
                StringComparer.OrdinalIgnoreCase);
            ElevationSettingDefinition[] definitions = options.Select(option => option.Definition).ToArray();
            foreach (SettingsCatalogSettingOption option in options)
                option.RefreshPresentation(values, definitions,
                    GetStateValueLabel(option.Definition.StateValueKey, options),
                    _elevationCatalog.ResolveText("settings.dependency.disabled"),
                    IsMaintenanceBusy);
            group.PrimarySetting?.SetDependencyMessage(group.PrimarySetting.ToggleValue
                ? string.Empty
                : _elevationCatalog.ResolveText("settings.dependency.disabled"));
        }
    }

    private string? GetStateValueLabel(
        string? settingKey,
        IReadOnlyList<SettingsCatalogSettingOption> options)
    {
        if (settingKey is null)
            return null;
        SettingsCatalogSettingOption? stateOption = options.FirstOrDefault(option =>
            option.Key.Equals(settingKey, StringComparison.OrdinalIgnoreCase));
        return stateOption?.Options.FirstOrDefault(choice =>
            choice.Value.Equals(stateOption.Value, StringComparison.Ordinal))?.Label;
    }

    private ICommand? ResolveMaintenanceAction(string? actionKey) =>
        actionKey == "runMaintenanceNow" ? RunScheduledMaintenanceCommand : null;

    private async Task ApplyScheduledMaintenanceAsync(CancellationToken token) =>
        await ApplyScheduledMaintenanceCoreAsync(token);

    private async Task<bool> ApplyScheduledMaintenanceCoreAsync(CancellationToken token)
    {
        if (IsMaintenanceBusy || _backgroundUpdates is null)
            return false;

        var old = (_preferences.UpdateMode, _preferences.UpdateScope, _preferences.UpdateSchedule,
            _preferences.UpdateScheduleTime, _preferences.UpdateScheduleDay, _preferences.UpdateIntervalMinutes,
            _preferences.UpdateNotificationsEnabled, _preferences.AutoUpdateAtStartup,
            _preferences.UpdateScheduleAnchorUtc);
        Dictionary<string, string> values = ScheduledMaintenanceGroups
            .SelectMany(group => group.Settings.Concat(group.PrimarySetting is { } primary
                ? new[] { primary }
                : Array.Empty<SettingsCatalogSettingOption>()))
            .Where(option => !string.IsNullOrWhiteSpace(option.Definition.StorageKey))
            .ToDictionary(option => option.Definition.StorageKey!, option => option.Value,
                StringComparer.OrdinalIgnoreCase);
        if (!_preferences.IsScheduledMaintenanceCatalogValuesValid(values))
        {
            StatusText = _elevationCatalog.ResolveText("maintenance.invalidOptions");
            return false;
        }

        IsMaintenanceBusy = true;
        try
        {
            token.ThrowIfCancellationRequested();
            bool enabled = _preferences.ApplyScheduledMaintenanceCatalogValues(values);
            var mode = _preferences.UpdateMode;
            var scope = _preferences.UpdateScope;
            var schedule = _preferences.UpdateSchedule;
            var interval = _preferences.UpdateIntervalMinutes;
            var time = _preferences.UpdateScheduleTime;
            var day = _preferences.UpdateScheduleDay;
            var result = enabled
                ? await _backgroundUpdates.EnableAsync(token)
                : await _backgroundUpdates.DisableAsync(token);
            if (!result.Success)
                throw new InvalidOperationException(result.Message ?? "Não foi possível aplicar a agenda.");

            ScheduledUpdatesEnabled = enabled;
            ScheduledUpdateMode = mode.ToString();
            ScheduledUpdateScope = scope.ToString();
            ScheduledUpdateFrequency = schedule.ToString();
            ScheduledUpdateInterval = interval.ToString(CultureInfo.InvariantCulture);
            ScheduledUpdateTime = time;
            ScheduledUpdateDay = day.ToString();
            OnPropertyChanged(nameof(LaunchAtStartup));
            RefreshMaintenanceHistory();
            UpdateMaintenanceActionDescription();
            _appLog.Write(AppLogLevel.Debug, "App", "Settings.Maintenance",
                "Preferências da manutenção agendada aplicadas.", eventName: "PreferenceChanged");
            StatusText = enabled ? "Agenda aplicada." : "Agenda desativada.";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or System.Security.SecurityException)
        {
            WinProvisionLog.Error(exception, "Settings.Maintenance.Apply");
            try
            {
                _preferences.SetUpdateAutomationOptions(old.Item1, old.Item2, old.Item3, old.Item4,
                    old.Item5, old.Item6, old.Item7, old.Item9);
                var restored = old.Item8
                    ? await _backgroundUpdates.EnableAsync(CancellationToken.None)
                    : await _backgroundUpdates.DisableAsync(CancellationToken.None);
                if (!restored.Success)
                    WinProvisionLog.Write($"Falha ao restaurar agenda: {restored.Message}");
            }
            catch (Exception restoreException) when (restoreException is IOException
                or UnauthorizedAccessException or InvalidOperationException or ArgumentException
                or System.Security.SecurityException)
            {
                WinProvisionLog.Error(restoreException, "Settings.Maintenance.Restore");
            }

            ReloadScheduledMaintenance();
            StatusText = "Não foi possível aplicar a agenda. A configuração anterior foi mantida.";
            if (_toastMessages is not null)
            {
                await _toastMessages.PublishAsync(new Notification(
                    Guid.NewGuid().ToString("N"),
                    NotificationCategories.BlockingError,
                    NotificationSeverity.Error,
                    "Não foi possível aplicar a agenda",
                    "A configuração anterior foi mantida. Consulte os logs para obter detalhes."));
            }
            return false;
        }
        finally
        {
            IsMaintenanceBusy = false;
        }
    }

    private async Task RunScheduledMaintenanceAsync(CancellationToken token)
    {
        if (IsMaintenanceBusy || _backgroundUpdates is null)
            return;
        if (!await ApplyScheduledMaintenanceCoreAsync(token))
            return;

        IsMaintenanceBusy = true;
        try
        {
            StatusText = "Executando a configuração atual pela CLI...";
            int result = await _backgroundUpdates.RunNowAsync(token);
            StatusText = result switch
            {
                0 => "Execução concluída.",
                3 => "Já existe uma execução em andamento.",
                _ => "A execução encontrou falhas. Consulte os logs."
            };
            RefreshMaintenanceHistory();
            UpdateMaintenanceActionDescription();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WinProvisionLog.Error(exception, "Settings.Maintenance.Run");
            StatusText = "Não foi possível executar a manutenção pela CLI.";
        }
        finally
        {
            IsMaintenanceBusy = false;
        }
    }

    private void RefreshMaintenanceHistory()
    {
        var state = BackgroundUpdateRunStore.Read();
        ScheduledUpdateHistory = state is null ? "Última execução: ainda não executada."
            : state.CompletedUtc is null ? $"Iniciada em {state.StartedUtc.ToLocalTime():g}; conclusão não registrada."
            : $"Última execução: {state.CompletedUtc.Value.ToLocalTime():g} — {(state.ExitCode == 0 ? "concluída" : state.ExitCode == 130 ? "cancelada" : "com falhas")}.";
        if (!_preferences.AutoUpdateAtStartup) { ScheduledUpdateNext = "Próxima execução: agenda desativada."; return; }
        if (_preferences.UpdateSchedule == BackgroundUpdateSchedule.AtLogon) { ScheduledUpdateNext = "Próxima execução: ao entrar no Windows."; return; }
        if (_preferences.UpdateSchedule == BackgroundUpdateSchedule.Interval)
        {
            var anchor = (_preferences.UpdateScheduleAnchorUtc ?? DateTimeOffset.UtcNow).ToLocalTime();
            anchor = anchor.AddSeconds(-anchor.Second).AddTicks(-(anchor.Ticks % TimeSpan.TicksPerSecond));
            double elapsed = Math.Max(0, (DateTimeOffset.Now - anchor).TotalMinutes);
            var nextInterval = anchor.AddMinutes((Math.Floor(elapsed / _preferences.UpdateIntervalMinutes) + 1) * _preferences.UpdateIntervalMinutes);
            ScheduledUpdateNext = $"Próxima execução prevista: {nextInterval:g} — a cada {_preferences.UpdateIntervalMinutes} minuto(s).";
            return;
        }
        var time = TimeOnly.ParseExact(_preferences.UpdateScheduleTime, "HH:mm", CultureInfo.InvariantCulture);
        DateTime next = DateTime.Today.Add(time.ToTimeSpan());
        while (next <= DateTime.Now || (_preferences.UpdateSchedule == BackgroundUpdateSchedule.Weekly && next.DayOfWeek != _preferences.UpdateScheduleDay)) next = next.AddDays(1);
        ScheduledUpdateNext = $"Próxima execução prevista: {next:g}.";
    }

    private void UpdateMaintenanceActionDescription()
    {
        string status = $"{ScheduledUpdateHistory} · {ScheduledUpdateNext}";
        foreach (SettingsCatalogSettingOption option in ScheduledMaintenanceGroups
                     .SelectMany(group => group.Settings)
                     .Where(option => option.IsAction))
            option.SetDescription(status);
    }
}
