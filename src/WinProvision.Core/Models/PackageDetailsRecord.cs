using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Models;

/// <summary>Formato comum e versionado para os detalhes exibidos de qualquer pacote.</summary>
public sealed record PackageDetailsRecord
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
    [JsonPropertyName("packageId")] public string PackageId { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("tags")] public IReadOnlyList<string> Tags { get; init; } = [];
    [JsonPropertyName("publisher")] public string? Publisher { get; init; }
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("homepage")] public string? Homepage { get; init; }
    [JsonPropertyName("license")] public PackageLicenseDetails? License { get; init; }
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; init; }
    [JsonPropertyName("screenshots")] public IReadOnlyList<string> Screenshots { get; init; } = [];
    [JsonPropertyName("lastUpdated")] public DateTimeOffset? LastUpdated { get; init; }
    [JsonPropertyName("dependencies")] public IReadOnlyList<string> Dependencies { get; init; } = [];
    [JsonPropertyName("releaseNotes")] public PackageReleaseNotes? ReleaseNotes { get; init; }
    [JsonPropertyName("manifestUrl")] public string? ManifestUrl { get; init; }
    [JsonPropertyName("installer")] public PackageInstallerDetails? Installer { get; init; }
    [JsonPropertyName("extra")] public Dictionary<string, JsonElement> Extra { get; init; } = new(StringComparer.Ordinal);
}

public sealed record PackageLicenseDetails(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("url")] string? Url);

public sealed record PackageReleaseNotes(
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("url")] string? Url);

public sealed record PackageInstallerDetails(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("sha256")] string? Sha256,
    [property: JsonPropertyName("sizeBytes")] long? SizeBytes);

/// <summary>Metadados estáveis de apresentação e capacidades do gerenciador.</summary>
public sealed class PackageManagerDescriptor
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("source")] public string Source { get; init; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = string.Empty;
    [JsonIgnore] public string Description { get; init; } = string.Empty;
    [JsonPropertyName("displayNameKey")] public string? DisplayNameKey { get; init; }
    [JsonPropertyName("descriptionKey")] public string? DescriptionKey { get; init; }
    [JsonPropertyName("disabledReasonKey")] public string? DisabledReasonKey { get; init; }
    [JsonPropertyName("iconKey")] public string? IconKey { get; init; }
    [JsonPropertyName("iconGlyph")] public string? IconGlyph { get; init; }
    [JsonPropertyName("iconUri")] public string? IconUri { get; init; }
    [JsonPropertyName("parentId")] public string? ParentId { get; init; }
    [JsonPropertyName("showInSettings")] public bool ShowInSettings { get; init; }
    [JsonPropertyName("canToggle")] public bool CanToggle { get; init; }
    [JsonPropertyName("hasDetailsPage")] public bool HasDetailsPage { get; init; }
    [JsonPropertyName("sortOrder")] public int SortOrder { get; init; }
    [JsonPropertyName("check")] public PackageManagerCheckDefinition Check { get; init; } = new();
    [JsonPropertyName("commandLabel")] public string? CommandLabel { get; init; }
    [JsonPropertyName("manifestLabel")] public string? ManifestLabel { get; init; }
    [JsonPropertyName("versionOptionsLabel")] public string? VersionOptionsLabel { get; init; }
    [JsonPropertyName("globalOptionsLabel")] public string? GlobalOptionsLabel { get; init; }
    [JsonPropertyName("customArgumentsDescription")] public string? CustomArgumentsDescription { get; init; }
    [JsonPropertyName("optionsUnavailableMessage")] public string? OptionsUnavailableMessage { get; init; }
    [JsonPropertyName("loadsExtendedMetadata")] public bool LoadsExtendedMetadata { get; init; }
    [JsonPropertyName("supportedArchitectures")] public IReadOnlyList<string> SupportedArchitectures { get; init; } = [];
    [JsonPropertyName("capabilities")] public PackageManagerCapabilities Capabilities { get; init; } = new();
}

public sealed class PackageManagerCheckDefinition
{
    [JsonPropertyName("kind")] public string Kind { get; init; } = "none";
    [JsonPropertyName("probe")] public string? Probe { get; init; }
}

public sealed class PackageManagerCapabilities
{
    [JsonPropertyName("interactiveInstall")] public bool InteractiveInstall { get; init; }
    [JsonPropertyName("skipHashCheck")] public bool SkipHashCheck { get; init; }
    [JsonPropertyName("customVersion")] public bool CustomVersion { get; init; }
    [JsonPropertyName("versionSelector")] public bool VersionSelector { get; init; }
    [JsonPropertyName("versionOperations")] public string[] VersionOperations { get; init; } = [];
    [JsonPropertyName("runAsAdministrator")] public bool RunAsAdministrator { get; init; }
    [JsonPropertyName("overrideArguments")] public bool OverrideArguments { get; init; }
    [JsonPropertyName("installerInfo")] public bool InstallerInfo { get; init; }
    [JsonPropertyName("manifestLink")] public bool ManifestLink { get; init; }
    [JsonPropertyName("customArgs")] public bool CustomArgs { get; init; }
    [JsonPropertyName("preInstallScripts")] public bool PreInstallScripts { get; init; }
    [JsonPropertyName("postInstallScripts")] public bool PostInstallScripts { get; init; }
    [JsonPropertyName("closeRunningApps")] public bool CloseRunningApps { get; init; }
    [JsonPropertyName("selectArchitecture")] public bool SelectArchitecture { get; init; }
    [JsonPropertyName("selectScope")] public bool SelectScope { get; init; }
    [JsonPropertyName("selectInstallLocation")] public bool SelectInstallLocation { get; init; }
    [JsonPropertyName("uninstallPrevious")] public bool UninstallPrevious { get; init; }
    [JsonPropertyName("packageOptions")] public bool PackageOptions { get; init; }
    [JsonPropertyName("globalOptions")] public bool GlobalOptions { get; init; }

    public bool SupportsVersionSelection(string operation)
    {
        string normalized = operation.Trim().ToLowerInvariant() switch
        {
            "install" or "instalar" => "install",
            "update" or "upgrade" or "atualizar" => "update",
            "uninstall" or "desinstalar" => "uninstall",
            _ => operation
        };
        return VersionOperations is { Length: > 0 }
            ? VersionOperations.Contains(normalized, StringComparer.OrdinalIgnoreCase)
            : normalized is "install" or "update" ? VersionSelector || CustomVersion : false;
    }
}
