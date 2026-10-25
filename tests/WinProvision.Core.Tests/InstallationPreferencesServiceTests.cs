using System.Text.Json;
using WinProvision.Cli;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstallationPreferencesServiceTests
{
    [Fact]
    public void UpdateMethodPreferenceIsIndependentAndMigratesLegacyFiles()
    {
        AotJson.Register(BackgroundJsonContext.Default);
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-update-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "installation-preferences.json");
        try
        {
            File.WriteAllText(path, "{\"preferredMethod\":\"WingetCli\"}");
            var preferences = new InstallationPreferencesService(path);

            Assert.Equal(PackageInstallMethod.WingetCli, preferences.PreferredMethod);
            Assert.Equal(PackageUpdateMethod.Automatic, preferences.PreferredUpdateMethod);

            preferences.SetPreferredUpdateMethod(PackageUpdateMethod.WinProvisionApi);
            var reloaded = new InstallationPreferencesService(path);
            Assert.Equal(PackageInstallMethod.WingetCli, reloaded.PreferredMethod);
            Assert.Equal(PackageUpdateMethod.WinProvisionApi, reloaded.PreferredUpdateMethod);

            reloaded.SetPreferredMethod(PackageInstallMethod.ComApi);
            var final = new InstallationPreferencesService(path);
            Assert.Equal(PackageInstallMethod.ComApi, final.PreferredMethod);
            Assert.Equal(PackageUpdateMethod.WinProvisionApi, final.PreferredUpdateMethod);

            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal("WinProvisionApi", json.RootElement.GetProperty("preferredUpdateMethod").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
