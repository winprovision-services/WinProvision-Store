using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

internal sealed record MainShellNavigationItem(
    string Id,
    string Label,
    string Icon,
    string TargetType,
    string TargetId,
    string Placement,
    string Kind = "navigation",
    IReadOnlyList<MainShellMenuItem>? Children = null,
    string? IconUriLight = null,
    string? IconUriDark = null);

internal sealed record MainShellMenuItem(string Type, string Id, string Label, string? Icon,
    string? CommandId, string? TargetType, string? TargetId, string? Shortcut,
    WinProvision.Core.Services.ElevationSettingCondition? DependsOn, string? TargetSection = null);

internal sealed class MainShellCommandRegistry
{
    private readonly Dictionary<string, Func<Task>> _commands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<bool>> _enabled = new(StringComparer.OrdinalIgnoreCase);
    public void Register(string id, Func<Task> action, Func<bool>? enabled = null)
    { _commands[id] = action; _enabled[id] = enabled ?? (() => true); }
    public bool TryGet(string id, out Func<Task> action) => _commands.TryGetValue(id, out action!);
    public bool IsEnabled(string id) => _enabled.TryGetValue(id, out Func<bool>? enabled) && enabled();
}

internal sealed record MainShellSearchDefinition(
    string Placement,
    double MinWidth,
    double MaxWidth,
    string Shortcut,
    IReadOnlyDictionary<string, TitleBarSearchPageDefinition> Pages);

