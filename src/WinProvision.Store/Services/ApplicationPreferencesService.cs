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
    Weekly
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
    public bool BackgroundUpdatesEnabled => AutoUpdateAtStartup || AutoMaintenanceUpdates;
    public BackgroundUpdateMode UpdateMode { get; private set; } = BackgroundUpdateMode.InstallAutomatically;
    public BackgroundUpdateScope UpdateScope { get; private set; } = BackgroundUpdateScope.AllSupportedPackages;
    public BackgroundUpdateSchedule UpdateSchedule { get; private set; } = BackgroundUpdateSchedule.AtLogon;
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
                    _savedAutoUpdateEnabled = saved.AutoUpdateAtStartup;
                    AutoMaintenanceUpdates = saved.AutoMaintenanceUpdates;
                    AutoMaintenanceDrivers = saved.AutoMaintenanceDrivers;
                }
            }
        }
        catch (Exception recoveryException)
        {
            WinProvision.Store.Services.WinProvisionLog.Warning(recoveryException, "ApplicationPreferencesService.ApplicationPreferencesService");

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
            bool legacyBackgroundOnly = HasArgument(appCommand, "--background-update");
            bool legacyCombined = HasArgument(appCommand, "--update-at-startup");

            LaunchAtStartup = !string.IsNullOrWhiteSpace(appCommand) && !legacyBackgroundOnly;
            bool registeredForBackgroundUpdates = !string.IsNullOrWhiteSpace(updateCommand)
                || legacyBackgroundOnly
                || legacyCombined;
            AutoUpdateAtStartup = _savedAutoUpdateEnabled ?? registeredForBackgroundUpdates;
            migrateLegacyRegistration = legacyBackgroundOnly || legacyCombined;
        }
        catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            WinProvisionLog.Error(exception, "ReadStartupPreferences");
            LaunchAtStartup = false;
            AutoUpdateAtStartup = false;
        }

        // Versões anteriores combinavam a inicialização do app e do atualizador em um único
        // comando. Se ambas as opções estavam ligadas, isso abria a interface em vez de rodar
        // apenas o processo de atualização. Migra para duas entradas independentes no Run.
        if (migrateLegacyRegistration)
        {
            try { UpdateStartupRegistration(); }
            catch (Exception exception) when (exception is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                WinProvisionLog.Error(exception, "MigrateStartupPreferences");
            }
        }
    }

    public void SetLaunchAtStartup(bool enabled)
    {
        LaunchAtStartup = enabled;
        UpdateStartupRegistration();
    }

    public void SetAutoUpdateAtStartup(bool enabled)
    {
        AutoUpdateAtStartup = enabled;
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

    public void SetMaintenanceAutomation(bool enabled, bool includeDrivers)
    {
        AutoMaintenanceUpdates = enabled;
        AutoMaintenanceDrivers = includeDrivers;
        Save();
        UpdateStartupRegistration();
    }

    public void SetUpdateAutomationOptions(
        BackgroundUpdateMode mode,
        BackgroundUpdateScope scope,
        BackgroundUpdateSchedule schedule,
        string time,
        DayOfWeek day)
    {
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
        string executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Não foi possível localizar o executável do WinProvision.");

        if (LaunchAtStartup)
            key.SetValue(StartupValueName, $"\"{executable}\"", RegistryValueKind.String);
        else
            key.DeleteValue(StartupValueName, throwOnMissingValue: false);

        if (BackgroundUpdatesEnabled && UpdateSchedule == BackgroundUpdateSchedule.AtLogon)
            key.SetValue(BackgroundUpdateValueName, $"\"{executable}\" --background-update", RegistryValueKind.String);
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
            AutoMaintenanceUpdates, AutoMaintenanceDrivers)));
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
        bool AutoMaintenanceDrivers = false);
}
