using System.Text.Json.Serialization;

namespace WinProvision.Core.Models;

/// <summary>Modelo de runtime neutro para renderizar e editar configurações de qualquer gerenciador.</summary>
public sealed record PackageManagerSettingsDescriptor
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; init; } = 2;
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("displayName")] public string DisplayName { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; init; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; init; } = true;
    [JsonPropertyName("status")] public PackageManagerSettingsStatus Status { get; init; } = new();
    [JsonPropertyName("fields")] public IReadOnlyList<PackageManagerSettingsField> Fields { get; init; } = [];
    [JsonPropertyName("sources")] public PackageManagerSettingsSources Sources { get; init; } = new();
    [JsonPropertyName("actions")] public PackageManagerSettingsActions Actions { get; init; } = new();
    [JsonPropertyName("notes")] public IReadOnlyList<PackageManagerSettingsNote> Notes { get; init; } = [];
}

public sealed record PackageManagerSettingsStatus
{
    [JsonPropertyName("state")] public string State { get; init; } = "notChecked";
    [JsonPropertyName("message")] public string Message { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("executable")] public string? Executable { get; init; }
    [JsonPropertyName("executableEditable")] public bool ExecutableEditable { get; init; }
    [JsonPropertyName("checkedAt")] public DateTimeOffset? CheckedAt { get; init; }
}

public sealed record PackageManagerSettingsField
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; init; } = "toggle";
    [JsonPropertyName("section")] public string Section { get; init; } = "install";
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("value")] public string? Value { get; init; }
    [JsonPropertyName("defaultValue")] public string? DefaultValue { get; init; }
    [JsonPropertyName("placeholder")] public string? Placeholder { get; init; }
    [JsonPropertyName("pathKind")] public string? PathKind { get; init; }
    [JsonPropertyName("choices")] public IReadOnlyList<PackageManagerSettingsChoice> Choices { get; init; } = [];
}

public sealed record PackageManagerSettingsChoice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label);

public sealed record PackageManagerSettingsSource(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("removable")] bool Removable);

public sealed record PackageManagerSettingsSources
{
    [JsonPropertyName("supported")] public bool Supported { get; init; }
    [JsonPropertyName("canAdd")] public bool CanAdd { get; init; }
    [JsonPropertyName("canRemove")] public bool CanRemove { get; init; }
    [JsonPropertyName("canRefresh")] public bool CanRefresh { get; init; }
    [JsonPropertyName("canReset")] public bool CanReset { get; init; }
    [JsonPropertyName("items")] public IReadOnlyList<PackageManagerSettingsSource> Items { get; init; } = [];
}

public sealed record PackageManagerSettingsActions
{
    [JsonPropertyName("supported")] public bool Supported { get; init; }
    [JsonPropertyName("items")] public IReadOnlyList<PackageManagerSettingsAction> Items { get; init; } = [];
}

public sealed record PackageManagerSettingsAction(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("confirm")] bool Confirm = false,
    [property: JsonPropertyName("section")] string Section = "related");

public sealed record PackageManagerSettingsNote(
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("text")] string Text);
