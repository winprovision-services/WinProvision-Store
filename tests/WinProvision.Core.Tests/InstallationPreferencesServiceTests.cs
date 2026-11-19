using System.Text.Json;
using WinProvision.Cli;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class InstallationPreferencesServiceTests
{
    [Theory]
    [InlineData("Automatic", WinGetEnginePreference.Automatic)]
    [InlineData("ComApi", WinGetEnginePreference.ComApi)]
    [InlineData("WingetCli", WinGetEnginePreference.WingetCli)]
    [InlineData("WinProvisionApi", WinGetEnginePreference.Automatic)]
    public void MigratesLegacyInstallPreference(string legacy, WinGetEnginePreference expected)
    {
        var (path, directory) = CreatePreferencesFile($"{{\"preferredMethod\":\"{legacy}\"}}");
        try
        {
            AotJson.Register(BackgroundJsonContext.Default);
            var preferences = new InstallationPreferencesService(path);

            Assert.Equal(expected, preferences.PreferredWinGetEngine);
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal((int)expected, json.RootElement.GetProperty("preferredWinGetEngine").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("preferredMethod", out _));
            Assert.False(json.RootElement.TryGetProperty("preferredUpdateMethod", out _));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("Automatic", WinGetEnginePreference.Automatic)]
    [InlineData("ComApi", WinGetEnginePreference.ComApi)]
    [InlineData("WingetCli", WinGetEnginePreference.WingetCli)]
    [InlineData("WinProvisionApi", WinGetEnginePreference.Automatic)]
    public void MigratesLegacyUpdatePreferenceWhenInstallPreferenceIsMissing(
        string legacy, WinGetEnginePreference expected)
    {
        var (path, directory) = CreatePreferencesFile($"{{\"preferredUpdateMethod\":\"{legacy}\"}}");
        try
        {
            AotJson.Register(BackgroundJsonContext.Default);
            var preferences = new InstallationPreferencesService(path);
            Assert.Equal(expected, preferences.PreferredWinGetEngine);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void InstallationPreferenceWinsWhenLegacyValuesDiffer()
    {
        var (path, directory) = CreatePreferencesFile(
            "{\"preferredMethod\":\"ComApi\",\"preferredUpdateMethod\":\"WingetCli\"}");
        try
        {
            AotJson.Register(BackgroundJsonContext.Default);
            var preferences = new InstallationPreferencesService(path);
            Assert.Equal(WinGetEnginePreference.ComApi, preferences.PreferredWinGetEngine);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void SingleSavedPreferenceOverridesLegacyValuesAndCanBeChanged()
    {
        var (path, directory) = CreatePreferencesFile(
            "{\"preferredWinGetEngine\":1,\"preferredMethod\":\"WingetCli\",\"preferredUpdateMethod\":\"WinProvisionApi\"}");
        try
        {
            AotJson.Register(BackgroundJsonContext.Default);
            var preferences = new InstallationPreferencesService(path);
            Assert.Equal(WinGetEnginePreference.ComApi, preferences.PreferredWinGetEngine);

            preferences.SetPreferredWinGetEngine(WinGetEnginePreference.Automatic);
            var reloaded = new InstallationPreferencesService(path);
            Assert.Equal(WinGetEnginePreference.Automatic, reloaded.PreferredWinGetEngine);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void DefaultsToWingetCliWhenNoPreferenceHasBeenSaved()
    {
        var (path, directory) = CreatePreferencesFile("{}");
        try
        {
            AotJson.Register(BackgroundJsonContext.Default);
            var preferences = new InstallationPreferencesService(path);
            Assert.Equal(WinGetEnginePreference.WingetCli, preferences.PreferredWinGetEngine);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static (string Path, string Directory) CreatePreferencesFile(string json)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "WinProvision-engine-preferences-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "installation-preferences.json");
        File.WriteAllText(path, json);
        return (path, directory);
    }
}
