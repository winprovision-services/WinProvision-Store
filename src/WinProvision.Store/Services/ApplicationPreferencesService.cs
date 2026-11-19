using WinProvision.Core.Services;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public enum ApplicationStartupPage
{
    Discover,
    Updates,
    Installed
}

internal sealed class NullableAppearancePreferenceBooleanConverter : JsonConverter<bool?>
{
    public override bool HandleNull => true;

    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.True) return true;
        if (reader.TokenType == JsonTokenType.False) return false;
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject) reader.Skip();
        WinProvision.Core.Services.CoreLog.Warning(
            new JsonException("Valor inválido para uma preferência visual; usando o padrão do catálogo."),
            "ApplicationPreferencesService.Appearance");
        return null;
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is bool enabled) writer.WriteBooleanValue(enabled);
        else writer.WriteNullValue();
    }
}

public sealed partial class ApplicationPreferencesService
{
    internal const bool DefaultShowPackageIcons = true;
    internal const bool DefaultShowPackageScreenshots = true;
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupValueName = "WinProvisionStoreWinUI3";
    private const string BackgroundUpdateValueName = "WinProvisionStoreWinUI3.BackgroundUpdate";
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStoreWinUI3", "app-preferences.json");

    public AppThemePreference Theme { get; private set; } = AppThemePreference.System;
    public string BackdropPreference { get; private set; } = "mica";
    public event Action<string>? BackdropPreferenceChanged;
    public bool ShowPackageIcons { get; private set; } = DefaultShowPackageIcons;
    public event Action<bool>? PackageIconsChanged;
    public bool ShowPackageScreenshots { get; private set; } = DefaultShowPackageScreenshots;
    public event Action<bool>? PackageScreenshotsChanged;
    public bool LaunchAtStartup { get; private set; }
    public bool AutoUpdateAtStartup { get; private set; }
    public bool AutomaticAppUpdateChecksEnabled { get; private set; } = true;
    public bool IncludePrereleaseAppUpdates { get; private set; }
    public DateTimeOffset? LastAppUpdateCheckUtc { get; private set; }
    public event EventHandler? AppUpdateCheckPreferencesChanged;
    public bool AutoMaintenanceUpdates { get; private set; }
    public bool AutoMaintenanceDrivers { get; private set; }
    public bool AutomaticCloudBackupEnabled { get; private set; } = true;
    public bool UseViperOfficeDownload { get; private set; }
    public bool SearchOnDemand { get; private set; }
    public ApplicationStartupPage StartupPage { get; private set; } = ApplicationStartupPage.Discover;
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
    public bool IgnoreWinGetUpdateNotifications { get; private set; }
    public bool ForceWinGetInstallLocationOnUpdate { get; private set; }
    public bool WingetEnabled { get; private set; } = true;
    public event Action<bool>? WingetEnabledChanged;
    public bool ChocolateyEnabled { get; private set; }
    public bool OfficeModuleEnabled { get; private set; } = true;
    public double OperationsPanelHeight { get; private set; } = 240;
    public bool HasSavedOperationsPanelHeight { get; private set; }
    public bool OperationsPanelExpanded { get; private set; } = true;
    public bool HasSavedOperationsPanelExpanded { get; private set; }
    public string OfficeProductId { get; private set; } = "O365HomePremRetail";
    public string OfficeChannel { get; private set; } = "Current";
    public string OfficeArchitecture { get; private set; } = "64";
    public string OfficeLanguageId { get; private set; } = "pt-br";
    public string OfficeDisplayMode { get; private set; } = "Silent";
    public bool OfficeDownloadBeforeInstall { get; private set; }
    public bool OfficeAutoUpdatesEnabled { get; private set; } = true;
    public string[] OfficeAdditionalLanguages { get; private set; } = [];
    public string[] OfficeExcludedComponents { get; private set; } = [];
    public string? OfficeVisioProductId { get; private set; }
    public string? OfficeProjectProductId { get; private set; }
    public event Action<bool>? OfficeModuleEnabledChanged;
    public event Action<bool>? ChocolateyEnabledChanged;
    public string? ChocolateyExecutablePath { get; private set; }
    public string UpdateScheduleTime { get; private set; } = "09:00";
    public DayOfWeek UpdateScheduleDay { get; private set; } = DayOfWeek.Monday;
    private readonly HashSet<string> _autoUpdatePackageKeys = new(StringComparer.OrdinalIgnoreCase);

