using System.Text.Json.Serialization;

namespace WinProvision.Core.Models;

/// <summary>Contrato declarativo de uma tela de pacotes, sem dependência de UI.</summary>
public sealed record PackageScreenDefinition
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("icon")] public string Icon { get; set; } = string.Empty;
    [JsonPropertyName("iconUriLight")] public string? IconUriLight { get; set; }
    [JsonPropertyName("iconUriDark")] public string? IconUriDark { get; set; }
    [JsonPropertyName("subtitle")] public string Subtitle { get; set; } = string.Empty;
    [JsonPropertyName("search")] public PackageScreenSearchDefinition Search { get; set; } = new();
    [JsonPropertyName("data")] public PackageScreenDataDefinition Data { get; set; } = new();
    [JsonPropertyName("toolbar")] public PackageScreenToolbarDefinition Toolbar { get; set; } = new();
    [JsonPropertyName("sidebar")] public PackageScreenSidebarDefinition Sidebar { get; set; } = new();
    [JsonPropertyName("views")] public PackageScreenViewsDefinition Views { get; set; } = new();
    [JsonPropertyName("row")] public PackageScreenRowDefinition Row { get; set; } = new();
    [JsonPropertyName("columns")] public IReadOnlyList<string> Columns { get; set; } = [];
    [JsonPropertyName("sortColumns")] public IReadOnlyList<string> SortColumns { get; set; } = [];
    [JsonPropertyName("selection")] public string Selection { get; set; } = "multiple";
    [JsonPropertyName("defaults")] public PackageScreenDefaultsDefinition Defaults { get; set; } = new();
    [JsonPropertyName("status")] public PackageScreenStatusDefinition? Status { get; set; }
    [JsonPropertyName("ranking")] public PackageSearchRankingDefinition Ranking { get; set; } = new();
}

public sealed record PackageSearchRankingDefinition
{
    [JsonPropertyName("sources")] public Dictionary<string, double> Sources { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("scoreWeight")] public double ScoreWeight { get; set; }
    [JsonPropertyName("starsWeight")] public double StarsWeight { get; set; }
    [JsonPropertyName("downloadsWeight")] public double DownloadsWeight { get; set; }
    [JsonPropertyName("storeRatingWeight")] public double StoreRatingWeight { get; set; }
    [JsonPropertyName("curatedIconBonus")] public double CuratedIconBonus { get; set; }
    [JsonPropertyName("metadataCompletenessWeight")] public double MetadataCompletenessWeight { get; set; }
    [JsonPropertyName("sourceMatchAdvantageThreshold")] public int SourceMatchAdvantageThreshold { get; set; } = 1;
    [JsonPropertyName("derivedPenalty")] public double DerivedPenalty { get; set; }
    [JsonPropertyName("duplicatePenalty")] public double DuplicatePenalty { get; set; }
    [JsonPropertyName("noiseSuffixes")] public IReadOnlyList<string> NoiseSuffixes { get; set; } = [];
}

public sealed record PackageScreenStatusDefinition
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = string.Empty;
    [JsonPropertyName("fields")] public IReadOnlyList<string> Fields { get; set; } = [];
    [JsonPropertyName("refreshAction")] public string? RefreshAction { get; set; }
}

public sealed record PackageScreenSearchDefinition
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }
    [JsonPropertyName("placeholder")] public string Placeholder { get; set; } = "search.placeholder";
    [JsonPropertyName("trigger")] public string Trigger { get; set; } = "live";
    [JsonPropertyName("debounceMs")] public int DebounceMs { get; set; }
    [JsonPropertyName("minLength")] public int MinLength { get; set; }
    [JsonPropertyName("keepQuery")] public bool KeepQuery { get; set; } = true;
}

public sealed record PackageScreenViewsDefinition
{
    [JsonPropertyName("list")] public PackageScreenViewDefinition List { get; set; } = new();
    [JsonPropertyName("details")] public PackageScreenViewDefinition Details { get; set; } = new();
    [JsonPropertyName("grid")] public PackageScreenViewDefinition Grid { get; set; } = new();
}

