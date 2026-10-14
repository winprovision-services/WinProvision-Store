using System.Collections.Generic;
using System.Text.Json.Serialization;
namespace WinProvision.Store;
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(Dictionary<string, WinProvision.Core.Models.PackageInstallOptions>))]
[JsonSerializable(typeof(WinProvision.Core.Models.ProfileBackupSet))]
[JsonSerializable(typeof(WinProvision.Store.Services.ApplicationPreferencesService.Preferences), TypeInfoPropertyName = "ApplicationPreferences")]
[JsonSerializable(typeof(WinProvision.Store.Services.InstallationPreferencesService.Preferences), TypeInfoPropertyName = "InstallationPreferences")]
[JsonSerializable(typeof(WinProvision.Store.Services.InstalledPackageInfo))]
[JsonSerializable(typeof(WinProvision.Store.ViewModels.AccountSyncViewModel.DeviceCodeResponse))]
[JsonSerializable(typeof(WinProvision.Store.ViewModels.AccountSyncViewModel.TokenResponse))]
internal partial class UiJsonContext : JsonSerializerContext { }
