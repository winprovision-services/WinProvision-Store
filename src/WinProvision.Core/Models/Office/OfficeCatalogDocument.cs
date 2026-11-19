using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinProvision.Core.Services;

namespace WinProvision.Core.Models.Office;

public sealed record OfficeCatalogApp(string Id, string Name, string? OdtId, string? ProductId,
    IReadOnlyList<string> Executables, IReadOnlyList<string> PackageNames, IReadOnlyList<string> DetectionPaths,
    string Icon, string IconFallback, string Kind, bool DefaultSelected, string? ProductGroup, string? CardDisplayName);
public sealed record OfficeCatalogAddOn(string ProductId, string AppId, string LicenseWarning);
public sealed record OfficeCatalogProduct(string Id, string ProductId, IReadOnlyList<string> Aliases,
    string Name, string Group, int Order, bool IsDefault, IReadOnlyList<string> Apps,
    IReadOnlyList<string> ConditionalApps, string? Channel, bool VolumeLicensed, bool SupportsSelectableChannel,
    IReadOnlyList<OfficeCatalogAddOn> AddOns);
public sealed record OfficeCatalogState(string Icon, string Label, string Tooltip, string Foreground);

/// <summary>Validador/parser do catálogo Office versionado, também usado para o descritor remoto hash-pinado.</summary>
public sealed class OfficeCatalogDocument
{
    private static readonly Regex SafeOdtId = new("^[A-Za-z0-9]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SafeAppId = new("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SafeIcon = new("^[A-Za-z0-9_-]+\\.png$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex SafeDetectionPath = new("^\\{(?:localAppData|programFiles|programFilesX86)\\}/[A-Za-z0-9 ._-]+(?:/[A-Za-z0-9 ._-]+)*$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    public int SchemaVersion { get; }
    public string SourceId { get; }
    public string SourceLabel { get; }
    public string SourceIcon { get; }
    public string SourceIconFallback { get; }
    public IReadOnlyList<OfficeCatalogApp> Apps { get; }
    public IReadOnlyList<OfficeCatalogProduct> Products { get; }
    public IReadOnlyDictionary<string, OfficeCatalogState> States { get; }
    public IReadOnlyList<string> LegacyIgnoredOdtIds { get; }
    public string ConditionalAppsDescription { get; }

    private OfficeCatalogDocument(int schemaVersion, string sourceId, string sourceLabel, string sourceIcon,
        string sourceIconFallback, IReadOnlyList<OfficeCatalogApp> apps, IReadOnlyList<OfficeCatalogProduct> products,
        IReadOnlyDictionary<string, OfficeCatalogState> states, IReadOnlyList<string> legacyIgnoredOdtIds,
        string conditionalAppsDescription)
    {
        SchemaVersion = schemaVersion; SourceId = sourceId; SourceLabel = sourceLabel; SourceIcon = sourceIcon;
        SourceIconFallback = sourceIconFallback; Apps = apps; Products = products; States = states;
        LegacyIgnoredOdtIds = legacyIgnoredOdtIds; ConditionalAppsDescription = conditionalAppsDescription;
    }

    public static OfficeCatalogDocument Parse(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("schemaVersion do catálogo Office não suportado.");
        JsonElement source = root.GetProperty("source");
        string sourceId = Required(source, "id"), sourceLabel = Required(source, "label");
        string sourceIcon = Required(source, "icon"), sourceFallback = Required(source, "iconFallback");
        var apps = new List<OfficeCatalogApp>();
        var appIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var odtIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in root.GetProperty("apps").EnumerateArray())
        {
            string id = Required(item, "id"), name = Required(item, "name"), kind = Required(item, "kind");
            string? odtId = Optional(item, "odtId"), productId = Optional(item, "productId");
            if (!SafeAppId.IsMatch(id) || !appIds.Add(id)) throw new InvalidDataException($"ID de aplicativo Office inválido ou duplicado: {id}");
            if (kind is not ("exclude" or "addOn" or "advanced")) throw new InvalidDataException($"Tipo de aplicativo Office desconhecido: {kind}");
            if (odtId is not null && (!SafeOdtId.IsMatch(odtId) || !odtIds.Add(odtId))) throw new InvalidDataException($"odtId inválido ou duplicado: {odtId}");
            if (productId is not null && !SafeOdtId.IsMatch(productId)) throw new InvalidDataException($"productId inválido: {productId}");
            if (odtId is null && productId is null) throw new InvalidDataException($"Aplicativo Office sem odtId ou productId: {id}");
            string[] executables = Strings(item, "executables");
            if (executables.Length == 0 && Optional(item, "exe") is { } legacyExe) executables = [legacyExe];
            if (executables.Any(executable => executable.Length == 0 || executable.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))))
                throw new InvalidDataException($"Executável inválido no aplicativo: {id}");
            string icon = Required(item, "icon"), iconFallback = Required(item, "iconFallback");
            if (!SafeIcon.IsMatch(icon) || !iconFallback.StartsWith("ms-appx:///Assets/OfficeApps/", StringComparison.Ordinal)
                || !SafeIcon.IsMatch(iconFallback["ms-appx:///Assets/OfficeApps/".Length..]))
                throw new InvalidDataException($"Referência de ícone inválida: {id}");
            string[] detectionPaths = Strings(item, "detectionPaths");
            if (detectionPaths.Any(path => !SafeDetectionPath.IsMatch(path) || path.Split('/').Any(part => part is "." or "..")))
                throw new InvalidDataException($"Caminho de detecção inválido: {id}");
            apps.Add(new(id, name, odtId, productId, executables, Strings(item, "packageNames"), detectionPaths, icon,
                iconFallback, kind, item.GetProperty("defaultSelected").GetBoolean(), Optional(item, "productGroup"), Optional(item, "cardDisplayName")));
        }
        var products = new List<OfficeCatalogProduct>();
        var productIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var productKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in root.GetProperty("products").EnumerateArray())
        {
            string id = Required(item, "id"), productId = Required(item, "productId");
            if (!SafeOdtId.IsMatch(productId) || !productKeys.Add(id) || !productIds.Add(productId)) throw new InvalidDataException($"productId inválido ou duplicado: {productId}");
            string[] included = Strings(item, "apps"), conditional = Strings(item, "conditionalApps");
            if (included.Length + conditional.Length == 0) throw new InvalidDataException($"Produto sem aplicativos: {productId}");
            if (included.Intersect(conditional, StringComparer.OrdinalIgnoreCase).Any()) throw new InvalidDataException($"Aplicativo repetido como incluído e condicional: {productId}");
            if (included.Concat(conditional).Any(app => !appIds.Contains(app))) throw new InvalidDataException($"Produto referencia aplicativo inexistente: {productId}");
            var addOns = item.TryGetProperty("addOns", out JsonElement addOnArray) && addOnArray.ValueKind == JsonValueKind.Array
                ? addOnArray.EnumerateArray().Select(addOn => new OfficeCatalogAddOn(Required(addOn, "productId"), Required(addOn, "appId"), Required(addOn, "licenseWarning"))).ToArray()
                : [];
            foreach (OfficeCatalogAddOn addOn in addOns)
                if (!SafeOdtId.IsMatch(addOn.ProductId) || !appIds.Contains(addOn.AppId)) throw new InvalidDataException($"Complemento inválido no produto: {productId}");
            string[] aliases = Strings(item, "aliases");
            if (aliases.Any(alias => !SafeOdtId.IsMatch(alias))) throw new InvalidDataException($"Alias inválido no produto: {productId}");
            products.Add(new(id, productId, aliases, Required(item, "name"), Required(item, "group"),
                item.GetProperty("order").GetInt32(), item.GetProperty("default").GetBoolean(), included, conditional,
                Optional(item, "channel"), item.GetProperty("volumeLicensed").GetBoolean(),
                item.GetProperty("supportsSelectableChannel").GetBoolean(), addOns));
        }
        foreach (OfficeCatalogApp app in apps.Where(item => item.ProductId is not null))
            if (!productIds.Contains(app.ProductId!)) throw new InvalidDataException($"Aplicativo referencia produto inexistente: {app.Id}");
        for (int index = 0; index < apps.Count; index++)
        {
            OfficeCatalogApp app = apps[index];
            if (app.Kind != "addOn") continue;
            OfficeCatalogProduct? product = products.FirstOrDefault(item => item.ProductId.Equals(app.ProductId, StringComparison.OrdinalIgnoreCase)
                && item.Apps.Contains(app.Id, StringComparer.OrdinalIgnoreCase));
            if (product is null) throw new InvalidDataException($"Complemento sem produto correspondente: {app.Id}");
            apps[index] = app with { ProductGroup = product.Group };
        }
        foreach (OfficeCatalogAddOn addOn in products.SelectMany(product => product.AddOns))
            if (!productIds.Contains(addOn.ProductId)) throw new InvalidDataException($"Complemento referencia produto inexistente: {addOn.ProductId}");
        foreach (OfficeCatalogProduct product in products)
            foreach (string alias in product.Aliases)
                if (productIds.Contains(alias) && !alias.Equals(product.ProductId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Alias de produto duplicado: {alias}");
        var aliasSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OfficeCatalogProduct product in products)
            foreach (string alias in product.Aliases)
                if (!aliasSet.Add(alias) && !alias.Equals(product.ProductId, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Alias de produto duplicado: {alias}");
        if (products.GroupBy(product => product.Group, StringComparer.OrdinalIgnoreCase).Any(group => group.Count(product => product.IsDefault) != 1))
            throw new InvalidDataException("Cada grupo de produtos Office precisa ter um único padrão.");
        var appById = apps.ToDictionary(app => app.Id, StringComparer.OrdinalIgnoreCase);
        if (products.SelectMany(product => product.ConditionalApps).Any(id => appById[id].DefaultSelected))
            throw new InvalidDataException("Aplicativos condicionais precisam vir desmarcados por padrão.");
        var states = new Dictionary<string, OfficeCatalogState>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty state in root.GetProperty("states").EnumerateObject())
            states[state.Name] = new(Required(state.Value, "icon"), Required(state.Value, "label"), Required(state.Value, "tooltip"),
                Optional(state.Value, "foreground") ?? "secondary");
        if (states.Count == 0) throw new InvalidDataException("Catálogo Office sem estados.");
        string[] ignored = root.TryGetProperty("legacyIgnoredOdtIds", out _) ? Strings(root, "legacyIgnoredOdtIds") : [];
        if (ignored.Any(value => !SafeOdtId.IsMatch(value))) throw new InvalidDataException("Identificador ODT legado inválido.");
        return new(1, sourceId, sourceLabel, sourceIcon, sourceFallback, apps, products, states, ignored,
            Required(root, "conditionalAppsDescription"));
    }

    private static string Required(JsonElement element, string property) => Optional(element, property)
        ?? throw new InvalidDataException($"Campo obrigatório ausente no catálogo Office: {property}");
    private static string? Optional(JsonElement element, string property) => element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string[] Strings(JsonElement element, string property) => element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray() : [];
}

public static class OfficeCatalogRuntime
{
    private const string ResourceName = "WinProvision.Core.Models.office-catalog.json";
    private static readonly Lazy<OfficeCatalogDocument> BuiltInValue = new(LoadEmbedded);
    public static OfficeCatalogDocument Current { get; private set; } = BuiltInValue.Value;
    public static void UseValidated(OfficeCatalogDocument catalog) => Current = catalog ?? throw new ArgumentNullException(nameof(catalog));
    public static byte[] GetEmbeddedBytes()
    {
        using Stream stream = typeof(OfficeCatalogRuntime).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException("Catálogo Office embutido não encontrado.");
        using var memory = new MemoryStream(); stream.CopyTo(memory); return memory.ToArray();
    }
    private static OfficeCatalogDocument LoadEmbedded()
    {
        try { return OfficeCatalogDocument.Parse(System.Text.Encoding.UTF8.GetString(GetEmbeddedBytes())); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
        {
            CoreLog.Warning(ex, "OfficeCatalog.Embedded");
            throw;
        }
    }
}
