using WinProvision.Core.Services;
using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;


namespace WinProvision.Store.Services;

public enum AppThemePreference
{
    System,
    Light,
    Dark
}

public enum BackgroundUpdateMode
{
    InstallAutomatically,
    NotifyOnly
}

public enum BackgroundUpdateScope
{
    AllSupportedPackages,
    SelectedPackagesOnly
}

public enum BackgroundUpdateSchedule
{
    AtLogon,
    Daily,
    Weekly,
    Interval
}

public sealed partial class ApplicationPreferencesService
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "WinProvisionStoreWinUI3";
    private const string BackgroundUpdateValueName = "WinProvisionStoreWinUI3.BackgroundUpdate";
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStoreWinUI3", "app-preferences.json");

    public AppThemePreference Theme { get; private set; } = AppThemePreference.System;
    public bool LaunchAtStartup { get; private set; }
    public bool AutoUpdateAtStartup { get; private set; }
    public bool AutoMaintenanceUpdates { get; private set; }
    public bool AutoMaintenanceDrivers { get; private set; }
    public bool AutomaticCloudBackupEnabled { get; private set; } = true;
    public bool UseViperOfficeDownload { get; private set; }
    public bool SearchOnDemand { get; private set; }
    public event Action<bool>? SearchOnDemandChanged;
    public bool ProvisioningMenuEnabled { get; private set; }
    public event Action<bool>? ProvisioningMenuEnabledChanged;
    public bool BackgroundUpdatesEnabled => AutoUpdateAtStartup || AutoMaintenanceUpdates;
    public BackgroundUpdateMode UpdateMode { get; private set; } = BackgroundUpdateMode.InstallAutomatically;
    public BackgroundUpdateScope UpdateScope { get; private set; } = BackgroundUpdateScope.AllSupportedPackages;
    public BackgroundUpdateSchedule UpdateSchedule { get; private set; } = BackgroundUpdateSchedule.AtLogon;
    public DateTimeOffset? UpdateScheduleAnchorUtc { get; private set; }
    public int UpdateIntervalMinutes { get; private set; } = 60;
    public bool UpdateNotificationsEnabled { get; private set; } = true;
    public string UpdateScheduleTime { get; private set; } = "09:00";
    public DayOfWeek UpdateScheduleDay { get; private set; } = DayOfWeek.Monday;
    private readonly HashSet<string> _autoUpdatePackageKeys = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _persistStartup;
    public ApplicationPreferencesService(string? filePath = null, bool persistStartup = true)
    {
        _persistStartup = persistStartup; if (filePath is not null) _filePath = filePath;
        try
        {
            if (File.Exists(_filePath))
            {
                var saved = WinProvision.Core.Services.AotJson.Deserialize<Preferences>(File.ReadAllText(_filePath));
                if (saved is not null)
                {
                    if (Enum.IsDefined(saved.Theme)) Theme = saved.Theme;
                    if (saved.UpdateMode is { } mode && Enum.IsDefined(mode)) UpdateMode = mode;
                    if (saved.UpdateScope is { } scope && Enum.IsDefined(scope)) UpdateScope = scope;
                    if (saved.UpdateSchedule is { } schedule && Enum.IsDefined(schedule)) UpdateSchedule = schedule;
                    if (TimeOnly.TryParseExact(saved.UpdateScheduleTime, "HH:mm", out _))
                        UpdateScheduleTime = saved.UpdateScheduleTime!;
                    if (saved.UpdateScheduleDay is { } day && Enum.IsDefined(day)) UpdateScheduleDay = day;
                    if (saved.AutoUpdatePackageKeys is not null)
                        _autoUpdatePackageKeys.UnionWith(saved.AutoUpdatePackageKeys.Where(key => !string.IsNullOrWhiteSpace(key)));
                    UpdateIntervalMinutes = saved.UpdateIntervalMinutes is >= 15 and <= 1440 ? saved.UpdateIntervalMinutes : 60;
                    UpdateNotificationsEnabled = saved.UpdateNotificationsEnabled;
                    UpdateScheduleAnchorUtc = saved.UpdateScheduleAnchorUtc;
                    _savedAutoUpdateEnabled = saved.AutoUpdateAtStartup;
                    AutoMaintenanceUpdates = saved.AutoMaintenanceUpdates;
                    AutoMaintenanceDrivers = saved.AutoMaintenanceDrivers;
                    AutomaticCloudBackupEnabled = saved.AutomaticCloudBackupEnabled;
                    UseViperOfficeDownload = saved.UseViperOfficeDownload;
                    SearchOnDemand = saved.SearchOnDemand;
                    ProvisioningMenuEnabled = saved.ProvisioningMenuEnabled;
                }
            }
        }
        catch (Exception recoveryException)
        {
            WinProvision.Core.Services.CoreLog.Warning(recoveryException, "ApplicationPreferencesService.ApplicationPreferencesService");

            Theme = AppThemePreference.System;
        }

        if (_persistStartup) LoadStartupOptions(); else AutoUpdateAtStartup = _savedAutoUpdateEnabled == true;
    }

    private void LoadStartupOptions()
    {
        bool migrateLegacyRegistration = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
            string? appCommand = key?.GetValue(StartupValueName) as string;
            string? updateCommand = key?.GetValue(BackgroundUpdateValueName) as string;
            bool cliStartup = HasArgument(appCommand, "cli\\winprovision.exe") || HasArgument(appCommand, "cli/winprovision.exe");
            bool legacyBackgroundOnly = HasArgument(appCommand, "--background-update") && !cliStartup;
            bool legacyCombined = HasArgument(appCommand, "--update-at-startup");

            LaunchAtStartup = !string.IsNullOrWhiteSpace(appCommand) && !legacyBackgroundOnly;
            bool registeredForBackgroundUpdates = !string.IsNullOrWhiteSpace(updateCommand)
                || legacyBackgroundOnly
                || legacyCombined;
            AutoUpdateAtStartup = LaunchAtStartup || (_savedAutoUpdateEnabled ?? registeredForBackgroundUpdates);
            migrateLegacyRegistration = legacyBackgroundOnly || legacyCombined
                || LaunchAtStartup || registeredForBackgroundUpdates;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            WinProvision.Core.Services.CoreLog.Warning(exception, "ReadStartupPreferences");
            LaunchAtStartup = false;
            AutoUpdateAtStartup = false;
        }

        // Reconcile existing registrations with the CLI command after updates.
        if (migrateLegacyRegistration)
        {
            try { Save(); UpdateStartupRegistration(); }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                WinProvision.Core.Services.CoreLog.Warning(exception, "MigrateStartupPreferences");
            }
        }
    }

    public void SetLaunchAtStartup(bool enabled)
    {
        LaunchAtStartup = enabled;
        AutoUpdateAtStartup = enabled;
        Save();
        UpdateStartupRegistration();
    }

    public void SetAutoUpdateAtStartup(bool enabled)
    {
        AutoUpdateAtStartup = enabled;
        if (!enabled) LaunchAtStartup = false;
        Save();
        UpdateStartupRegistration();
    }

    public void SetTheme(AppThemePreference theme)
    {
        if (!Enum.IsDefined(theme)) theme = AppThemePreference.System;
        AppThemePreference previousTheme = Theme;
        Theme = theme;
        try
        {
            Save();
        }
        catch
        {
            Theme = previousTheme;
            throw;
        }
    }

    public void SetSearchOnDemand(bool enabled)
    {
        if (SearchOnDemand == enabled) return;
        bool previous = SearchOnDemand;
        SearchOnDemand = enabled;
        try
        {
            Save();
        }
        catch
        {
            SearchOnDemand = previous;
            throw;
        }
        SearchOnDemandChanged?.Invoke(enabled);
    }

    public void SetProvisioningMenuEnabled(bool enabled)
    {
        if (ProvisioningMenuEnabled == enabled) return;
        bool previous = ProvisioningMenuEnabled;
        ProvisioningMenuEnabled = enabled;
        try { Save(); }
        catch
        {
            ProvisioningMenuEnabled = previous;
            throw;
        }
        ProvisioningMenuEnabledChanged?.Invoke(enabled);
    }

    public void SetMaintenanceAutomation(bool enabled, bool includeDrivers)
    {
        AutoMaintenanceUpdates = enabled;
        AutoMaintenanceDrivers = includeDrivers;
        Save();
        UpdateStartupRegistration();
    }

    public void SetAutomaticCloudBackup(bool enabled)
    {
        bool previous = AutomaticCloudBackupEnabled;
        AutomaticCloudBackupEnabled = enabled;
        try { Save(); }
        catch
        {
            AutomaticCloudBackupEnabled = previous;
            throw;
        }
    }

    public void SetUpdateAutomationOptions(
        BackgroundUpdateMode mode,
        BackgroundUpdateScope scope,
        BackgroundUpdateSchedule schedule,
        string time,
        DayOfWeek day,
        int? intervalMinutes = null,
        bool? notificationsEnabled = null)
    {
        if (intervalMinutes is not null && intervalMinutes is not (>= 15 and <= 1440))
            throw new ArgumentOutOfRangeException(nameof(intervalMinutes), "O intervalo deve estar entre 15 minutos e 24 horas.");
        UpdateScheduleAnchorUtc = DateTimeOffset.UtcNow;
        UpdateIntervalMinutes = intervalMinutes ?? UpdateIntervalMinutes;
        UpdateNotificationsEnabled = notificationsEnabled ?? UpdateNotificationsEnabled;
        UpdateMode = Enum.IsDefined(mode) ? mode : BackgroundUpdateMode.InstallAutomatically;
        UpdateScope = Enum.IsDefined(scope) ? scope : BackgroundUpdateScope.AllSupportedPackages;
        UpdateSchedule = Enum.IsDefined(schedule) ? schedule : BackgroundUpdateSchedule.AtLogon;
        UpdateScheduleTime = TimeOnly.TryParseExact(time, "HH:mm", out _) ? time : "09:00";
        UpdateScheduleDay = Enum.IsDefined(day) ? day : DayOfWeek.Monday;
        Save();
        UpdateStartupRegistration();
    }

    public bool IsAutoUpdatePackageSelected(string id, string source) =>
        _autoUpdatePackageKeys.Contains(GetAutoUpdatePackageKey(id, source));

    public bool ToggleAutoUpdatePackage(string id, string source)
    {
        string key = GetAutoUpdatePackageKey(id, source);
        bool selected = _autoUpdatePackageKeys.Add(key);
        if (!selected)
            _autoUpdatePackageKeys.Remove(key);
        Save();
        return selected;
    }

    public int AutoUpdatePackageCount => _autoUpdatePackageKeys.Count;

    public void SetUseViperOfficeDownload(bool enabled)
    {
        if (UseViperOfficeDownload == enabled) return;
        bool previous = UseViperOfficeDownload;
        UseViperOfficeDownload = enabled;
        try { Save(); }
        catch
        {
            UseViperOfficeDownload = previous;
            throw;
        }
    }

    public PortableApplicationPreferences ExportPortablePreferences() => new(
        Theme, LaunchAtStartup, AutoUpdateAtStartup, AutoMaintenanceUpdates, AutoMaintenanceDrivers,
        AutomaticCloudBackupEnabled, UpdateMode, UpdateScope, UpdateSchedule, UpdateScheduleTime,
        UpdateScheduleDay, UpdateIntervalMinutes, UpdateNotificationsEnabled,
        _autoUpdatePackageKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(), UseViperOfficeDownload, SearchOnDemand, ProvisioningMenuEnabled);

    public void ImportPortablePreferences(PortableApplicationPreferences value)
    {
        if (!Enum.IsDefined(value.Theme) || !Enum.IsDefined(value.UpdateMode) || !Enum.IsDefined(value.UpdateScope)
            || !Enum.IsDefined(value.UpdateSchedule) || !Enum.IsDefined(value.UpdateScheduleDay)
            || value.UpdateIntervalMinutes is < 15 or > 1440
            || !TimeOnly.TryParseExact(value.UpdateScheduleTime, "HH:mm", out _))
            throw new ArgumentException("O arquivo contém opções inválidas.");
        Theme = value.Theme;
        LaunchAtStartup = value.LaunchAtStartup;
        AutoUpdateAtStartup = value.AutoUpdateAtStartup;
        AutoMaintenanceUpdates = value.AutoMaintenanceUpdates;
        AutoMaintenanceDrivers = value.AutoMaintenanceDrivers;
        AutomaticCloudBackupEnabled = value.AutomaticCloudBackupEnabled;
        UpdateMode = value.UpdateMode;
        UpdateScope = value.UpdateScope;
        UpdateSchedule = value.UpdateSchedule;
        UpdateScheduleTime = value.UpdateScheduleTime;
        UpdateScheduleDay = value.UpdateScheduleDay;
        UpdateIntervalMinutes = value.UpdateIntervalMinutes;
        UpdateNotificationsEnabled = value.UpdateNotificationsEnabled;
        UseViperOfficeDownload = value.UseViperOfficeDownload;
        SearchOnDemand = value.SearchOnDemand;
        ProvisioningMenuEnabled = value.ProvisioningMenuEnabled;
        _autoUpdatePackageKeys.Clear();
        foreach (string key in value.AutoUpdatePackageKeys ?? [])
            if (!string.IsNullOrWhiteSpace(key) && key.Length <= 512) _autoUpdatePackageKeys.Add(key);
        Save();
        SearchOnDemandChanged?.Invoke(SearchOnDemand);
        ProvisioningMenuEnabledChanged?.Invoke(ProvisioningMenuEnabled);
        UpdateStartupRegistration();
    }

    private static string GetAutoUpdatePackageKey(string id, string source)
    {
        string normalizedSource = source.Contains("store", StringComparison.OrdinalIgnoreCase)
            || source.Equals("msstore", StringComparison.OrdinalIgnoreCase)
            ? "msstore"
            : "winget";
        return $"{normalizedSource}|{id.Trim()}";
    }

    private void UpdateStartupRegistration()
    {
        if (!_persistStartup) return;
        using var key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath, writable: true)
            ?? throw new InvalidOperationException("Não foi possível abrir a configuração de inicialização do usuário.");
        string command = BackgroundUpdateCommand.ForDirectory(AppContext.BaseDirectory);

        if (LaunchAtStartup)
            key.SetValue(StartupValueName, command, RegistryValueKind.String);
        else
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);

        if (!LaunchAtStartup && BackgroundUpdatesEnabled && UpdateSchedule == BackgroundUpdateSchedule.AtLogon)
            key.SetValue(BackgroundUpdateValueName, command, RegistryValueKind.String);
        else
            key.DeleteValue(BackgroundUpdateValueName, throwOnMissingValue: false);
    }

    private static bool HasArgument(string? command, string argument) =>
        command?.Contains(argument, StringComparison.OrdinalIgnoreCase) == true;


    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        AtomicFile.WriteAllText(_filePath, WinProvision.Core.Services.AotJson.Serialize(new Preferences(
            Theme,
            AutoUpdateAtStartup,
            UpdateMode,
            UpdateScope,
            UpdateSchedule,
            UpdateScheduleTime,
            UpdateScheduleDay,
            _autoUpdatePackageKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(),
            AutoMaintenanceUpdates, AutoMaintenanceDrivers, UpdateIntervalMinutes, UpdateNotificationsEnabled, UpdateScheduleAnchorUtc,
            AutomaticCloudBackupEnabled, UseViperOfficeDownload, SearchOnDemand, ProvisioningMenuEnabled)));
    }

    private bool? _savedAutoUpdateEnabled;

    internal sealed record Preferences(
        AppThemePreference Theme,
        bool? AutoUpdateAtStartup = null,
        BackgroundUpdateMode? UpdateMode = null,
        BackgroundUpdateScope? UpdateScope = null,
        BackgroundUpdateSchedule? UpdateSchedule = null,
        string? UpdateScheduleTime = null,
        DayOfWeek? UpdateScheduleDay = null,
        string[]? AutoUpdatePackageKeys = null,
        bool AutoMaintenanceUpdates = false,
        bool AutoMaintenanceDrivers = false,
        int UpdateIntervalMinutes = 60,
        bool UpdateNotificationsEnabled = true,
        DateTimeOffset? UpdateScheduleAnchorUtc = null,
        bool AutomaticCloudBackupEnabled = true,
        bool UseViperOfficeDownload = false,
        bool SearchOnDemand = false,
        bool ProvisioningMenuEnabled = false);
}

public sealed record PortableApplicationPreferences(
    AppThemePreference Theme, bool LaunchAtStartup, bool AutoUpdateAtStartup,
    bool AutoMaintenanceUpdates, bool AutoMaintenanceDrivers, bool AutomaticCloudBackupEnabled,
    BackgroundUpdateMode UpdateMode, BackgroundUpdateScope UpdateScope, BackgroundUpdateSchedule UpdateSchedule,
    string UpdateScheduleTime, DayOfWeek UpdateScheduleDay, int UpdateIntervalMinutes,
    bool UpdateNotificationsEnabled, string[] AutoUpdatePackageKeys,
    bool UseViperOfficeDownload = false,
    bool SearchOnDemand = false,
    bool ProvisioningMenuEnabled = false);
