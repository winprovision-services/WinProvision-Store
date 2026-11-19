using System.Text.Json;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed class OfficePageCatalogService
{
    private const string ResourceName = "WinProvision.Store.Assets.office-page.json";
    private const string TextResourceName = "WinProvision.Store.Assets.Localization.elevation-settings.pt-BR.json";
    private const string FallbackJson = """
    {"schemaVersion":1,"page":{"id":"office","titleTextKey":"office.page.title","back":true},"groups":[
    {"key":"officeStatus","page":"office","titleTextKey":"office.group.status","searchCategoryKey":"Office","searchCategoryTextKey":"office.page.title","order":0},
    {"key":"officeProduct","page":"office","titleTextKey":"office.group.product","searchCategoryKey":"Office","searchCategoryTextKey":"office.page.title","order":1},
    {"key":"officeApps","page":"office","titleTextKey":"office.group.apps","searchCategoryKey":"Office","searchCategoryTextKey":"office.page.title","order":2},
    {"key":"officeLocale","page":"office","titleTextKey":"office.group.locale","searchCategoryKey":"Office","searchCategoryTextKey":"office.page.title","order":3},
    {"key":"officeAdvanced","page":"office","titleTextKey":"office.group.advanced","searchCategoryKey":"Office","searchCategoryTextKey":"office.page.title","order":4},
    {"key":"officeActions","page":"office","titleTextKey":"office.group.actions","searchCategoryKey":"Office","searchCategoryTextKey":"office.page.title","order":5}],
    "settings":[
    {"key":"office.status","group":"officeStatus","kind":"status","control":"info","risk":"normal","defaultValue":"","appliesImmediately":true,"requiresConfirmation":false,"titleTextKey":"office.status.title","descriptionTextKey":"office.status.description"},
    {"key":"office.architecture","group":"officeProduct","kind":"preference","control":"combo","risk":"normal","defaultValue":"64","storageKey":"Architecture","options":[{"value":"64","labelTextKey":"office.architecture.x64"},{"value":"32","labelTextKey":"office.architecture.x86"}],"appliesImmediately":true,"requiresConfirmation":false,"titleTextKey":"office.architecture.title","descriptionTextKey":"office.architecture.description"},
    {"key":"office.install","group":"officeActions","kind":"info","control":"action","risk":"normal","actionKey":"office.install","actionTextKey":"office.action.install","appliesImmediately":true,"requiresConfirmation":true,"titleTextKey":"office.install.title","descriptionTextKey":"office.install.description"}]}
    """;
    private readonly ElevationSettingsCatalogService _settings = new();

    public OfficePageCatalogService()
    {
        Catalog = LoadEmbedded();
        Text = LoadText();
    }

    public ElevationSettingsCatalog Catalog { get; }
    public IReadOnlyDictionary<string, string> Text { get; }
    public string ResolveText(string key) => Text.TryGetValue(key, out string? value) ? value : key;

    private static IReadOnlyDictionary<string, string> LoadText()
    {
        try
        {
            using Stream stream = typeof(OfficePageCatalogService).Assembly.GetManifestResourceStream(TextResourceName)
                ?? throw new FileNotFoundException("Recursos pt-BR do Office não encontrados.");
            return System.Text.Json.JsonSerializer.Deserialize(stream, UiJsonContext.Default.ElevationSettingsText)
                ?? new Dictionary<string, string>();
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            WinProvisionLog.Warning(exception, "OfficePageCatalog.LoadText");
            return new Dictionary<string, string>();
        }
    }

    private ElevationSettingsCatalog LoadEmbedded()
    {
        try
        {
            using Stream stream = typeof(OfficePageCatalogService).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new FileNotFoundException("Catálogo Office não encontrado.");
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();
            using JsonDocument document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("schemaVersion", out JsonElement version)
                || version.GetInt32() != 1
                || !document.RootElement.TryGetProperty("page", out JsonElement page)
                || page.ValueKind != JsonValueKind.Object
                || !page.TryGetProperty("id", out JsonElement id)
                || id.GetString() != "office")
                throw new JsonException("Catálogo de página Office inválido ou com schemaVersion incompatível.");
            JsonElement groups = document.RootElement.GetProperty("groups");
            JsonElement settings = document.RootElement.GetProperty("settings");
            string shared = "{\"schemaVersion\":1,\"groups\":" + groups.GetRawText()
                + ",\"settings\":" + settings.GetRawText() + "}";
            return ElevationSettingsCatalog.Load(shared,
                message => WinProvisionLog.Warning(new InvalidDataException(message), "OfficePageCatalog"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            WinProvisionLog.Warning(exception, "OfficePageCatalog.Load");
            return LoadFallback();
        }
    }

    private static ElevationSettingsCatalog LoadFallback()
    {
        using JsonDocument document = JsonDocument.Parse(FallbackJson);
        string shared = "{\"schemaVersion\":1,\"groups\":" + document.RootElement.GetProperty("groups").GetRawText()
            + ",\"settings\":" + document.RootElement.GetProperty("settings").GetRawText() + "}";
        return ElevationSettingsCatalog.Load(shared, message => WinProvisionLog.Warning(new InvalidDataException(message), "OfficePageCatalog.Fallback"));
    }

}
