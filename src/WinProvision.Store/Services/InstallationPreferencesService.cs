using WinProvision.Core.Services;
using System;
using System.IO;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

public sealed partial class InstallationPreferencesService
{
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "installation-preferences.json");

    public PackageInstallMethod PreferredMethod { get; private set; } = PackageInstallMethod.Automatic;

    public InstallationPreferencesService()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var value = WinProvision.Core.Services.AotJson.Deserialize<Preferences>(File.ReadAllText(_filePath));
                if (value is not null && Enum.IsDefined(value.PreferredMethod))
                    PreferredMethod = value.PreferredMethod;
            }
        }
        catch (Exception recoveryException)
        {
            WinProvision.Store.Services.WinProvisionLog.Warning(recoveryException, "InstallationPreferencesService.InstallationPreferencesService");

            PreferredMethod = PackageInstallMethod.Automatic;
        }
    }

    public void SetPreferredMethod(PackageInstallMethod method)
    {
        if (!Enum.IsDefined(method)) method = PackageInstallMethod.Automatic;
        PreferredMethod = method;
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        AtomicFile.WriteAllText(_filePath, WinProvision.Core.Services.AotJson.Serialize(new Preferences(method)));
    }

    internal sealed record Preferences(PackageInstallMethod PreferredMethod);
}
