using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record SettingSearchResult(string Key, string Title, string Description, string Category, string CategoryKey, string SearchTerms)
{
    public string AccessibleName => $"{Title}, {Category}";
}

public sealed partial class SettingsViewModel
{
    public ObservableCollection<SettingSearchResult> SearchResults { get; } = new();
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    public bool HasNoSearchResults => IsSearching && SearchResults.Count == 0;
    public string SearchResultSummary => $"{SearchResults.Count} opções encontradas. Selecione para abrir essa configuração.";

    partial void OnSearchTextChanged(string value)
    {
        SearchResults.Clear();
        if (IsSearching)
        {
            string[] terms = Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (SettingSearchResult entry in GetCatalogSearchEntries())
                if (terms.All(term => Normalize($"{entry.Title} {entry.Description} {entry.Category} {entry.SearchTerms}").Contains(term, StringComparison.Ordinal)))
                    SearchResults.Add(entry);
        }
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(HasNoSearchResults));
        OnPropertyChanged(nameof(SearchResultSummary));
    }

    private IEnumerable<SettingSearchResult> GetCatalogSearchEntries()
    {
        ElevationSettingsCatalog catalog = _elevationCatalog.Catalog;
        var groups = catalog.Groups.ToDictionary(group => group.Key, StringComparer.OrdinalIgnoreCase);
        foreach (ElevationSettingDefinition setting in catalog.Settings)
        {
            if (!groups.TryGetValue(setting.Group, out ElevationSettingsGroup? group)
                || string.IsNullOrWhiteSpace(group.SearchCategoryKey))
                continue;

            string optionLabels = string.Join(' ', (setting.Options ?? [])
                .Select(option => _elevationCatalog.ResolveText(option.LabelTextKey)));
            yield return new SettingSearchResult(
                setting.Key,
                _elevationCatalog.ResolveText(setting.TitleTextKey),
                _elevationCatalog.ResolveText(setting.DescriptionTextKey),
                _elevationCatalog.ResolveText(group.SearchCategoryTextKey ?? group.TitleTextKey),
                group.SearchCategoryKey,
                $"{_elevationCatalog.ResolveText(group.TitleTextKey)} {optionLabels} {setting.EffectiveControl} {setting.Kind} {setting.SearchTerms}");
        }
        if (!_applicationPreferences.OfficeModuleEnabled) yield break;
        var officeCatalogService = new OfficePageCatalogService();
        ElevationSettingsCatalog office = officeCatalogService.Catalog;
        var officeGroups = office.Groups.ToDictionary(group => group.Key, StringComparer.OrdinalIgnoreCase);
        foreach (ElevationSettingDefinition setting in office.GetSettingsForPage("office"))
        {
            if (!officeGroups.TryGetValue(setting.Group, out ElevationSettingsGroup? group))
                continue;
            string options = string.Join(' ', (setting.Options ?? []).Select(option => officeCatalogService.ResolveText(option.LabelTextKey)));
            yield return new SettingSearchResult(setting.Key, officeCatalogService.ResolveText(setting.TitleTextKey),
                officeCatalogService.ResolveText(setting.DescriptionTextKey), officeCatalogService.ResolveText(group.SearchCategoryTextKey ?? group.TitleTextKey),
                group.SearchCategoryKey ?? "Office", $"{options} {setting.SearchTerms}");
        }
    }

    private static string Normalize(string value) =>
        new(value.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .Select(char.ToLowerInvariant)
            .ToArray());
}
