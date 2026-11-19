using WinProvision.Core.Models;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class PackageScreenCatalogTests
{
    private const string CatalogJson = "WinProvision.Core.Models.package-screen-catalog.json";

    [Fact]
    public void EmbeddedCatalogContainsFivePackageScreensAndResolvesSharedCatalogs()
    {
        PackageScreenCatalog catalog = PackageScreenCatalog.Default;

        Assert.Equal(5, catalog.Screens.Count);
        Assert.Equal("office", catalog.Get("office").Id);
        Assert.Equal("discover", catalog.Get("discover").Id);
        Assert.Equal("updates", catalog.Get("updates").Data.Provider);
        Assert.Equal("relevance", catalog.Get("discover").Defaults.Sort.Column);
        Assert.Contains(".portable", catalog.Get("discover").Ranking.NoiseSuffixes);
        Assert.Equal("Nome", catalog.GetColumn("name").Title);
        Assert.Equal("Recarregar", catalog.GetAction("refresh").Title);
        Assert.False(string.IsNullOrWhiteSpace(catalog.ResolveIcon("winget")));
    }

    [Fact]
    public void LoadAcceptsJsonOnlyFakeScreenWithoutChangingEmbeddedCatalog()
    {
        string catalogJson = ReadResource(CatalogJson);
        var screens = new Dictionary<string, string>
        {
            ["fake"] = """
                { "schemaVersion": 1, "id": "fake", "title": "Tela de teste", "icon": "discover",
                  "subtitle": "{filtered} de {total} {packageLabel}", "data": { "provider": "fake" },
                  "toolbar": { "primary": { "action": "refresh", "menu": [] }, "overflow": [] },
                  "sidebar": { "sections": [] }, "columns": ["name"], "selection": "multiple",
                  "defaults": { "view": "list", "sort": { "column": "name", "direction": "ascending" } } }
                """
        };

        PackageScreenCatalog catalog = PackageScreenCatalog.Load(screens, catalogJson);

        Assert.Equal("fake", catalog.Get("fake").Data.Provider);
        Assert.Equal("2 de 3 pacotes", catalog.FormatSubtitle(catalog.Get("fake"), 3, 2));
    }

    [Fact]
    public void LoadIgnoresLegacyPagingFieldsWhenReadingScreenOverrides()
    {
        string catalogJson = ReadResource(CatalogJson);
        var screens = new Dictionary<string, string>
        {
            ["fake"] = """
                { "schemaVersion": 1, "id": "fake", "title": "Tela de teste", "icon": "discover",
                  "subtitle": "{filtered} de {total} {packageLabel}", "data": { "provider": "fake" },
                  "toolbar": { "primary": { "action": "refresh", "menu": [] }, "overflow": [] },
                  "sidebar": { "sections": [] }, "columns": ["name"], "selection": "multiple",
                  "paging": { "pageSize": 24 }, "pagination": { "enabled": true },
                  "defaults": { "view": "list", "sort": { "column": "name", "direction": "ascending" } } }
                """
        };

        PackageScreenCatalog catalog = PackageScreenCatalog.Load(screens, catalogJson);

        Assert.Equal("fake", catalog.Get("fake").Id);
    }

    [Fact]
    public void LoadAcceptsPerViewFieldsAndJsonDeclaredRowActions()
    {
        const string screenJson = """
            {
              "schemaVersion": 1, "id": "fake", "title": "Tela de teste", "icon": "discover",
              "data": { "provider": "fake" },
              "toolbar": { "primary": { "action": "refresh" } },
              "views": {
                "list": { "columns": ["publisher", "version"] },
                "details": { "columns": ["name", "status"], "subtitle": "status" },
                "grid": { "card": ["publisher", "versionTransition"], "badge": "source" }
              },
              "row": { "tap": "select", "doubleTap": "details", "contextActions": ["export"] },
              "defaults": { "view": "list", "sort": { "column": "name", "direction": "ascending" } }
            }
            """;

        PackageScreenDefinition screen = PackageScreenCatalog.Load(
            new Dictionary<string, string> { ["fake"] = screenJson }, ReadResource(CatalogJson)).Get("fake");

        Assert.Equal(new[] { "publisher", "version" }, screen.Views.List.Columns);
        Assert.Equal(new[] { "name", "status" }, screen.Views.Details.Columns);
        Assert.Equal("status", screen.Views.Details.Subtitle);
        Assert.Equal(new[] { "publisher", "versionTransition" }, screen.Views.Grid.Card);
        Assert.Equal("source", screen.Views.Grid.Badge);
        Assert.Equal("details", screen.Row.DoubleTap);
        Assert.Equal(new[] { "export" }, screen.Row.ContextActions);
    }

    [Fact]
    public void LoadRejectsUnknownCardField()
    {
        const string screenJson = """
            {
              "schemaVersion": 1, "id": "fake", "title": "Tela de teste", "icon": "discover",
              "data": { "provider": "fake" },
              "toolbar": { "primary": { "action": "refresh" } },
              "views": {
                "list": { "columns": ["name"] },
                "details": { "columns": ["name"] },
                "grid": { "card": ["missing"] }
              },
              "row": { "contextActions": ["export"] },
              "defaults": { "view": "list", "sort": { "column": "name", "direction": "ascending" } }
            }
            """;

        Assert.Throws<System.Text.Json.JsonException>(() => PackageScreenCatalog.Load(
            new Dictionary<string, string> { ["fake"] = screenJson }, ReadResource(CatalogJson)));
    }

    [Fact]
    public void LoadRejectsUnknownRowAction()
    {
        const string screenJson = """
            {
              "schemaVersion": 1, "id": "fake", "title": "Tela de teste", "icon": "discover",
              "data": { "provider": "fake" },
              "toolbar": { "primary": { "action": "refresh" } },
              "views": {
                "list": { "columns": ["name"] },
                "details": { "columns": ["name"] },
                "grid": { "card": ["name"] }
              },
              "row": { "contextActions": ["missing"] },
              "defaults": { "view": "list", "sort": { "column": "name", "direction": "ascending" } }
            }
            """;

        Assert.Throws<System.Text.Json.JsonException>(() => PackageScreenCatalog.Load(
            new Dictionary<string, string> { ["fake"] = screenJson }, ReadResource(CatalogJson)));
    }

    [Theory]
    [InlineData("collapsed")]
    [InlineData("")]
    public void InvalidSidebarDefaultLogsWarningAndUsesDefault(string invalidDefault)
    {
        string json = $$"""
            { "schemaVersion": 1, "id": "invalid-default", "title": "Teste", "icon": "discover",
              "data": { "provider": "test" },
              "toolbar": { "primary": { "action": "refresh" } },
              "columns": ["name"],
              "sidebar": { "sections": [{ "id": "filters", "title": "Filtros", "default": "{{invalidDefault}}" }] } }
            """;
        var logs = new List<string>();

        PackageScreenCatalog catalog = PackageScreenCatalog.Load(
            new Dictionary<string, string> { ["invalid-default"] = json }, ReadResource(CatalogJson), logs.Add);

        Assert.Null(catalog.Get("invalid-default").Sidebar.Sections[0].Default);
        Assert.Contains(logs, message => message.Contains("default inválido", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OptionalSidebarPropertiesRetainDefaultsWithSourceGeneratedJson()
    {
        const string json = """
            { "schemaVersion": 1, "id": "minimal", "title": "Teste", "icon": "discover",
              "data": { "provider": "test" },
              "toolbar": { "primary": { "action": "refresh" } },
              "columns": ["name"],
              "sidebar": { "sections": [{ "id": "filters", "title": "Filtros" }] } }
            """;
        var catalog = PackageScreenCatalog.Load(new Dictionary<string, string> { ["minimal"] = json }, ReadResource(CatalogJson));
        var screen = catalog.Get("minimal");
        Assert.Empty(screen.Toolbar.Overflow);
        Assert.Empty(screen.Toolbar.Primary.Menu);
        Assert.Empty(screen.Sidebar.Sections[0].BulkActions);
        Assert.Empty(screen.Sidebar.Sections[0].Items);
        Assert.Equal("list", screen.Defaults.View);
        Assert.Equal("multiple", screen.Selection);
    }

    [Fact]
    public void LoadRejectsUnknownActionAndDuplicateScreenIds()
    {
        string catalogJson = ReadResource(CatalogJson);
        string valid = """
            { "schemaVersion": 1, "id": "fake", "title": "Tela", "icon": "discover", "data": { "provider": "fake" },
              "toolbar": { "primary": { "action": "refresh", "menu": [] }, "overflow": [] },
              "sidebar": { "sections": [] }, "columns": ["name"], "selection": "multiple",
              "defaults": { "view": "list", "sort": { "column": "name", "direction": "ascending" } } }
            """;
        string unknownAction = valid.Replace("\"refresh\"", "\"missing\"", StringComparison.Ordinal);
        Assert.Throws<System.Text.Json.JsonException>(() => PackageScreenCatalog.Load(new Dictionary<string, string> { ["fake"] = unknownAction }, catalogJson));
        Assert.Throws<System.Text.Json.JsonException>(() => PackageScreenCatalog.Load(new Dictionary<string, string> { ["fake"] = valid, ["FAKE"] = valid }, catalogJson));
        Assert.Throws<System.Text.Json.JsonException>(() => PackageScreenCatalog.Load(new Dictionary<string, string> { ["fake"] = valid.Replace("\"list\"", "\"unknown\"", StringComparison.Ordinal) }, catalogJson));
    }

    [Fact]
    public void OfficeUsesSharedPackageScreenContractAndValidatesDependencies()
    {
        PackageScreenDefinition office = PackageScreenCatalog.Default.Get("office");
        Assert.Equal("office", office.Data.Provider);
        Assert.True(office.Search.Enabled);
        Assert.Equal("officeApply", office.Toolbar.Primary.Action);
        Assert.Contains("state", office.Views.List.Columns);

        string catalogJson = ReadResource(CatalogJson);
        const string valid = """
            {"schemaVersion":1,"id":"fake","title":"Teste","icon":"discover","data":{"provider":"fake"},
             "toolbar":{"primary":{"action":"refresh"}},"columns":["name"],
             "sidebar":{"sections":[{"id":"settings","title":"Configuração","items":[
               {"id":"product","title":"Produto"},{"id":"channel","title":"Canal","dependsOn":{"key":"product","valueText":"x"}}]}]}}
            """;
        PackageScreenCatalog.Load(new Dictionary<string, string> { ["fake"] = valid }, catalogJson);
        string circular = valid.Replace("\"id\":\"product\",\"title\":\"Produto\"", "\"id\":\"product\",\"title\":\"Produto\",\"dependsOn\":{\"key\":\"channel\",\"valueText\":\"x\"}", StringComparison.Ordinal);
        Assert.Throws<System.Text.Json.JsonException>(() => PackageScreenCatalog.Load(new Dictionary<string, string> { ["fake"] = circular }, catalogJson));
        string invalid = valid.Replace("\"valueText\":\"x\"", "\"valueText\":\"x\",\"values\":[\"y\"]", StringComparison.Ordinal);
        Assert.Throws<System.Text.Json.JsonException>(() => PackageScreenCatalog.Load(new Dictionary<string, string> { ["fake"] = invalid }, catalogJson));
        string restrictedSort = valid.Replace("\"columns\":[\"name\"]", "\"columns\":[\"name\"],\"sortColumns\":[\"name\"]", StringComparison.Ordinal);
        Assert.Single(PackageScreenCatalog.Load(new Dictionary<string, string> { ["fake"] = restrictedSort }, catalogJson).Get("fake").SortColumns);
        string invalidSort = valid.Replace("\"columns\":[\"name\"]", "\"columns\":[\"name\"],\"sortColumns\":[\"versionTransition\"]", StringComparison.Ordinal);
        Assert.Throws<System.Text.Json.JsonException>(() => PackageScreenCatalog.Load(new Dictionary<string, string> { ["fake"] = invalidSort }, catalogJson));
    }

    private static string ReadResource(string name)
    {
        using Stream stream = typeof(PackageScreenCatalog).Assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