internal sealed record MainShellDefinition(
    string Title,
    double OpenPaneLength,
    bool StartsOpen,
    IReadOnlyList<MainShellNavigationItem> Items,
    MainShellSearchDefinition Search)
{
    private const int SupportedSchemaVersion = 1;
    private const string ResourceName = "WinProvision.Store.Assets.main-shell.json";

    public static MainShellDefinition LoadEmbedded(Action<string>? log = null)
    {
        using Stream stream = typeof(MainShellDefinition).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException($"Descritor da janela não encontrado: {ResourceName}");
        using var reader = new StreamReader(stream);
        return Load(ResolveRuntimeText(reader.ReadToEnd()), log);
    }

    internal static MainShellDefinition Load(
        string json,
        Action<string>? log = null,
        Func<string, bool>? screenExists = null,
        Func<string, bool>? pageExists = null,
        Func<string, string?>? resolveIcon = null)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException exception)
        {
            Warn(log, $"JSON inválido no descritor da janela; usando padrão embutido. {exception.Message}");
            return Load(ResolveRuntimeText(DefaultJson), log, screenExists, pageExists, resolveIcon);
        }
        using (document)
        {
        JsonElement root = document.RootElement;
        if (!TryReadInt(root, "schemaVersion", out int schemaVersion) || schemaVersion != SupportedSchemaVersion)
        {
            Warn(log, $"Versão do descritor da janela não suportada; esperado schemaVersion {SupportedSchemaVersion}.");
            return Load(ResolveRuntimeText(DefaultJson), log, screenExists, pageExists, resolveIcon);
        }

        string title = ReadString(root, "title") ?? "WinProvision Store";
        JsonElement navigation = root.TryGetProperty("navigation", out JsonElement value) ? value : default;
        JsonElement pane = navigation.ValueKind == JsonValueKind.Object
            && navigation.TryGetProperty("pane", out value) ? value : default;
        double paneLength = pane.ValueKind == JsonValueKind.Object
            && pane.TryGetProperty("openLength", out value) && value.TryGetDouble(out double length)
            && double.IsFinite(length) && length is >= 180 and <= 600
                ? length
                : 300;
        bool startsOpen = pane.ValueKind == JsonValueKind.Object && pane.TryGetProperty("startsOpen", out value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : true;
        JsonElement search = root.TryGetProperty("search", out value) ? value : default;
        pageExists ??= id => id is "settings" or "package-managers" or "operation-history" or "office";
        string searchPlacement = ReadString(search, "placement") ?? "titlebar";
        double searchMinWidth = ReadDouble(search, "minWidth", 320);
        double searchMaxWidth = ReadDouble(search, "maxWidth", 480);
        string searchShortcut = ReadString(search, "shortcut") ?? "Ctrl+F";
        if (searchPlacement != "titlebar" || searchMinWidth is < 200 or > 800
            || searchMaxWidth < searchMinWidth || searchMaxWidth > 1000
            || !searchShortcut.Equals("Ctrl+F", StringComparison.OrdinalIgnoreCase))
        {
            Warn(log, "Configuração de pesquisa da janela inválida; usando os padrões.");
            searchPlacement = "titlebar";
            searchMinWidth = 320;
            searchMaxWidth = 480;
            searchShortcut = "Ctrl+F";
        }
        var pageSearch = new Dictionary<string, TitleBarSearchPageDefinition>(StringComparer.OrdinalIgnoreCase);
        if (search.ValueKind == JsonValueKind.Object
            && search.TryGetProperty("pages", out JsonElement pageSearches)
            && pageSearches.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty page in pageSearches.EnumerateObject())
            {
                JsonElement config = page.Value;
                if (config.ValueKind != JsonValueKind.Object)
                {
                    Warn(log, $"Configuração de pesquisa inválida ignorada para a página '{page.Name}'.");
                    continue;
                }
                bool enabled = !config.TryGetProperty("enabled", out JsonElement enabledValue)
                    || enabledValue.ValueKind != JsonValueKind.False;
                string? placeholder = ReadString(config, "placeholder");
                string? trigger = ReadString(config, "trigger");
                int debounceMs = ReadInt(config, "debounceMs", 0);
                int minLength = ReadInt(config, "minLength", 0);
                bool keepQuery = !config.TryGetProperty("keepQuery", out JsonElement keepValue)
                    || keepValue.ValueKind != JsonValueKind.False;

                if (!pageExists(page.Name) || placeholder is null || trigger is not ("live" or "submit")
                    || debounceMs is < 0 or > 5000 || minLength is < 0 or > 256)
                {
                    Warn(log, $"Configuração de pesquisa inválida ignorada para a página '{page.Name}'.");
                    continue;
                }

                pageSearch[page.Name] = new(enabled, placeholder, trigger, debounceMs, minLength, keepQuery);
            }
        }

        screenExists ??= id => PackageScreenCatalog.Default.Screens.Any(screen =>
            screen.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        resolveIcon ??= key =>
        {
            string glyph = PackageScreenCatalog.Default.ResolveIcon(key);
            return string.IsNullOrWhiteSpace(glyph) ? null : glyph;
        };

        var items = new List<MainShellNavigationItem>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (navigation.ValueKind == JsonValueKind.Object
            && navigation.TryGetProperty("items", out JsonElement itemArray)
            && itemArray.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in itemArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    Warn(log, "Item inválido ignorado no menu.");
                    continue;
                }
                string? id = ReadString(item, "id");
                string? label = ReadString(item, "label");
                string? icon = ReadString(item, "icon");
                string? iconUriLight = ReadIconUri(item, "iconUriLight", id, log);
                string? iconUriDark = ReadIconUri(item, "iconUriDark", id, log);
                string? placement = ReadString(item, "placement");
                string kind = ReadString(item, "kind") ?? "navigation";
                JsonElement target = item.TryGetProperty("target", out value) ? value : default;
                string? targetType = ReadString(target, "type");
                string? targetId = ReadString(target, "id");
                bool visible = !item.TryGetProperty("visible", out value)
                    || value.ValueKind != JsonValueKind.False;

                if (!visible)
                    continue;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(label)
                    || string.IsNullOrWhiteSpace(icon)
                    || placement is not ("top" or "bottom")
                    || !ids.Add(id))
                {
                    Warn(log, $"Item inválido ou duplicado ignorado no menu: '{id ?? "(sem id)"}'.");
                    continue;
                }

                if (kind == "flyout")
                {
                    if (resolveIcon(icon) is null)
                    {
                        Warn(log, $"Item '{id}' ignorado: ícone inválido.");
                        continue;
                    }
                    var children = ReadChildren(item, screenExists, pageExists, resolveIcon, log);
                    items.Add(new(id, label, icon, string.Empty, string.Empty, placement, kind, children));
                    continue;
                }
                if (kind != "navigation" || string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId))
                {
                    Warn(log, $"Item '{id}' ignorado: tipo ou destino inválido.");
                    continue;
                }
                bool targetExists = targetType switch { "screen" => screenExists(targetId), "page" => pageExists(targetId), _ => false };
                if (!targetExists || resolveIcon(icon) is null)
                {
                    Warn(log, $"Item '{id}' ignorado: referência de tela, página ou ícone inválida.");
                    continue;
                }

                items.Add(new(id, label, icon, targetType, targetId, placement,
                    IconUriLight: iconUriLight, IconUriDark: iconUriDark));
            }
        }

        if (paneLength == 300 && pane.ValueKind == JsonValueKind.Object
            && pane.TryGetProperty("openLength", out value)
            && (!value.TryGetDouble(out double configuredLength) || !double.IsFinite(configuredLength)
                || configuredLength is < 180 or > 600))
            Warn(log, "Largura do painel inválida; usando 300.");

        return new(title, paneLength, startsOpen, items,
            new(searchPlacement, searchMinWidth, searchMaxWidth, searchShortcut, pageSearch));
        }
    }

    private static IReadOnlyList<MainShellMenuItem> ReadChildren(JsonElement item,
        Func<string, bool> screenExists, Func<string, bool> pageExists,
        Func<string, string?> resolveIcon, Action<string>? log)
    {
        if (!item.TryGetProperty("items", out JsonElement children) || children.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<MainShellMenuItem>();
        foreach (JsonElement child in children.EnumerateArray())
        {
            string? type = ReadString(child, "type"), id = ReadString(child, "id"), label = ReadString(child, "label");
            if (child.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(type)
                || type is not ("navigation" or "command" or "info" or "separator")
                || (type != "separator" && string.IsNullOrWhiteSpace(id)))
            { Warn(log, "Item inválido ou tipo desconhecido ignorado no flyout."); continue; }
            if (child.TryGetProperty("visible", out JsonElement visible) && visible.ValueKind == JsonValueKind.False)
                continue;
            string? icon = ReadString(child, "icon"), commandId = ReadString(child, "commandId");
            JsonElement target = child.TryGetProperty("target", out JsonElement targetElement) ? targetElement : default;
            string? targetType = ReadString(target, "type"), targetId = ReadString(target, "id");
            string? targetSection = ReadString(target, "section");
            WinProvision.Core.Services.ElevationSettingCondition? condition = null;
            if (child.TryGetProperty("dependsOn", out JsonElement depends))
            {
                condition = ReadCondition(depends);
                if (condition is null || !WinProvision.Core.Services.ElevationSettingConditionEvaluator.IsValid(condition))
                { Warn(log, $"Condição inválida ignorada no item '{id}'."); continue; }
            }
            bool valid = type switch
            {
                "separator" => true,
                "info" => !string.IsNullOrWhiteSpace(label),
                "command" => !string.IsNullOrWhiteSpace(label) && !string.IsNullOrWhiteSpace(commandId),
                    "navigation" => !string.IsNullOrWhiteSpace(label) && targetId is not null
                    && (targetType == "screen" ? screenExists(targetId) : targetType == "page" && pageExists(targetId)),
                _ => false
            };
            if (!valid || (icon is not null && resolveIcon(icon) is null))
            { Warn(log, $"Item '{id}' inválido ou com referência inexistente ignorado no flyout."); continue; }
            result.Add(new(type, id ?? string.Empty, label ?? string.Empty, icon, commandId,
                targetType, targetId, ReadString(child, "shortcut"), condition, targetSection));
        }
        return result;
    }

    private static WinProvision.Core.Services.ElevationSettingCondition? ReadCondition(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        string? key = ReadString(element, "key"), risk = ReadString(element, "risk"), valueText = ReadString(element, "valueText");
        bool? value = element.TryGetProperty("value", out JsonElement valueElement)
            && valueElement.ValueKind is JsonValueKind.True or JsonValueKind.False ? valueElement.GetBoolean() : null;
        bool? differs = element.TryGetProperty("differsFromDefault", out JsonElement differsElement)
            && differsElement.ValueKind is JsonValueKind.True or JsonValueKind.False ? differsElement.GetBoolean() : null;
        int? minimum = element.TryGetProperty("minimumCount", out JsonElement minimumElement)
            && minimumElement.TryGetInt32(out int count) ? count : null;
        IReadOnlyList<string>? values = null;
        if (element.TryGetProperty("values", out JsonElement valuesElement))
        {
            if (valuesElement.ValueKind != JsonValueKind.Array) return null;
            var entries = new List<string>();
            foreach (JsonElement entry in valuesElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String) return null;
                entries.Add(entry.GetString() ?? string.Empty);
            }
            values = entries;
        }
        return new(key, value, risk, differs, minimum, valueText, values);
    }

    private static string ResolveRuntimeText(string json) => json.Replace(
        "{version}", AppUpdateService.GetCurrentVersion().ToString(3), StringComparison.Ordinal);

    private const string DefaultJson = """
    {
      "schemaVersion": 1,
      "id": "main-shell",
      "title": "shell.title",
      "search": { "placement": "titlebar", "minWidth": 320, "maxWidth": 480, "shortcut": "Ctrl+F", "pages": { "operation-history": { "enabled": true, "placeholder": "Pesquisar histórico de operações", "trigger": "live", "debounceMs": 0, "minLength": 0, "keepQuery": false } } },
      "navigation": {
        "pane": { "openLength": 300, "startsOpen": true },
        "items": [
          { "id": "discover", "label": "shell.navigation.discover", "icon": "discover", "target": { "type": "screen", "id": "discover" }, "placement": "top" },
          { "id": "updates", "label": "shell.navigation.updates", "icon": "updates", "target": { "type": "screen", "id": "updates" }, "placement": "top" },
          { "id": "installed", "label": "shell.navigation.installed", "icon": "installed", "target": { "type": "screen", "id": "installed" }, "placement": "top" },
          { "id": "collections", "label": "shell.navigation.collections", "icon": "collections", "target": { "type": "screen", "id": "collections" }, "placement": "top" },
          { "id": "office", "label": "shell.navigation.office", "icon": "office", "iconUriLight": "ms-appx:///Assets/Icons/microsoft-office.svg", "iconUriDark": "ms-appx:///Assets/Icons/microsoft-office.svg", "target": { "type": "page", "id": "office" }, "placement": "top" },
          { "id": "settings", "label": "shell.navigation.settings", "icon": "settings", "target": { "type": "page", "id": "settings" }, "placement": "bottom" },
          { "id": "package-managers", "label": "shell.navigation.packageManagers", "icon": "installed", "target": { "type": "page", "id": "package-managers" }, "placement": "bottom" },
          { "id": "more-menu", "kind": "flyout", "label": "Mais", "icon": "details", "placement": "bottom", "items": [
            { "type": "command", "id": "logs-folder", "label": "Abrir pasta de logs", "icon": "openFolder", "commandId": "open-logs" },
            { "type": "navigation", "id": "history", "label": "Histórico de operações", "icon": "history", "target": { "type": "page", "id": "operation-history" } },
            { "type": "separator", "id": "separator-updates" },
            { "type": "info", "id": "version", "label": "WinProvision Store · Versão {version}" },
            { "type": "command", "id": "release-notes", "label": "Notas de lançamento", "icon": "megaphone", "commandId": "open-release" },
            { "type": "command", "id": "check-updates", "label": "Verificar atualizações", "icon": "update", "commandId": "check-updates" },
            { "type": "separator", "id": "separator-exit" },
            { "type": "command", "id": "about", "label": "Sobre", "icon": "infoCircle", "commandId": "open-about" },
            { "type": "separator", "id": "separator-before-exit" },
            { "type": "command", "id": "exit", "label": "Sair do WinProvision Store", "icon": "dismissCircle", "commandId": "exit" }
          ] }
        ]
      }
    }
    """;

    private static double ReadDouble(JsonElement element, string property, double fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.TryGetDouble(out double number)
        && double.IsFinite(number)
            ? number
            : fallback;

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadIconUri(JsonElement item, string property, string? itemId, Action<string>? log)
    {
        string? value = ReadString(item, property);
        if (value is null) return null;
        if (Uri.TryCreate(value, UriKind.Absolute, out Uri? uri)
            && uri.Scheme.Equals("ms-appx", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.StartsWith("/Assets/Icons/", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            return value;
        Warn(log, $"Fonte de ícone inválida ignorada no item '{itemId ?? "(sem id)"}': {property}.");
        return null;
    }

    private static bool TryReadInt(JsonElement element, string property, out int result)
    {
        result = default;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(property, out JsonElement value)
            && value.TryGetInt32(out result);
    }

    private static int ReadInt(JsonElement element, string property, int fallback) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.TryGetInt32(out int number)
            ? number
            : fallback;

    private static void Warn(Action<string>? log, string message)
    {
        if (log is not null)
            log(message);
        else
            WinProvisionLog.Warning(new InvalidDataException(message), "MainShellCatalog");
    }
}
