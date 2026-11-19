using CommunityToolkit.Mvvm.ComponentModel;

namespace WinProvision.Store.ViewModels;

/// <summary>Manager-neutral package row projected for the shared screen.</summary>
[WinRT.GeneratedBindableCustomProperty]
public partial class PackageScreenRow : ObservableObject
{
    [ObservableProperty] public partial string Key { get; set; } = string.Empty;
    [ObservableProperty] public partial string Name { get; set; } = string.Empty;
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty] public partial string Version { get; set; } = string.Empty;
    [ObservableProperty] public partial string? NewVersion { get; set; }
    [ObservableProperty] public partial string Source { get; set; } = string.Empty;
    [ObservableProperty] public partial string? SourceGlyph { get; set; }
    [ObservableProperty] public partial string? SourceIconUri { get; set; }
    [ObservableProperty] public partial string? SourceIconUriDark { get; set; }
    [ObservableProperty] public partial string? IconUri { get; set; }
    [ObservableProperty] public partial string? IconUriDark { get; set; }
    [ObservableProperty] public partial string? IconFallbackUri { get; set; }
    [ObservableProperty] public partial bool ShowPackageIcon { get; set; } = true;
    [ObservableProperty] public partial string? IconGlyph { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsWorking { get; set; }
    [ObservableProperty] public partial bool IsInstalled { get; set; }
    [ObservableProperty] public partial bool CanSelect { get; set; } = true;
    [ObservableProperty] public partial string? SelectionHelpText { get; set; }
    [ObservableProperty] public partial string SelectionGroup { get; set; } = "default";
    [ObservableProperty] public partial string? Status { get; set; }

    /// <summary>Original manager row, kept opaque to the presentation shell for routed commands.</summary>
    public object? Payload { get; init; }

    [ObservableProperty] public partial IReadOnlyDictionary<string, string?> Values { get; set; } =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    public string? GetValue(string columnId)
    {
        if (Values.TryGetValue(columnId, out string? value)) return value;
        return columnId.ToLowerInvariant() switch
        {
            "name" => Name,
            "id" => Id,
            "version" => Version,
            "newversion" => NewVersion,
            "source" => Source,
            "status" => Status,
            _ => null
        };
    }
}
