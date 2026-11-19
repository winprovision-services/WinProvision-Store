using System.Text.Json;
using System.Reflection;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

internal sealed record AboutDialogLink(string LabelKey, string Destination);
internal sealed record AboutDialogListItem(string? TextKey, string? Name, string? Purpose, string? License,
    string? LicenseUrl, string? SourceUrl, string? Warning);
internal sealed record AboutDialogBlock(string Type, string? TextKey = null, string? TitleKey = null,
    string? Severity = null, string? Icon = null, string? ProviderId = null,
    IReadOnlyList<AboutDialogLink>? Links = null, IReadOnlyList<AboutDialogListItem>? Items = null);
internal sealed record AboutDialogTab(string Id, string LabelKey, IReadOnlyList<AboutDialogBlock> Blocks, string? Footer = null);

internal sealed class AboutValueProviderRegistry
{
    private readonly Dictionary<string, Func<string>> _providers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string id, Func<string> value) => _providers[id] = value;
    public bool Contains(string id) => _providers.ContainsKey(id);
    public bool TryResolve(string id, out string value)
    {
        if (_providers.TryGetValue(id, out Func<string>? provider))
        {
            value = provider();
            return true;
        }
        value = string.Empty;
        return false;
    }

    public static AboutValueProviderRegistry CreateDefault()
    {
        var providers = new AboutValueProviderRegistry();
        providers.Register("app-version", () => AppUpdateService.GetCurrentVersion().ToString(3));
        return providers;
    }
}

