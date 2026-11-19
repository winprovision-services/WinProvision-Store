using System.Text.Json;
using WinProvision.Core.Services;

namespace WinProvision.Core.Models;

/// <summary>Catálogo embedded dos contratos declarativos de telas de pacotes.</summary>
public sealed class PackageScreenCatalog
{
    private const string CatalogResource = "WinProvision.Core.Models.package-screen-catalog.json";
    private readonly IReadOnlyDictionary<string, PackageScreenDefinition> _screens;
    private readonly IReadOnlyDictionary<string, PackageScreenActionDefinition> _actions;
    private readonly IReadOnlyDictionary<string, PackageScreenColumnDefinition> _columns;
    private readonly IReadOnlyDictionary<string, string> _icons;
    private readonly IReadOnlyDictionary<string, PackageScreenIconSources> _iconSources;
    private readonly IReadOnlyDictionary<string, Dictionary<string, string>> _translations;

    private static readonly Lazy<PackageScreenCatalog> DefaultCatalog = new(LoadEmbedded);
    public static PackageScreenCatalog Default => DefaultCatalog.Value;
    public IReadOnlyList<PackageScreenDefinition> Screens => _screens.Values.OrderBy(screen => screen.Id, StringComparer.Ordinal).ToArray();
    public IReadOnlyDictionary<string, PackageScreenActionDefinition> Actions => _actions;
    public IReadOnlyDictionary<string, PackageScreenColumnDefinition> Columns => _columns;

    private PackageScreenCatalog(IReadOnlyDictionary<string, PackageScreenDefinition> screens,
        IReadOnlyDictionary<string, PackageScreenActionDefinition> actions,
        IReadOnlyDictionary<string, PackageScreenColumnDefinition> columns,
        IReadOnlyDictionary<string, string> icons,
        IReadOnlyDictionary<string, PackageScreenIconSources> iconSources,
        IReadOnlyDictionary<string, Dictionary<string, string>> translations)
    {
        _screens = screens;
        _actions = actions;
        _columns = columns;
        _icons = icons;
        _iconSources = iconSources;
        _translations = translations;
    }

    public PackageScreenDefinition Get(string id) => _screens.TryGetValue(id, out PackageScreenDefinition? screen)
        ? screen : throw new KeyNotFoundException($"Tela de pacotes '{id}' não encontrada.");

    public string GetText(string key, string? fallback = null, string language = "pt-BR")
    {
        if (_translations.TryGetValue(language, out Dictionary<string, string>? values) && values.TryGetValue(key, out string? text)) return text;
        if (_translations.TryGetValue("pt-BR", out values) && values.TryGetValue(key, out text)) return text;
        return fallback ?? key;
    }

