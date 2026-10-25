using WinProvision.Core.Services;
using System;
using System.IO;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

public sealed partial class InstallationPreferencesService
{
    private readonly string _filePath;

    public PackageInstallMethod PreferredMethod { get; private set; } = PackageInstallMethod.Automatic;
    public PackageUpdateMethod PreferredUpdateMethod { get; private set; } = PackageUpdateMethod.Automatic;

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
            if (File.Exists(_filePath))
            {
                var value = WinProvision.Core.Services.AotJson.Deserialize<Preferences>(File.ReadAllText(_filePath),
                    WinProvision.Core.Services.WinProvisionJsonOptions.Default);
                if (value is not null)
                {
                    PreferredMethod = !Enum.IsDefined(value.PreferredMethod)
                        || value.PreferredMethod == PackageInstallMethod.WinGetPkgsViper
                        ? PackageInstallMethod.Automatic
                        : value.PreferredMethod;
                    PreferredUpdateMethod = Enum.IsDefined(value.PreferredUpdateMethod)
                        ? value.PreferredUpdateMethod
                        : PackageUpdateMethod.Automatic;
                    if (PreferredMethod != value.PreferredMethod
                        || PreferredUpdateMethod != value.PreferredUpdateMethod)
                        Save();
                }
            }
        }
        catch (Exception recoveryException)
        {
            WinProvision.Core.Services.CoreLog.Warning(recoveryException, "InstallationPreferencesService.InstallationPreferencesService");

            PreferredMethod = PackageInstallMethod.Automatic;
        }
    }

    public void SetPreferredMethod(PackageInstallMethod method)
    {
        if (!Enum.IsDefined(method) || method == PackageInstallMethod.WinGetPkgsViper)
            method = PackageInstallMethod.Automatic;
        PreferredMethod = method;
        Save();
    }

    public void SetPreferredUpdateMethod(PackageUpdateMethod method)
    {
        if (!Enum.IsDefined(method)) method = PackageUpdateMethod.Automatic;
        PreferredUpdateMethod = method;
        Save();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        AtomicFile.WriteAllText(_filePath, WinProvision.Core.Services.AotJson.Serialize(
            new Preferences(PreferredMethod, PreferredUpdateMethod),
            WinProvision.Core.Services.WinProvisionJsonOptions.Default));
    }

    internal sealed record Preferences(
        PackageInstallMethod PreferredMethod,
        PackageUpdateMethod PreferredUpdateMethod = PackageUpdateMethod.Automatic);
}