internal sealed class AboutContentCatalog
{
    private const string CatalogResourceName = "WinProvision.Store.Assets.about-content.json";
    private const string TextResourceName = "WinProvision.Store.Assets.Localization.about.pt-BR.json";
    private const int SupportedSchemaVersion = 1;
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "winprovision-services.github.io", "github.com", "community.chocolatey.org", "learn.microsoft.com",
        "www.microsoft.com", "dashboardicons.com", "commons.wikimedia.org", "mscloudlogos.com"
    };

    private const string DefaultJson = """
        {"schemaVersion":1,"tabs":[{"id":"overview","label":"about.tab.overview","blocks":[{"type":"heading","text":"about.product.name"},{"type":"subtitle","text":"about.product.subtitle"},{"type":"paragraph","text":"about.product.description"},{"type":"valueLine","text":"about.version.line","providerId":"app-version"},{"type":"notice","severity":"warning","title":"about.legal.title","text":"about.legal.text","icon":"infoCircle"},{"type":"heading","text":"about.links.heading"},{"type":"links","items":[{"label":"about.links.home","destination":"https://winprovision-services.github.io/WinProvision-Store/"},{"label":"about.links.repository","destination":"https://github.com/winprovision-services/WinProvision-Store"},{"label":"about.links.report","destination":"https://github.com/winprovision-services/WinProvision-Store/issues"}]}]}]}
        """;

    private static readonly IReadOnlyDictionary<string, string> DefaultTexts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["about.dialog.title"] = "Sobre o WinProvision Store", ["about.tab.overview"] = "Sobre",
        ["about.product.name"] = "WinProvision Store", ["about.product.subtitle"] = "Gerencie e provisione aplicativos no Windows com WinGet",
        ["about.product.description"] = "Pesquise aplicativos, confira versões e gerencie instalações e atualizações do WinGet em um só lugar.",
        ["about.version.line"] = "Você instalou a versão {value} do WinProvision Store", ["about.legal.title"] = "Aviso legal",
        ["about.legal.text"] = "O WinProvision Store não é afiliado, associado, autorizado, endossado ou patrocinado pela Microsoft Corporation nem pelo projeto WinGet. Microsoft e WinGet são marcas de seus respectivos titulares.",
        ["about.links.heading"] = "Links úteis", ["about.links.home"] = "Página inicial", ["about.links.repository"] = "Repositório",
        ["about.links.report"] = "Relatar problema ou solicitar recurso", ["about.close"] = "Fechar"
    };

    private AboutContentCatalog(IReadOnlyList<AboutDialogTab> tabs, IReadOnlyDictionary<string, string> texts)
    { Tabs = tabs; Texts = texts; }

    public IReadOnlyList<AboutDialogTab> Tabs { get; }
    public IReadOnlyDictionary<string, string> Texts { get; }
    public string GetText(string key) => Texts.TryGetValue(key, out string? text) ? text : DefaultTexts.GetValueOrDefault(key, key);

    public static AboutContentCatalog LoadEmbedded(Action<string>? log = null)
    {
        Assembly assembly = typeof(AboutContentCatalog).Assembly;
        string json = ReadEmbedded(assembly, CatalogResourceName, DefaultJson, log);
        string text = ReadEmbedded(assembly, TextResourceName, "{}", log);
        return Load(json, text, AboutValueProviderRegistry.CreateDefault(), log);
    }

    internal static AboutContentCatalog Load(string json, string textJson,
        AboutValueProviderRegistry providers, Action<string>? log = null)
    {
        IReadOnlyList<AboutDialogTab> tabs;
        try { tabs = ParseTabs(json, providers, log); }
        catch (JsonException exception)
        {
            Warn(log, $"JSON inválido no conteúdo Sobre; usando o padrão embutido. {exception.Message}");
            tabs = ParseTabs(DefaultJson, providers, log);
        }
        if (tabs.Count == 0)
        {
            Warn(log, "Nenhuma aba válida no conteúdo Sobre; usando o padrão embutido.");
            tabs = ParseTabs(DefaultJson, providers, log);
        }

        var texts = new Dictionary<string, string>(DefaultTexts, StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument document = JsonDocument.Parse(textJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String)
                        texts[property.Name] = property.Value.GetString() ?? string.Empty;
        }
        catch (JsonException exception) { Warn(log, $"Recursos Sobre inválidos; usando textos pt-BR embutidos. {exception.Message}"); }
        return new(tabs, texts);
    }

    internal static bool TryValidateDestination(string destination, out Uri? uri)
    {
        if (Uri.TryCreate(destination, UriKind.Absolute, out uri)
            && uri.Scheme is "https" or "http"
            && string.IsNullOrEmpty(uri.UserInfo)
            && uri.IsDefaultPort
            && AllowedHosts.Contains(uri.Host))
            return true;
        uri = null;
        return false;
    }

    private static IReadOnlyList<AboutDialogTab> ParseTabs(string json,
        AboutValueProviderRegistry providers, Action<string>? log)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !TryInt(root, "schemaVersion", out int version) || version != SupportedSchemaVersion)
            throw new JsonException("schemaVersion incompatível.");
        if (!root.TryGetProperty("tabs", out JsonElement tabsElement) || tabsElement.ValueKind != JsonValueKind.Array)
            throw new JsonException("A lista de abas está ausente.");

        var tabs = new List<AboutDialogTab>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement tab in tabsElement.EnumerateArray())
        {
            string? id = ReadString(tab, "id"), label = ReadString(tab, "label");
            if (tab.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(label)
                || !ids.Add(id) || !tab.TryGetProperty("blocks", out JsonElement blocks) || blocks.ValueKind != JsonValueKind.Array)
            { Warn(log, "Aba inválida ou repetida ignorada no conteúdo Sobre."); continue; }

            var parsed = new List<AboutDialogBlock>();
            foreach (JsonElement block in blocks.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object)
                { Warn(log, "Bloco inválido ignorado no conteúdo Sobre."); continue; }
                string? type = ReadString(block, "type"), textKey = ReadString(block, "text"), titleKey = ReadString(block, "title");
                if (type is not ("heading" or "subtitle" or "paragraph" or "valueLine" or "notice" or "links" or "list" or "separator"))
                { Warn(log, $"Tipo de bloco desconhecido '{type ?? "(vazio)"}' ignorado no conteúdo Sobre."); continue; }
                string? providerId = ReadString(block, "providerId");
                if (type == "valueLine" && (string.IsNullOrWhiteSpace(providerId) || !providers.Contains(providerId)))
                { Warn(log, $"Provedor desconhecido '{providerId ?? "(vazio)"}' ignorado no conteúdo Sobre."); continue; }
                string? severity = ReadString(block, "severity");
                if (type == "notice" && (severity is not ("info" or "warning") || string.IsNullOrWhiteSpace(textKey) || string.IsNullOrWhiteSpace(titleKey)))
                { Warn(log, "Aviso inválido ignorado no conteúdo Sobre."); continue; }
                if (type is "heading" or "subtitle" or "paragraph" or "valueLine" && string.IsNullOrWhiteSpace(textKey))
                { Warn(log, $"Bloco '{type}' sem chave de texto ignorado no conteúdo Sobre."); continue; }

                List<AboutDialogLink>? links = null;
                if (type == "links")
                {
                    links = [];
                    if (block.TryGetProperty("items", out JsonElement linkItems) && linkItems.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement link in linkItems.EnumerateArray())
                        {
                            string? linkLabel = ReadString(link, "label"), destination = ReadString(link, "destination");
                            if (string.IsNullOrWhiteSpace(linkLabel) || string.IsNullOrWhiteSpace(destination)
                                || !TryValidateDestination(destination, out _))
                            { Warn(log, "Link inválido ignorado no conteúdo Sobre."); continue; }
                            links.Add(new(linkLabel, destination));
                        }
                    if (links.Count == 0) continue;
                }
                List<AboutDialogListItem>? items = null;
                if (type == "list")
                {
                    items = [];
                    if (block.TryGetProperty("items", out JsonElement listItems) && listItems.ValueKind == JsonValueKind.Array)
                        foreach (JsonElement listItem in listItems.EnumerateArray())
                        {
                            if (listItem.ValueKind == JsonValueKind.String)
                                items.Add(new(listItem.GetString(), null, null, null, null, null, null));
                            else if (listItem.ValueKind == JsonValueKind.Object)
                            {
                                string? licenseUrl = ReadString(listItem, "licenseUrl"), sourceUrl = ReadString(listItem, "sourceUrl");
                                if ((!string.IsNullOrWhiteSpace(licenseUrl) && !TryValidateDestination(licenseUrl, out _))
                                    || (!string.IsNullOrWhiteSpace(sourceUrl) && !TryValidateDestination(sourceUrl, out _)))
                                { Warn(log, "Link inválido ignorado em item da lista Sobre."); continue; }
                                items.Add(new(ReadString(listItem, "text"), ReadString(listItem, "name"),
                                    ReadString(listItem, "purpose"), ReadString(listItem, "license"), licenseUrl,
                                    sourceUrl, ReadString(listItem, "warning")));
                            }
                            else Warn(log, "Item inválido ignorado na lista Sobre.");
                        }
                    if (items.Count == 0) continue;
                }
                if (type == "separator" || type == "links" || type == "list"
                    || !string.IsNullOrWhiteSpace(textKey))
                    parsed.Add(new(type, textKey, titleKey, severity, ReadString(block, "icon"), providerId, links, items));
            }
            if (parsed.Count == 0) { Warn(log, $"Aba vazia '{id}' ignorada no conteúdo Sobre."); continue; }
            tabs.Add(new(id, label, parsed, ReadString(tab, "footer")));
        }
        return tabs;
    }

    private static string ReadEmbedded(Assembly assembly, string name, string fallback, Action<string>? log)
    {
        try
        {
            using Stream stream = assembly.GetManifestResourceStream(name) ?? throw new FileNotFoundException(name);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException exception) { Warn(log, $"Recurso Sobre ausente; usando padrão embutido. {exception.Message}"); return fallback; }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool TryInt(JsonElement element, string property, out int number)
    {
        number = 0;
        return element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out number);
    }
    private static void Warn(Action<string>? log, string message) => log?.Invoke(message);
}