    public string FormatSubtitle(PackageScreenDefinition screen, int total, int filtered, int selected = 0) =>
        screen.Subtitle.Replace("{total}", total.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("{filtered}", filtered.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
            .Replace("{selected}", selected.ToString(System.Globalization.CultureInfo.CurrentCulture), StringComparison.Ordinal)
        .Replace("{packageLabel}", GetText(total == 1 ? "packages.one" : "packages.many", total == 1 ? "pacote" : "pacotes"), StringComparison.Ordinal);

    public PackageScreenColumnDefinition GetColumn(string id) => _columns.TryGetValue(id, out PackageScreenColumnDefinition? column)
        ? column : throw new KeyNotFoundException($"Coluna '{id}' não encontrada.");

    public PackageScreenActionDefinition GetAction(string id) => _actions.TryGetValue(id, out PackageScreenActionDefinition? action)
        ? action : throw new KeyNotFoundException($"Ação '{id}' não encontrada.");

    /// <summary>Resolve ícones de gerenciadores a partir de PackageManagerCatalog e ícones de ação pelo catálogo compartilhado.</summary>
    public string ResolveIcon(string key)
    {
        PackageManagerDescriptor? manager = PackageManagerCatalog.Default.Managers.FirstOrDefault(item =>
            item.Id.Equals(key, StringComparison.OrdinalIgnoreCase) || string.Equals(item.IconKey, key, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(manager?.IconGlyph)) return manager.IconGlyph;
        return _icons.TryGetValue(key, out string? glyph) ? glyph : string.Empty;
    }

    public PackageScreenIconSources? ResolveIconSources(string key) =>
        _iconSources.TryGetValue(key, out PackageScreenIconSources? sources) ? sources : null;

    public static PackageScreenCatalog Load(IReadOnlyDictionary<string, string> screenJson, string catalogJson, Action<string>? log = null)
    {
        using JsonDocument catalogDocument = JsonDocument.Parse(catalogJson);
        JsonElement root = catalogDocument.RootElement;
        if (ReadInt(root, "schemaVersion") != 1) throw new JsonException("Versão do catálogo de telas inválida.");
        Dictionary<string, string> icons = ReadStringMap(root, "icons");
        Dictionary<string, PackageScreenIconSources> iconSources = ReadIconSources(root);
        Dictionary<string, PackageScreenActionDefinition> actions = ReadMap(root, "actions", item =>
            JsonSerializer.Deserialize(item.GetRawText(), CoreJsonContext.Default.PackageScreenActionDefinition)
                ?? throw new JsonException("Ação vazia no catálogo."));
        Dictionary<string, PackageScreenColumnDefinition> columns = ReadMap(root, "columns", item =>
            JsonSerializer.Deserialize(item.GetRawText(), CoreJsonContext.Default.PackageScreenColumnDefinition)
                ?? throw new JsonException("Coluna vazia no catálogo."));
        foreach (PackageScreenActionDefinition action in actions.Values) ValidateIcon(action.Icon, icons);
        foreach (PackageScreenColumnDefinition column in columns.Values)
            if (string.IsNullOrWhiteSpace(column.Title) || column.Width is <= 0) throw new JsonException($"Coluna inválida '{column.Id}'.");
        var translations = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("resources", out JsonElement resources) && resources.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty locale in resources.EnumerateObject())
                translations[locale.Name] = locale.Value.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.String)
                    .ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

        var screens = new Dictionary<string, PackageScreenDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach ((string id, string json) in screenJson)
        {
            PackageScreenDefinition screen = JsonSerializer.Deserialize(json, CoreJsonContext.Default.PackageScreenDefinition)
                ?? throw new JsonException($"JSON inválido para a tela '{id}'.");
            Validate(screen, id, actions, columns, icons, log);
            if (!screens.TryAdd(screen.Id, screen)) throw new JsonException($"Tela repetida '{screen.Id}'.");
        }
        if (screens.Count == 0) throw new JsonException("Catálogo sem telas.");
        return new PackageScreenCatalog(screens, actions, columns, icons, iconSources, translations);
    }

    private static void Validate(PackageScreenDefinition screen, string expectedId,
        IReadOnlyDictionary<string, PackageScreenActionDefinition> actions,
        IReadOnlyDictionary<string, PackageScreenColumnDefinition> columns,
        IReadOnlyDictionary<string, string> icons,
        Action<string>? log)
    {
        if (screen.Search is null || screen.Data is null || screen.Toolbar?.Primary is null || screen.Sidebar?.Sections is null
            || screen.Views is null || screen.Views.List is null || screen.Views.Details is null || screen.Views.Grid is null
            || screen.Row is null
            || screen.Defaults?.Sort is null || screen.Columns is null
            || (screen.Status is not null && screen.Status.Fields is null)
            || screen.Views.List.Columns is null || screen.Views.Details.Columns is null || screen.Views.Grid.Columns is null
            || screen.Views.Grid.Card is null || screen.Row.ContextActions is null
            || screen.Toolbar.Overflow is null || screen.Toolbar.Primary.Menu is null
            || screen.Sidebar.Sections.Any(section => section is null || section.Items is null
                || section.BulkActions is null || section.Modes is null))
            throw new JsonException($"Campos obrigatórios nulos na tela '{expectedId}'.");
        if (screen.SchemaVersion != 1 || !string.Equals(screen.Id, expectedId, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(screen.Title) || string.IsNullOrWhiteSpace(screen.Data.Provider))
            throw new JsonException($"Definição de tela inválida: '{expectedId}'.");
        ValidateIconUri(screen.IconUriLight, expectedId);
        ValidateIconUri(screen.IconUriDark, expectedId);
        if (screen.Search.Trigger is not ("live" or "submit")
            || string.IsNullOrWhiteSpace(screen.Search.Placeholder)
            || screen.Search.DebounceMs is < 0 or > 5000
            || screen.Search.MinLength is < 0 or > 256)
            throw new JsonException($"Configuração de pesquisa inválida na tela '{expectedId}'.");
        if (screen.Sidebar.Width is <= 0 or > 500) throw new JsonException($"Largura lateral inválida na tela '{expectedId}'.");
        ValidateIcon(screen.Icon, icons);
        if (screen.Selection is not ("single" or "multiple" or "none")) throw new JsonException("Modo de seleção inválido.");
        if (screen.Defaults.Sort.Direction is not ("ascending" or "descending")) throw new JsonException("Direção de ordenação inválida.");
        if (screen.Defaults.View is not ("list" or "grid" or "details")) throw new JsonException("Visualização padrão inválida.");
        if (screen.Row.Tap is not ("select" or "none")) throw new JsonException("Ação de clique da linha inválida.");
        if (screen.SortColumns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != screen.SortColumns.Count)
            throw new JsonException($"Coluna de ordenação repetida na tela '{expectedId}'.");
        foreach (string sortColumn in screen.SortColumns)
        {
            ValidateColumn(sortColumn, columns);
            if (!columns[sortColumn].Sortable) throw new JsonException($"Coluna '{sortColumn}' não pode ser ordenada.");
        }
        if (screen.SortColumns.Count > 0 && !screen.SortColumns.Contains(screen.Defaults.Sort.Column, StringComparer.OrdinalIgnoreCase))
            throw new JsonException($"Ordenação padrão não disponível na tela '{expectedId}'.");
        var actionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var screenActions = new[] { screen.Toolbar.Primary.Action }.Concat(screen.Toolbar.Primary.Menu).Concat(screen.Toolbar.Overflow);
        foreach (string actionId in screenActions)
        {
            if (!actions.ContainsKey(actionId)) throw new JsonException($"Ação desconhecida '{actionId}'.");
            if (!actionIds.Add(actionId)) throw new JsonException($"Ação repetida '{actionId}'.");
        }
        if (screen.Row.DoubleTap is { } doubleTap && !actions.ContainsKey(doubleTap))
            throw new JsonException($"Ação de duplo clique desconhecida '{doubleTap}'.");
        if (screen.Status?.RefreshAction is { } refreshAction && !actions.ContainsKey(refreshAction))
            throw new JsonException($"Ação de recarga de estado desconhecida '{refreshAction}'.");
        foreach (string action in screen.Row.ContextActions)
            if (!actions.ContainsKey(action)) throw new JsonException($"Ação contextual desconhecida '{action}'.");

        static void ValidateColumn(string id, IReadOnlyDictionary<string, PackageScreenColumnDefinition> catalogColumns)
        {
            if (!catalogColumns.TryGetValue(id, out PackageScreenColumnDefinition? column))
                throw new JsonException($"Coluna desconhecida '{id}'.");
            if (column.CellTemplate is not ("text" or "package" or "source" or "version-transition" or "status"))
                throw new JsonException($"Template de célula desconhecido na coluna '{id}'.");
        }

        static void ValidateColumnList(IReadOnlyList<string> ids, IReadOnlyDictionary<string, PackageScreenColumnDefinition> catalogColumns)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in ids)
            {
                ValidateColumn(id, catalogColumns);
                if (!seen.Add(id)) throw new JsonException($"Coluna repetida '{id}'.");
            }
        }

        ValidateColumnList(screen.Columns, columns);
        ValidateColumnList(screen.Views.List.Columns, columns);
        ValidateColumnList(screen.Views.Details.Columns, columns);
        ValidateColumnList(screen.Views.Grid.Columns, columns);
        ValidateColumnList(screen.Views.Grid.Card, columns);
        ValidateColumnList(screen.SortColumns, columns);
        if (screen.Views.Details.Subtitle is { } subtitle)
            ValidateColumn(subtitle, columns);
        if (screen.Views.Grid.Badge is { } badge
            && (!columns.TryGetValue(badge, out PackageScreenColumnDefinition? badgeColumn)
                || badgeColumn.CellTemplate != "source"))
            throw new JsonException("Badge de cartão deve referenciar uma coluna de origem.");
        if (!screen.Views.List.Columns.Concat(screen.Views.Details.Columns).Concat(screen.Columns).Concat(screen.SortColumns)
            .Contains(screen.Defaults.Sort.Column, StringComparer.OrdinalIgnoreCase))
            throw new JsonException("Coluna da ordenação padrão não está na tela.");
        foreach (PackageScreenSidebarSectionDefinition section in screen.Sidebar.Sections)
        {
            if (section.Default is not null
                && !section.Default.Equals("expanded", StringComparison.OrdinalIgnoreCase)
                && !section.Default.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                string message = $"Valor default inválido '{section.Default}' na seção '{section.Id}' da tela '{expectedId}'; usando o padrão.";
                if (log is not null) log(message);
                else CoreLog.Warning(new JsonException(message), "PackageScreenCatalog.InvalidSidebarDefault");
                section.Default = null;
            }
            foreach (string action in section.BulkActions)
                if (!actions.ContainsKey(action)) throw new JsonException($"Ação em lote desconhecida '{action}'.");
            foreach (PackageScreenSidebarItemDefinition item in section.Items) ValidateIcon(item.Icon, icons);
        }
        var sidebarItems = screen.Sidebar.Sections.SelectMany(section => section.Items).ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (PackageScreenSidebarItemDefinition item in sidebarItems.Values)
        {
            if (item.DependsOn is { } condition
                && (!WinProvision.Core.Services.ElevationSettingConditionEvaluator.IsValid(condition)
                    || string.IsNullOrWhiteSpace(condition.Key)
                    || (!sidebarItems.ContainsKey(condition.Key) && !condition.Key.StartsWith("$row:", StringComparison.OrdinalIgnoreCase))))
                throw new JsonException($"Condição dependsOn inválida em '{item.Id}'.");
        }
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool HasCycle(string id)
        {
            if (!visiting.Add(id)) return true;
            if (visited.Contains(id)) { visiting.Remove(id); return false; }
            string? dependency = sidebarItems[id].DependsOn?.Key;
            if (dependency is not null && sidebarItems.ContainsKey(dependency) && HasCycle(dependency)) return true;
            visiting.Remove(id); visited.Add(id); return false;
        }
        foreach (string id in sidebarItems.Keys)
            if (HasCycle(id)) throw new JsonException($"Dependência circular em '{id}'.");
    }

