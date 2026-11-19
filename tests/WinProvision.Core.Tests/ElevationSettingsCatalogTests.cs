using System.Text.Json;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ElevationSettingsCatalogTests
{
    [Fact]
    public void ParallelOperationChoices_UseSharedConcurrencyLimits()
    {
        ElevationSettingDefinition setting = ElevationSettingsCatalog.Default.Settings
            .Single(candidate => candidate.StorageKey == "ParallelOperations");
        IReadOnlyList<ElevationSettingChoice> choices = Assert.IsAssignableFrom<IReadOnlyList<ElevationSettingChoice>>(setting.Options);
        Assert.Equal(OperationConcurrencyLimits.MinimumParallelOperations.ToString(), choices.First().Value);
        Assert.Equal(OperationConcurrencyLimits.MaximumParallelOperations.ToString(), choices.Last().Value);
        Assert.Equal(4, choices.Count);
    }

    [Fact]
    public void CatalogIgnoresUnknownFieldsAndAllowsSyntheticPreferenceKeys()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "extra": "ignored",
          "groups": [{
            "key": "synthetic-group",
            "titleTextKey": "group.synthetic",
            "searchCategoryKey": "Synthetic",
            "searchCategoryTextKey": "search.synthetic",
            "order": 0,
            "future": true
          }],
          "settings": [{
            "key": "invented.preference",
            "group": "synthetic-group",
            "kind": "preference",
            "risk": "dangerous",
            "defaultValue": false,
            "appliesImmediately": true,
            "requiresConfirmation": true,
            "titleTextKey": "title",
            "descriptionTextKey": "description",
            "riskWarningTextKey": "warning",
            "searchTerms": "invented searchable term",
            "futureField": 123
          }]
        }
        """;

        string? catalogLog = null;
        ElevationSettingsCatalog catalog = ElevationSettingsCatalog.Load(json, message => catalogLog = message);
        Assert.True(catalog.Settings.Count == 1, catalogLog);
        ElevationSettingDefinition setting = Assert.Single(catalog.Settings);
        Assert.Equal("invented.preference", setting.Key);
        Assert.Equal("invented searchable term", setting.SearchTerms);
        Assert.Equal("Synthetic", Assert.Single(catalog.Groups).SearchCategoryKey);
        Assert.True(ElevationSettingsCatalog.ResolvePresentation(
            setting,
            new Dictionary<string, bool>()).ShowToggle);
    }

    [Fact]
    public void InvalidJsonAndFutureSchemaFallBackToEmbeddedDefaults()
    {
        string? log = null;
        Assert.Same(ElevationSettingsCatalog.Default, ElevationSettingsCatalog.Load("{broken", message => log = message));
        Assert.Contains("padrão embutido", log, StringComparison.OrdinalIgnoreCase);
        Assert.Same(ElevationSettingsCatalog.Default,
            ElevationSettingsCatalog.Load("""{"schemaVersion":99,"groups":[],"settings":[]}"""));
    }

    [Fact]
    public void CatalogReadsDeclaredPreferenceAndDescriptionProviderIds()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "groups": [{ "key": "invented-group", "titleTextKey": "group", "page": "cache", "order": 0 }],
          "settings": [
            {
              "key": "invented-choice",
              "group": "invented-group",
              "kind": "preference",
              "control": "combo",
              "risk": "normal",
              "options": [{ "value": "a", "labelTextKey": "option.a" }],
              "defaultValue": "a",
              "storageKey": "invented-storage-field",
              "preferenceProviderId": "invented-preference-provider",
              "appliesImmediately": true,
              "requiresConfirmation": false,
              "titleTextKey": "choice",
              "descriptionTextKey": "choice.description"
            },
            {
              "key": "invented-action",
              "group": "invented-group",
              "kind": "info",
              "control": "action",
              "risk": "normal",
              "actionKey": "inventedAction",
              "actionTextKey": "action.run",
              "descriptionProviderId": "invented-description-provider",
              "titleTextKey": "action",
              "descriptionTextKey": "action.description"
            }
          ]
        }
        """;

        ElevationSettingsCatalog catalog = ElevationSettingsCatalog.Load(json);
        ElevationSettingDefinition[] settings = catalog.Settings.ToArray();
        Assert.Equal("invented-preference-provider",
            Assert.Single(settings, setting => setting.StorageKey == "invented-storage-field").PreferenceProviderId);
        Assert.Equal("invented-description-provider",
            Assert.Single(settings, setting => setting.ActionKey == "inventedAction").DescriptionProviderId);
    }

    [Fact]
    public void UnknownDependencyKeyIsIgnoredWithoutDroppingTheSetting()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "groups": [{ "key": "g", "titleTextKey": "g", "order": 0 }],
          "settings": [{
            "key": "synthetic-preference",
            "group": "g",
            "kind": "preference",
            "risk": "normal",
            "defaultValue": true,
            "dependsOn": { "key": "not-declared", "value": true },
            "appliesImmediately": true,
            "requiresConfirmation": false,
            "titleTextKey": "title",
            "descriptionTextKey": "description"
          }]
        }
        """;

        string? catalogLog = null;
        ElevationSettingsCatalog catalog = ElevationSettingsCatalog.Load(json, message => catalogLog = message);
        Assert.True(catalog.Settings.Count == 1, catalogLog);
        ElevationSettingDefinition setting = Assert.Single(catalog.Settings);
        Assert.Null(setting.DependsOn);
    }

    [Fact]
    public void CircularDependenciesAreDetectedAndRemoved()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "groups": [{ "key": "g", "titleTextKey": "g", "order": 0 }],
          "settings": [
            { "key": "synthetic-a", "group": "g", "kind": "preference", "risk": "normal", "defaultValue": true, "dependsOn": { "key": "synthetic-b", "value": true }, "appliesImmediately": true, "requiresConfirmation": false, "titleTextKey": "a", "descriptionTextKey": "a" },
            { "key": "synthetic-b", "group": "g", "kind": "preference", "risk": "normal", "defaultValue": false, "dependsOn": { "key": "synthetic-a", "value": true }, "appliesImmediately": true, "requiresConfirmation": false, "titleTextKey": "b", "descriptionTextKey": "b" }
          ]
        }
        """;

        ElevationSettingsCatalog catalog = ElevationSettingsCatalog.Load(json);
        Assert.All(catalog.Settings, setting => Assert.Null(setting.DependsOn));
    }

    [Fact]
    public void RenderingDependsOnCharacteristicsNotTheSettingKey()
    {
        ElevationSettingDefinition first = Preference("synthetic-one", "dangerous");
        ElevationSettingDefinition second = Preference("different-invented-key", "dangerous");
        var values = new Dictionary<string, bool>();

        Assert.Equal(
            ElevationSettingsCatalog.ResolvePresentation(first, values),
            ElevationSettingsCatalog.ResolvePresentation(second, values));
    }

    [Fact]
    public void EnforcedKindNeverRendersAToggle()
    {
        var enforced = new ElevationSettingDefinition(
            "synthetic-protection", "synthetic-group", "enforced", "normal", null, null,
            "locked", true, false, "title", "description", null);

        ElevationSettingPresentation presentation = ElevationSettingsCatalog.ResolvePresentation(
            enforced, new Dictionary<string, bool>());

        Assert.False(presentation.ShowToggle);
        Assert.False(presentation.IsEnabled);
    }

    [Fact]
    public void CatalogPresentationSupportsSyntheticToggleComboAndActionControls()
    {
        ElevationSettingDefinition firstCombo = ComboPreference("synthetic-choice-a");
        ElevationSettingDefinition secondCombo = firstCombo with { Key = "invented-choice-b" };
        ElevationSettingDefinition toggle = new(
            "invented-toggle-card", "synthetic-group", "preference", "normal",
            JsonDocument.Parse("false").RootElement.Clone(), null, null, true, false,
            "title", "description", null, "toggle-card", StorageKey: "SyntheticEnabled");
        ElevationSettingDefinition secondToggle = toggle with { Key = "invented-toggle-card-b" };
        ElevationSettingDefinition action = new(
            "invented-action", "synthetic-group", "info", "normal", default, null, null, true, false,
            "title", "description", null, "action", ActionKey: "syntheticAction", ActionTextKey: "action");
        ElevationSettingDefinition secondAction = action with { Key = "invented-action-b" };

        ElevationSettingPresentation first = ElevationSettingsCatalog.ResolvePresentation(
            firstCombo, new Dictionary<string, string>());
        ElevationSettingPresentation second = ElevationSettingsCatalog.ResolvePresentation(
            secondCombo, new Dictionary<string, string>());
        ElevationSettingPresentation togglePresentation = ElevationSettingsCatalog.ResolvePresentation(
            toggle, new Dictionary<string, string>());
        ElevationSettingPresentation secondTogglePresentation = ElevationSettingsCatalog.ResolvePresentation(
            secondToggle, new Dictionary<string, string>());
        ElevationSettingPresentation actionPresentation = ElevationSettingsCatalog.ResolvePresentation(
            action, new Dictionary<string, string>());
        ElevationSettingPresentation secondActionPresentation = ElevationSettingsCatalog.ResolvePresentation(
            secondAction, new Dictionary<string, string>());

        Assert.Equal(first, second);
        Assert.Equal(togglePresentation, secondTogglePresentation);
        Assert.Equal(actionPresentation, secondActionPresentation);
        Assert.True(first.ShowCombo);
        Assert.True(togglePresentation.ShowToggle);
        Assert.True(actionPresentation.ShowAction);
        Assert.True(actionPresentation.IsEnabled);
    }

    [Fact]
    public void NavigationPresentationUsesDeclaredKindAndTargetForSyntheticKeys()
    {
        ElevationSettingDefinition first = Navigation("invented-page-a");
        ElevationSettingDefinition second = Navigation("synthetic-page-b");

        ElevationSettingPresentation firstPresentation = ElevationSettingsCatalog.ResolvePresentation(
            first, new Dictionary<string, string>());
        ElevationSettingPresentation secondPresentation = ElevationSettingsCatalog.ResolvePresentation(
            second, new Dictionary<string, string>());

        Assert.True(firstPresentation.ShowNavigation);
        Assert.True(firstPresentation.IsEnabled);
        Assert.Equal(firstPresentation, secondPresentation);
    }

    [Fact]
    public void TextDependencyUsesTheSelectedValueWithoutDependingOnSettingKeyNames()
    {
        ElevationSettingDefinition parent = ComboPreference("invented-parent");
        ElevationSettingDefinition child = Preference("synthetic-child", "normal") with
        {
            DependsOn = new ElevationSettingCondition("invented-parent", null, ValueText: "weekly")
        };
        var settings = new[] { parent, child };

        ElevationSettingPresentation active = ElevationSettingsCatalog.ResolvePresentation(
            child, new Dictionary<string, string> { ["invented-parent"] = "weekly" }, settings: settings);
        ElevationSettingPresentation inactive = ElevationSettingsCatalog.ResolvePresentation(
            child, new Dictionary<string, string> { ["invented-parent"] = "daily" }, settings: settings);

        Assert.False(active.IsDimmed);
        Assert.True(active.IsEnabled);
        Assert.True(inactive.IsDimmed);
        Assert.False(inactive.IsEnabled);
    }

    [Fact]
    public void VisibilityConditionAcceptsMultipleCatalogValues()
    {
        ElevationSettingDefinition frequency = ComboPreference("synthetic-frequency");
        ElevationSettingDefinition calendarTime = Preference("synthetic-time", "normal") with
        {
            VisibleWhen = new ElevationSettingCondition("synthetic-frequency", null,
                Values: new[] { "daily", "weekly" })
        };
        var settings = new[] { frequency, calendarTime };

        ElevationSettingPresentation weekly = ElevationSettingsCatalog.ResolvePresentation(
            calendarTime, new Dictionary<string, string> { ["synthetic-frequency"] = "weekly" }, settings: settings);
        ElevationSettingPresentation interval = ElevationSettingsCatalog.ResolvePresentation(
            calendarTime, new Dictionary<string, string> { ["synthetic-frequency"] = "interval" }, settings: settings);

        Assert.True(weekly.IsVisible);
        Assert.False(interval.IsVisible);
    }

    [Fact]
    public void GenericPreferencesPersistAndKeepLegacyElevationValueInSync()
    {
        string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        string path = Path.Combine(folder, "elevation.json");
        try
        {
            var preferences = new ElevationPreferencesService(path);
            preferences.SetBoolean("invented.preference", true);
            preferences.SetAllowed(false);

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            Assert.False(document.RootElement.GetProperty("allowElevation").GetBoolean());
            var reloaded = new ElevationPreferencesService(path);
            Assert.True(reloaded.GetBoolean("invented.preference", false));
            Assert.False(reloaded.AllowElevation);
            Assert.True(reloaded.HasValue("invented.preference"));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static ElevationSettingDefinition Preference(string key, string risk) =>
        new(key, "synthetic-group", "preference", risk, false, null, null,
            true, true, "title", "description", "warning");

    private static ElevationSettingDefinition ComboPreference(string key) =>
        new(key, "synthetic-group", "preference", "normal",
            JsonDocument.Parse("\"weekly\"").RootElement.Clone(), null, null, true, false,
            "title", "description", null, "combo",
            (ElevationSettingChoice[])[new("daily", "daily"), new("weekly", "weekly")],
            StorageKey: "SyntheticChoice");

    private static ElevationSettingDefinition Navigation(string key) =>
        new(key, "synthetic-group", "navigation", "normal", default, null, null,
            true, false, "title", "description", null, "navigation",
            TargetPage: "SyntheticDestination");
}
