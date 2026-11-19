using System.Text.Json.Serialization;
using WinProvision.Store.Services;
namespace WinProvision.Cli;
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, WinProvision.Core.Models.PackageInstallOptions>))]
[JsonSerializable(typeof(ApplicationPreferencesService.Preferences), TypeInfoPropertyName = "ApplicationPreferences")]
[JsonSerializable(typeof(InstallationPreferencesService.Preferences), TypeInfoPropertyName = "InstallationPreferences")]
internal partial class BackgroundJsonContext : JsonSerializerContext { }