    private static void ValidateIcon(string? key, IReadOnlyDictionary<string, string> icons)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        bool managerIcon = PackageManagerCatalog.Default.Managers.Any(item => item.Id.Equals(key, StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.IconKey, key, StringComparison.OrdinalIgnoreCase));
        if (!managerIcon && !icons.ContainsKey(key)) throw new JsonException($"Ícone desconhecido '{key}'.");
    }

    private static void ValidateIconUri(string? value, string screenId)
    {
        if (value is null) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals("ms-appx", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/Assets/Icons/", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            throw new JsonException($"Fonte de ícone inválida na tela '{screenId}'.");
    }

    private static void ValidateIconUri(string? value)
    {
        if (value is null) return;
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            || !uri.Scheme.Equals("ms-appx", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/Assets/Icons/", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            throw new JsonException("Fonte de ícone inválida; use um SVG local de Assets/Icons.");
    }

    private static string? ReadOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static PackageScreenCatalog LoadEmbedded()
    {
        try
        {
            var screens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string resourceName in typeof(PackageScreenCatalog).Assembly.GetManifestResourceNames()
                .Where(name => name.StartsWith("WinProvision.Core.Models.package-screens.", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            {
                using Stream stream = typeof(PackageScreenCatalog).Assembly.GetManifestResourceStream(resourceName)
                    ?? throw new FileNotFoundException($"Recurso embedded ausente: {resourceName}");
                using var reader = new StreamReader(stream);
                screens.Add(resourceName["WinProvision.Core.Models.package-screens.".Length..^5], reader.ReadToEnd());
            }
            using Stream catalogStream = typeof(PackageScreenCatalog).Assembly.GetManifestResourceStream(CatalogResource)
                ?? throw new FileNotFoundException("Catálogo compartilhado de telas não encontrado.");
            using var catalogReader = new StreamReader(catalogStream);
            return Load(screens, catalogReader.ReadToEnd());
        }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "PackageScreenCatalog.LoadEmbedded");
            throw new InvalidOperationException("Não foi possível carregar os contratos de telas de pacotes embedded.", exception);
        }
    }

    private static Dictionary<string, T> ReadMap<T>(JsonElement root, string property, Func<JsonElement, T> parse) where T : class
    {
        if (!root.TryGetProperty(property, out JsonElement array) || array.ValueKind != JsonValueKind.Array)
            throw new JsonException($"Catálogo '{property}' inválido.");
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in array.EnumerateArray())
        {
            T value = parse(item);
            string id = item.GetProperty("id").GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(id) || !result.TryAdd(id, value)) throw new JsonException($"ID inválido ou repetido em '{property}': '{id}'.");
        }
        return result;
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement obj) || obj.ValueKind != JsonValueKind.Object) throw new JsonException($"Catálogo '{property}' inválido.");
        return obj.EnumerateObject().Where(item => item.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, PackageScreenIconSources> ReadIconSources(JsonElement root)
    {
        var result = new Dictionary<string, PackageScreenIconSources>(StringComparer.OrdinalIgnoreCase);
        if (!root.TryGetProperty("iconSources", out JsonElement sources)) return result;
        if (sources.ValueKind != JsonValueKind.Object) throw new JsonException("Catálogo 'iconSources' inválido.");
        foreach (JsonProperty property in sources.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name) || property.Value.ValueKind != JsonValueKind.Object)
                throw new JsonException("Definição de fonte de ícone inválida.");
            string? light = ReadOptionalString(property.Value, "light");
            string? dark = ReadOptionalString(property.Value, "dark");
            ValidateIconUri(light);
            ValidateIconUri(dark);
            if (light is null && dark is null) throw new JsonException($"Fonte de ícone '{property.Name}' sem variantes.");
            if (!result.TryAdd(property.Name, new(light, dark)))
                throw new JsonException($"Fonte de ícone repetida '{property.Name}'.");
        }
        return result;
    }

    private static int ReadInt(JsonElement root, string property) => root.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int number)
        ? number : throw new JsonException($"Campo '{property}' inválido.");
}

public sealed record PackageScreenIconSources(string? Light, string? Dark);