public sealed record PackageScreenViewDefinition
{
    [JsonPropertyName("columns")] public IReadOnlyList<string> Columns { get; set; } = [];
    [JsonPropertyName("card")] public IReadOnlyList<string> Card { get; set; } = [];
    [JsonPropertyName("subtitle")] public string? Subtitle { get; set; }
    [JsonPropertyName("badge")] public string? Badge { get; set; }
}

public sealed record PackageScreenRowDefinition
{
    [JsonPropertyName("tap")] public string Tap { get; set; } = "select";
    [JsonPropertyName("doubleTap")] public string? DoubleTap { get; set; } = "details";
    [JsonPropertyName("contextActions")] public IReadOnlyList<string> ContextActions { get; set; } = [];
}

public sealed record PackageScreenDataDefinition
{
    [JsonPropertyName("provider")] public string Provider { get; set; } = string.Empty;
}

public sealed record PackageScreenToolbarDefinition
{
    [JsonPropertyName("primary")] public PackageScreenPrimaryActionDefinition Primary { get; set; } = new();
    [JsonPropertyName("overflow")] public IReadOnlyList<string> Overflow { get; set; } = [];
}

public sealed record PackageScreenPrimaryActionDefinition
{
    [JsonPropertyName("action")] public string Action { get; set; } = string.Empty;
    [JsonPropertyName("menu")] public IReadOnlyList<string> Menu { get; set; } = [];
}

public sealed record PackageScreenSidebarDefinition
{
    [JsonPropertyName("sections")] public IReadOnlyList<PackageScreenSidebarSectionDefinition> Sections { get; set; } = [];
    [JsonPropertyName("width")] public double? Width { get; set; }
}

public sealed record PackageScreenSidebarSectionDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("type")] public string Type { get; set; } = "filters";
    [JsonPropertyName("bulkActions")] public IReadOnlyList<string> BulkActions { get; set; } = [];
    [JsonPropertyName("items")] public IReadOnlyList<PackageScreenSidebarItemDefinition> Items { get; set; } = [];
    [JsonPropertyName("modes")] public IReadOnlyList<string> Modes { get; set; } = [];
    [JsonPropertyName("default")] public string? Default { get; set; }
}

public sealed record PackageScreenSidebarItemDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "toggle";
    [JsonPropertyName("storageKey")] public string? StorageKey { get; set; }
    [JsonPropertyName("optionsSource")] public string? OptionsSource { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("applyCommand")] public string? ApplyCommand { get; set; }
    [JsonPropertyName("dependsOn")] public WinProvision.Core.Services.ElevationSettingCondition? DependsOn { get; set; }
    [JsonPropertyName("options")] public IReadOnlyList<PackageScreenSidebarOptionDefinition> Options { get; set; } = [];
    [JsonPropertyName("default")] public string? Default { get; set; }
}

public sealed record PackageScreenSidebarOptionDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
}

public sealed record PackageScreenSortDefinition
{
    [JsonPropertyName("column")] public string Column { get; set; } = "name";
    [JsonPropertyName("direction")] public string Direction { get; set; } = "ascending";
}

public sealed record PackageScreenDefaultsDefinition
{
    [JsonPropertyName("view")] public string View { get; set; } = "list";
    [JsonPropertyName("sort")] public PackageScreenSortDefinition Sort { get; set; } = new();
}

public sealed record PackageScreenActionDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("icon")] public string? Icon { get; set; }
    [JsonPropertyName("confirm")] public bool Confirm { get; set; }
    [JsonPropertyName("requiresSelection")] public bool RequiresSelection { get; set; }
    [JsonPropertyName("requiresValidConfiguration")] public bool RequiresValidConfiguration { get; set; }
}

public sealed record PackageScreenColumnDefinition
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
    [JsonPropertyName("width")] public double? Width { get; set; }
    [JsonPropertyName("cellTemplate")] public string CellTemplate { get; set; } = "text";
    [JsonPropertyName("sortable")] public bool Sortable { get; set; } = true;
}
