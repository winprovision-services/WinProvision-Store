using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Models;

public sealed class LanIndexDocument
{
    [JsonPropertyName("schema")] public string Schema { get; set; } = "winprovision.lan";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("generatedAt")] public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("modules")] public List<LanModuleDescriptor> Modules { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class LanModuleDescriptor
{
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("version")] public int Version { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class LanInstallersModuleDocument
{
    [JsonPropertyName("schema")] public string Schema { get; set; } = "winprovision.lan.installers";
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("installers")] public List<LanInstallerEntry> Installers { get; set; } = [];
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class LanInstallerEntry
{
    [JsonPropertyName("file")] public string File { get; set; } = string.Empty;
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = string.Empty;
    [JsonPropertyName("packageId")] public string PackageId { get; set; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; set; } = string.Empty;
    [JsonPropertyName("installerType")] public string? InstallerType { get; set; }
    [JsonPropertyName("silentArgs")] public string? SilentArgs { get; set; }
    [JsonPropertyName("silentSource")] public string? SilentSource { get; set; }
    [JsonPropertyName("scope")] public string? Scope { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed class LanInstallerSidecar
{
    [JsonPropertyName("packageId")] public string? PackageId { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("installerType")] public string? InstallerType { get; set; }
    [JsonPropertyName("silentArgs")] public string? SilentArgs { get; set; }
    [JsonPropertyName("silentSource")] public string? SilentSource { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

public sealed record LanInstallerAvailability(LanInstallerEntry Installer, bool SilentSupported, string? UnsupportedReason);

public sealed record LanRequestLog(DateTimeOffset Timestamp, string IpAddress, string File, int StatusCode);

public sealed record LanServedInstaller(string File, string Name, string? Version, string InstallerType);

public sealed record LanNetworkAddress(string InterfaceName, string Address, bool IsVirtual);
