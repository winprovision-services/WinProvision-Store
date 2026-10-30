using System.Text.Json.Serialization;

namespace WinProvision.Core.Models;

/// <summary>Opção de instalação extraída do manifesto do gerenciador de pacotes e embutida no detalhe do catálogo V2.</summary>
public class CatalogInstaller
{
    [JsonPropertyName("architecture")] public string? Architecture { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("nestedType")] public string? NestedType { get; set; }
    [JsonPropertyName("nestedInstallerFile")] public string? NestedInstallerFile { get; set; }
    [JsonPropertyName("portableCommandAlias")] public string? PortableCommandAlias { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonPropertyName("elevationRequirement")] public string? ElevationRequirement { get; set; }
    [JsonPropertyName("locale")] public string? Locale { get; set; }
    [JsonPropertyName("platforms")] public List<string> Platforms { get; set; } = [];
    [JsonPropertyName("minimumOsVersion")] public string? MinimumOsVersion { get; set; }
    [JsonPropertyName("installModes")] public List<string> InstallModes { get; set; } = [];
    [JsonPropertyName("installerSwitches")] public Dictionary<string, string> InstallerSwitches { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("upgradeBehavior")] public string? UpgradeBehavior { get; set; }
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = [];
    [JsonPropertyName("unsupportedArguments")] public List<string> UnsupportedArguments { get; set; } = [];
    [JsonPropertyName("downloadCommandProhibited")] public bool DownloadCommandProhibited { get; set; }
    [JsonPropertyName("nestedInstallerFiles")] public List<string> NestedInstallerFiles { get; set; } = [];
    [JsonPropertyName("appsAndFeaturesEntries")] public List<CatalogAppsAndFeaturesEntry> AppsAndFeaturesEntries { get; set; } = [];
    [JsonPropertyName("commands")] public List<string> Commands { get; set; } = [];
    [JsonPropertyName("fileExtensions")] public List<string> FileExtensions { get; set; } = [];
    [JsonPropertyName("protocols")] public List<string> Protocols { get; set; } = [];
    [JsonPropertyName("capabilities")] public List<string> Capabilities { get; set; } = [];
    [JsonPropertyName("unsupportedOsArchitectures")] public List<string> UnsupportedOsArchitectures { get; set; } = [];
    [JsonPropertyName("errorCodes")] public List<int> ErrorCodes { get; set; } = [];
    [JsonPropertyName("url")] public string Url { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string? Sha256 { get; set; }
    [JsonPropertyName("silentArgs")] public string? SilentArgs { get; set; }
    [JsonPropertyName("silentSource")] public string SilentSource { get; set; } = "none";
    [JsonPropertyName("silentSupported")] public bool SilentSupported { get; set; }
    [JsonPropertyName("productCode")] public string? ProductCode { get; set; }
    [JsonPropertyName("successCodes")] public List<int> SuccessCodes { get; set; } = [];

    [JsonIgnore]
    public bool IsZipWithNestedInstaller =>
        string.Equals(Type, "zip", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(NestedInstallerFile);

    [JsonIgnore]
    public bool IsPortable => string.Equals(Type, "portable", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(Type, "zip", StringComparison.OrdinalIgnoreCase)
            && string.Equals(NestedType, "portable", StringComparison.OrdinalIgnoreCase));
}

public sealed class CatalogAppsAndFeaturesEntry
{
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("publisher")] public string? Publisher { get; set; }
    [JsonPropertyName("displayVersion")] public string? DisplayVersion { get; set; }
    [JsonPropertyName("productCode")] public string? ProductCode { get; set; }
    [JsonPropertyName("upgradeCode")] public string? UpgradeCode { get; set; }
    [JsonPropertyName("installerType")] public string? InstallerType { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
}
