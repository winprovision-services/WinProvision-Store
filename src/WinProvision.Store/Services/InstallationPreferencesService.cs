using WinProvision.Core.Services;
using System;
using System.IO;
using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

public sealed partial class InstallationPreferencesService
{
    private readonly string _filePath;

    public WinGetEnginePreference PreferredWinGetEngine { get; private set; } = WinGetEnginePreference.WingetCli;

    public InstallationPreferencesService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "installation-preferences.json"))
    {
    }

    internal InstallationPreferencesService(string filePath)
    {
        _filePath = Path.GetFullPath(filePath);
        try
        {
            if (!File.Exists(_filePath)) return;

            Preferences? saved = AotJson.Deserialize<Preferences>(File.ReadAllText(_filePath),
                WinProvisionJsonOptions.Default);
            if (saved is null) return;

            if (saved.PreferredWinGetEngine is { } engine && Enum.IsDefined(engine))
            {
                PreferredWinGetEngine = engine;
                LogIgnoredLegacyValues(saved, "preferredWinGetEngine tem precedência");
                return;
            }

            if (saved.PreferredWinGetEngine is not null)
                CoreLog.Information("InstallationPreferences.Migration",
                    $"Valor inválido ignorado: preferredWinGetEngine={saved.PreferredWinGetEngine}.");

            bool migrated = false;
            if (saved.PreferredMethod is { } install && Enum.IsDefined(install))
            {
                PreferredWinGetEngine = install switch
                {
                    PackageInstallMethod.ComApi => WinGetEnginePreference.ComApi,
                    PackageInstallMethod.WingetCli => WinGetEnginePreference.WingetCli,
                    _ => WinGetEnginePreference.Automatic
                };
                CoreLog.Information("InstallationPreferences.Migration",
                    $"preferredMethod={install} migrado para preferredWinGetEngine={PreferredWinGetEngine}.");
                migrated = true;
            }
            else if (saved.PreferredMethod is not null)
            {
                CoreLog.Information("InstallationPreferences.Migration",
                    $"Valor inválido ignorado: preferredMethod={saved.PreferredMethod}.");
            }

            if (saved.PreferredUpdateMethod is { } update && Enum.IsDefined(update))
            {
                WinGetEnginePreference updateEngine = update switch
                {
                    PackageUpdateMethod.ComApi => WinGetEnginePreference.ComApi,
                    PackageUpdateMethod.WingetCli => WinGetEnginePreference.WingetCli,
                    _ => WinGetEnginePreference.Automatic
                };
                if (saved.PreferredMethod is null || !Enum.IsDefined(saved.PreferredMethod.Value))
                {
                    PreferredWinGetEngine = updateEngine;
                    CoreLog.Information("InstallationPreferences.Migration",
                        $"preferredUpdateMethod={update} migrado para preferredWinGetEngine={PreferredWinGetEngine}.");
                    migrated = true;
                }
                else
                {
                    CoreLog.Information("InstallationPreferences.Migration",
                        $"preferredUpdateMethod={update} ignorado; vale preferredMethod={saved.PreferredMethod} e migração resultante={PreferredWinGetEngine}.");
                    migrated = true;
                }
            }
            else if (saved.PreferredUpdateMethod is not null)
            {
                CoreLog.Information("InstallationPreferences.Migration",
                    $"Valor inválido ignorado: preferredUpdateMethod={saved.PreferredUpdateMethod}.");
            }

            // Persist only the single current preference after migrating a legacy file.
            if (migrated || saved.PreferredWinGetEngine is null
                || saved.PreferredWinGetEngine is not null && !Enum.IsDefined(saved.PreferredWinGetEngine.Value))
                Save();
        }
        catch (Exception recoveryException)
        {
            CoreLog.Warning(recoveryException, "InstallationPreferencesService.InstallationPreferencesService");
            PreferredWinGetEngine = WinGetEnginePreference.WingetCli;
        }
    }

    private static void LogIgnoredLegacyValues(Preferences saved, string reason)
    {
        if (saved.PreferredMethod is { } install)
            CoreLog.Information("InstallationPreferences.Migration",
                $"preferredMethod={install} ignorado; {reason}.");
        if (saved.PreferredUpdateMethod is { } update)
            CoreLog.Information("InstallationPreferences.Migration",
                $"preferredUpdateMethod={update} ignorado; {reason}.");
    }

    public void SetPreferredWinGetEngine(WinGetEnginePreference preference)
    {
        if (!Enum.IsDefined(preference)) preference = WinGetEnginePreference.WingetCli;
        PreferredWinGetEngine = preference;
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        AtomicFile.WriteAllText(_filePath, AotJson.Serialize(
            new Preferences(PreferredWinGetEngine), WinProvisionJsonOptions.Default));
    }

    internal sealed record Preferences(
        WinGetEnginePreference? PreferredWinGetEngine = null,
        PackageInstallMethod? PreferredMethod = null,
        PackageUpdateMethod? PreferredUpdateMethod = null);
}