    private readonly bool _persistStartup;
    private readonly Dictionary<string, bool> _packageManagerEnabledStates = new(StringComparer.OrdinalIgnoreCase);
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
                    if (!string.IsNullOrWhiteSpace(saved.Appearance?.Backdrop))
                        BackdropPreference = saved.Appearance.Backdrop;
                    ShowPackageIcons = saved.Appearance?.ShowPackageIcons
                        ?? DefaultShowPackageIcons;
                    ShowPackageScreenshots = saved.Appearance?.ShowPackageScreenshots
                        ?? DefaultShowPackageScreenshots;
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
                    IgnoreWinGetUpdateNotifications = saved.IgnoreWinGetUpdateNotifications;
                    ForceWinGetInstallLocationOnUpdate = saved.ForceWinGetInstallLocationOnUpdate;
                    WingetEnabled = saved.WingetEnabled ?? true;
                    if (saved.PackageManagerEnabledStates is not null)
                        foreach (KeyValuePair<string, bool> manager in saved.PackageManagerEnabledStates)
                            if (!string.IsNullOrWhiteSpace(manager.Key)
                                && !manager.Key.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                                _packageManagerEnabledStates[manager.Key] = manager.Value;
                    ChocolateyEnabled = saved.ChocolateyEnabled ?? false;
                    OfficeModuleEnabled = saved.OfficeModuleEnabled ?? true;
                    HasSavedOperationsPanelHeight = saved.OperationsPanelHeight is double panelHeight
                        && double.IsFinite(panelHeight) && panelHeight >= 120;
                    OperationsPanelHeight = HasSavedOperationsPanelHeight ? saved.OperationsPanelHeight!.Value : 240;
                    HasSavedOperationsPanelExpanded = saved.OperationsPanelExpanded.HasValue;
                    OperationsPanelExpanded = saved.OperationsPanelExpanded ?? true;
                    OfficeProductId = string.IsNullOrWhiteSpace(saved.OfficeProductId) ? OfficeProductId : saved.OfficeProductId;
                    OfficeChannel = string.IsNullOrWhiteSpace(saved.OfficeChannel) ? OfficeChannel : saved.OfficeChannel;
                    OfficeArchitecture = saved.OfficeArchitecture is "32" or "64" ? saved.OfficeArchitecture : OfficeArchitecture;
                    OfficeLanguageId = string.IsNullOrWhiteSpace(saved.OfficeLanguageId) ? OfficeLanguageId : saved.OfficeLanguageId;
                    OfficeDisplayMode = saved.OfficeDisplayMode is "Silent" or "Visible" ? saved.OfficeDisplayMode : OfficeDisplayMode;
                    OfficeDownloadBeforeInstall = saved.OfficeDownloadBeforeInstall;
                    OfficeAutoUpdatesEnabled = saved.OfficeAutoUpdatesEnabled ?? true;
                    OfficeAdditionalLanguages = (saved.OfficeAdditionalLanguages ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    OfficeExcludedComponents = (saved.OfficeExcludedComponents ?? []).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    OfficeVisioProductId = saved.OfficeVisioProductId;
                    OfficeProjectProductId = saved.OfficeProjectProductId;
                    ChocolateyExecutablePath = string.IsNullOrWhiteSpace(saved.ChocolateyExecutablePath) ? null : saved.ChocolateyExecutablePath;
                    UpdateScheduleAnchorUtc = saved.UpdateScheduleAnchorUtc;
                    _savedAutoUpdateEnabled = saved.AutoUpdateAtStartup;
                    _savedLaunchAtStartup = saved.LaunchAtStartup;
                    AutomaticAppUpdateChecksEnabled = saved.AutomaticAppUpdateChecksEnabled ?? true;
                    IncludePrereleaseAppUpdates = saved.IncludePrereleaseAppUpdates;
                    LastAppUpdateCheckUtc = saved.LastAppUpdateCheckUtc;
                    AutoMaintenanceUpdates = saved.AutoMaintenanceUpdates;
                    AutoMaintenanceDrivers = saved.AutoMaintenanceDrivers;
                    AutomaticCloudBackupEnabled = saved.AutomaticCloudBackupEnabled;
                    UseViperOfficeDownload = saved.UseViperOfficeDownload;
                    SearchOnDemand = saved.SearchOnDemand;
                    if (saved.StartupPage is { } startupPage && Enum.IsDefined(startupPage)) StartupPage = startupPage;
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

            LaunchAtStartup = _savedLaunchAtStartup ?? (!string.IsNullOrWhiteSpace(appCommand) && !legacyBackgroundOnly);
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

    public void SetAutomaticAppUpdateChecksEnabled(bool enabled)
    {
        if (AutomaticAppUpdateChecksEnabled == enabled) return;
        bool previous = AutomaticAppUpdateChecksEnabled;
        AutomaticAppUpdateChecksEnabled = enabled;
        try { Save(); }
        catch
        {
            AutomaticAppUpdateChecksEnabled = previous;
            throw;
        }
        AppUpdateCheckPreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetIncludePrereleaseAppUpdates(bool enabled)
    {
        if (IncludePrereleaseAppUpdates == enabled) return;
        bool previous = IncludePrereleaseAppUpdates;
        IncludePrereleaseAppUpdates = enabled;
        try { Save(); }
        catch
        {
            IncludePrereleaseAppUpdates = previous;
            throw;
        }
        AppUpdateCheckPreferencesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkAppUpdateCheck(DateTimeOffset checkedAtUtc)
    {
        DateTimeOffset? previous = LastAppUpdateCheckUtc;
        LastAppUpdateCheckUtc = checkedAtUtc.ToUniversalTime();
        try { Save(); }
        catch
        {
            LastAppUpdateCheckUtc = previous;
            throw;
        }
        AppUpdateCheckPreferencesChanged?.Invoke(this, EventArgs.Empty);
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

    public void SetBackdropPreference(string backdropId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backdropId);
        string previous = BackdropPreference;
        if (previous.Equals(backdropId, StringComparison.OrdinalIgnoreCase)) return;
        BackdropPreference = backdropId;
        try
        {
            Save();
        }
        catch
        {
            BackdropPreference = previous;
            throw;
        }
        BackdropPreferenceChanged?.Invoke(backdropId);
    }

    public void SetPackageVisualPreference(string id, bool enabled)
    {
        switch (id)
        {
            case "showPackageIcons":
                SetPackageIcons(enabled);
                break;
            case "showPackageScreenshots":
                SetPackageScreenshots(enabled);
                break;
            default:
                var exception = new ArgumentOutOfRangeException(nameof(id), id, "Preferência visual desconhecida.");
                CoreLog.Warning(exception, "ApplicationPreferencesService.SetPackageVisualPreference");
                throw exception;
        }
    }

    private void SetPackageIcons(bool enabled)
    {
        if (ShowPackageIcons == enabled) return;
        bool previous = ShowPackageIcons;
        ShowPackageIcons = enabled;
        try { Save(); }
        catch { ShowPackageIcons = previous; throw; }
        PackageIconsChanged?.Invoke(enabled);
    }

    private void SetPackageScreenshots(bool enabled)
    {
        if (ShowPackageScreenshots == enabled) return;
        bool previous = ShowPackageScreenshots;
        ShowPackageScreenshots = enabled;
        try { Save(); }
        catch { ShowPackageScreenshots = previous; throw; }
        PackageScreenshotsChanged?.Invoke(enabled);
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

    public void SetStartupPage(ApplicationStartupPage page)
    {
        if (!Enum.IsDefined(page)) page = ApplicationStartupPage.Discover;
        ApplicationStartupPage previous = StartupPage;
        StartupPage = page;
        try { Save(); }
        catch { StartupPage = previous; throw; }
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
        bool? notificationsEnabled = null,
        DateTimeOffset? scheduleAnchorUtc = null)
    {
        if (intervalMinutes is not null && intervalMinutes is not (>= 15 and <= 1440))
            throw new ArgumentOutOfRangeException(nameof(intervalMinutes), "O intervalo deve estar entre 15 minutos e 24 horas.");
        UpdateScheduleAnchorUtc = scheduleAnchorUtc ?? DateTimeOffset.UtcNow;
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

    public IReadOnlyDictionary<string, string> GetScheduledMaintenanceCatalogValues() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AutoUpdateAtStartup"] = AutoUpdateAtStartup.ToString(),
            ["UpdateMode"] = UpdateMode.ToString(),
            ["UpdateScope"] = UpdateScope.ToString(),
            ["UpdateSchedule"] = UpdateSchedule.ToString(),
            ["UpdateIntervalMinutes"] = UpdateIntervalMinutes.ToString(CultureInfo.InvariantCulture),
            ["UpdateScheduleTime"] = UpdateScheduleTime,
            ["UpdateScheduleDay"] = UpdateScheduleDay.ToString()
        };

    public bool ApplyScheduledMaintenanceCatalogValues(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (!IsScheduledMaintenanceCatalogValuesValid(values))
            throw new ArgumentException("As opções da manutenção agendada são inválidas.", nameof(values));

        string Get(string key, string current) => values.GetValueOrDefault(key, current);
        bool.TryParse(Get("AutoUpdateAtStartup", AutoUpdateAtStartup.ToString()), out bool enabled);
        Enum.TryParse(Get("UpdateMode", UpdateMode.ToString()), true, out BackgroundUpdateMode mode);
        Enum.TryParse(Get("UpdateScope", UpdateScope.ToString()), true, out BackgroundUpdateScope scope);
        Enum.TryParse(Get("UpdateSchedule", UpdateSchedule.ToString()), true, out BackgroundUpdateSchedule schedule);
        Enum.TryParse(Get("UpdateScheduleDay", UpdateScheduleDay.ToString()), true, out DayOfWeek day);
        int.TryParse(Get("UpdateIntervalMinutes", UpdateIntervalMinutes.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int interval);
        string time = Get("UpdateScheduleTime", UpdateScheduleTime);

        SetUpdateAutomationOptions(mode, scope, schedule, time, day, interval,
            notificationsEnabled: true);
        return enabled;
    }

    public bool IsScheduledMaintenanceCatalogValuesValid(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string Get(string key, string current) => values.GetValueOrDefault(key, current);
        return bool.TryParse(Get("AutoUpdateAtStartup", AutoUpdateAtStartup.ToString()), out _)
            && Enum.TryParse(Get("UpdateMode", UpdateMode.ToString()), true, out BackgroundUpdateMode mode)
            && Enum.IsDefined(mode)
            && Enum.TryParse(Get("UpdateScope", UpdateScope.ToString()), true, out BackgroundUpdateScope scope)
            && Enum.IsDefined(scope)
            && Enum.TryParse(Get("UpdateSchedule", UpdateSchedule.ToString()), true, out BackgroundUpdateSchedule schedule)
            && Enum.IsDefined(schedule)
            && Enum.TryParse(Get("UpdateScheduleDay", UpdateScheduleDay.ToString()), true, out DayOfWeek day)
            && Enum.IsDefined(day)
            && int.TryParse(Get("UpdateIntervalMinutes", UpdateIntervalMinutes.ToString(CultureInfo.InvariantCulture)),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int interval)
            && interval is >= 15 and <= 1440
            && TimeOnly.TryParseExact(Get("UpdateScheduleTime", UpdateScheduleTime), "HH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    public void SetIgnoreWinGetUpdateNotifications(bool value)
    {
        if (IgnoreWinGetUpdateNotifications == value) return;
        IgnoreWinGetUpdateNotifications = value;
        Save();
    }

    public void SetForceWinGetInstallLocationOnUpdate(bool value)
    {
        if (ForceWinGetInstallLocationOnUpdate == value) return;
        ForceWinGetInstallLocationOnUpdate = value;
        Save();
    }

    public void SetWingetEnabled(bool enabled)
    {
        if (WingetEnabled == enabled) return;
        bool previous = WingetEnabled;
        WingetEnabled = enabled;
        try { Save(); }
        catch { WingetEnabled = previous; throw; }
        WingetEnabledChanged?.Invoke(enabled);
        PackageManagerEnabledChanged?.Invoke("winget", enabled);
    }

    public bool GetPackageManagerEnabled(string managerId, bool defaultValue = true) => managerId.Trim().ToLowerInvariant() switch
    {
        "winget" => WingetEnabled,
        "msstore" => WingetEnabled,
        "chocolatey" => ChocolateyEnabled,
        _ => _packageManagerEnabledStates.TryGetValue(managerId, out bool enabled) ? enabled : defaultValue
    };

    public event Action<string, bool>? PackageManagerEnabledChanged;

    public void SetPackageManagerEnabled(string managerId, bool enabled)
    {
        switch (managerId.Trim().ToLowerInvariant())
        {
            case "winget": SetWingetEnabled(enabled); return;
            case "msstore": return;
            case "chocolatey": SetChocolateyEnabled(enabled); return;
        }

        string key = managerId.Trim();
        bool hadPrevious = _packageManagerEnabledStates.TryGetValue(key, out bool previous);
        if (hadPrevious && previous == enabled) return;
        _packageManagerEnabledStates[key] = enabled;
        try { Save(); }
        catch
        {
            if (hadPrevious) _packageManagerEnabledStates[key] = previous;
            else _packageManagerEnabledStates.Remove(key);
            throw;
        }
        PackageManagerEnabledChanged?.Invoke(key, enabled);
    }

    public void SetChocolateyEnabled(bool enabled)
    {
        if (ChocolateyEnabled == enabled) return;
        bool previous = ChocolateyEnabled;
        ChocolateyEnabled = enabled;
        try { Save(); }
        catch { ChocolateyEnabled = previous; throw; }
        ChocolateyEnabledChanged?.Invoke(enabled);
        PackageManagerEnabledChanged?.Invoke("chocolatey", enabled);
    }

    public void SetOfficeModuleEnabled(bool enabled)
    {
        if (OfficeModuleEnabled == enabled) return;
        bool previous = OfficeModuleEnabled;
        OfficeModuleEnabled = enabled;
        try { Save(); }
        catch { OfficeModuleEnabled = previous; throw; }
        OfficeModuleEnabledChanged?.Invoke(enabled);
    }

    public void SetOperationsPanelState(double height, bool expanded)
    {
        OperationsPanelHeight = double.IsFinite(height) && height >= 120 ? height : 240;
        HasSavedOperationsPanelHeight = true;
        OperationsPanelExpanded = expanded;
        HasSavedOperationsPanelExpanded = true;
        Save();
    }

    public void SetOfficeSettings(string productId, string channel, string architecture, string languageId,
        string displayMode, bool downloadBeforeInstall, bool autoUpdatesEnabled,
        IEnumerable<string> additionalLanguages, IEnumerable<string> excludedComponents,
        string? visioProductId, string? projectProductId)
    {
        OfficeProductId = productId;
        OfficeChannel = channel;
        OfficeArchitecture = architecture;
        OfficeLanguageId = languageId;
        OfficeDisplayMode = displayMode;
        OfficeDownloadBeforeInstall = downloadBeforeInstall;
        OfficeAutoUpdatesEnabled = autoUpdatesEnabled;
        OfficeAdditionalLanguages = additionalLanguages.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        OfficeExcludedComponents = excludedComponents.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        OfficeVisioProductId = visioProductId;
        OfficeProjectProductId = projectProductId;
        Save();
    }

    public void SetChocolateyExecutablePath(string? path)
    {
        string? normalized = string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());
        if (string.Equals(ChocolateyExecutablePath, normalized, StringComparison.OrdinalIgnoreCase)) return;
        string? previous = ChocolateyExecutablePath;
        ChocolateyExecutablePath = normalized;
        try { Save(); }
        catch { ChocolateyExecutablePath = previous; throw; }
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
        _autoUpdatePackageKeys.OrderBy(key => key, StringComparer.OrdinalIgnoreCase).ToArray(), UseViperOfficeDownload, SearchOnDemand, ProvisioningMenuEnabled, StartupPage,
        AutomaticAppUpdateChecksEnabled, IncludePrereleaseAppUpdates);

    public void ImportPortablePreferences(PortableApplicationPreferences value)
    {
        if (!Enum.IsDefined(value.Theme) || !Enum.IsDefined(value.UpdateMode) || !Enum.IsDefined(value.UpdateScope)
            || !Enum.IsDefined(value.UpdateSchedule) || !Enum.IsDefined(value.UpdateScheduleDay)
            || !Enum.IsDefined(value.StartupPage)
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
        StartupPage = value.StartupPage;
        AutomaticAppUpdateChecksEnabled = value.AutomaticAppUpdateChecksEnabled;
        IncludePrereleaseAppUpdates = value.IncludePrereleaseAppUpdates;
        _autoUpdatePackageKeys.Clear();
        foreach (string key in value.AutoUpdatePackageKeys ?? [])
            if (!string.IsNullOrWhiteSpace(key) && key.Length <= 512) _autoUpdatePackageKeys.Add(key);
        Save();
        SearchOnDemandChanged?.Invoke(SearchOnDemand);
        ProvisioningMenuEnabledChanged?.Invoke(ProvisioningMenuEnabled);
        AppUpdateCheckPreferencesChanged?.Invoke(this, EventArgs.Empty);
        UpdateStartupRegistration();
    }

    private static string GetAutoUpdatePackageKey(string id, string source)
    {
        string normalizedSource = source.Contains("chocolatey", StringComparison.OrdinalIgnoreCase)
            ? "chocolatey"
            : source.Contains("store", StringComparison.OrdinalIgnoreCase)
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
        key.DeleteValue(StartupValueName, throwOnMissingValue: false);

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
            AutomaticCloudBackupEnabled, UseViperOfficeDownload, SearchOnDemand, ProvisioningMenuEnabled, StartupPage,
            LaunchAtStartup, IgnoreWinGetUpdateNotifications, ForceWinGetInstallLocationOnUpdate,
            ChocolateyEnabled,
            ChocolateyExecutablePath,
            WingetEnabled,
            _packageManagerEnabledStates.Count == 0 ? null : new Dictionary<string, bool>(_packageManagerEnabledStates, StringComparer.OrdinalIgnoreCase),
            new AppearancePreferences(BackdropPreference, ShowPackageIcons, ShowPackageScreenshots),
            AutomaticAppUpdateChecksEnabled, IncludePrereleaseAppUpdates, LastAppUpdateCheckUtc,
            OfficeModuleEnabled, OfficeProductId, OfficeChannel, OfficeArchitecture, OfficeLanguageId, OfficeDisplayMode,
            OfficeDownloadBeforeInstall, OfficeAutoUpdatesEnabled, OfficeAdditionalLanguages, OfficeExcludedComponents,
            OfficeVisioProductId, OfficeProjectProductId, OperationsPanelHeight, OperationsPanelExpanded)));
    }

    private bool? _savedAutoUpdateEnabled;
    private bool? _savedLaunchAtStartup;

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
        bool ProvisioningMenuEnabled = false,
        ApplicationStartupPage? StartupPage = null,
        bool? LaunchAtStartup = null,
        bool IgnoreWinGetUpdateNotifications = false,
        bool ForceWinGetInstallLocationOnUpdate = false,
        bool? ChocolateyEnabled = null,
        string? ChocolateyExecutablePath = null,
        bool? WingetEnabled = null,
        Dictionary<string, bool>? PackageManagerEnabledStates = null,
        [property: JsonPropertyName("appearance")] AppearancePreferences? Appearance = null,
        bool? AutomaticAppUpdateChecksEnabled = null,
        bool IncludePrereleaseAppUpdates = false,
        DateTimeOffset? LastAppUpdateCheckUtc = null,
        bool? OfficeModuleEnabled = null, string? OfficeProductId = null, string? OfficeChannel = null,
        string? OfficeArchitecture = null, string? OfficeLanguageId = null, string? OfficeDisplayMode = null,
        bool OfficeDownloadBeforeInstall = false, bool? OfficeAutoUpdatesEnabled = null,
        string[]? OfficeAdditionalLanguages = null, string[]? OfficeExcludedComponents = null,
        string? OfficeVisioProductId = null, string? OfficeProjectProductId = null,
        double? OperationsPanelHeight = null, bool? OperationsPanelExpanded = null);

    internal sealed record AppearancePreferences(
        [property: JsonPropertyName("backdrop")] string? Backdrop = null,
        [property: JsonPropertyName("showPackageIcons"), JsonConverter(typeof(NullableAppearancePreferenceBooleanConverter))] bool? ShowPackageIcons = null,
        [property: JsonPropertyName("showPackageScreenshots"), JsonConverter(typeof(NullableAppearancePreferenceBooleanConverter))] bool? ShowPackageScreenshots = null);
}

public sealed record PortableApplicationPreferences(
    AppThemePreference Theme, bool LaunchAtStartup, bool AutoUpdateAtStartup,
    bool AutoMaintenanceUpdates, bool AutoMaintenanceDrivers, bool AutomaticCloudBackupEnabled,
    BackgroundUpdateMode UpdateMode, BackgroundUpdateScope UpdateScope, BackgroundUpdateSchedule UpdateSchedule,
    string UpdateScheduleTime, DayOfWeek UpdateScheduleDay, int UpdateIntervalMinutes,
    bool UpdateNotificationsEnabled, string[] AutoUpdatePackageKeys,
    bool UseViperOfficeDownload = false,
    bool SearchOnDemand = false,
    bool ProvisioningMenuEnabled = false,
    ApplicationStartupPage StartupPage = ApplicationStartupPage.Discover,
    bool AutomaticAppUpdateChecksEnabled = true,
    bool IncludePrereleaseAppUpdates = false);
