using System.Text.Json;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed class NoticeCatalogService
{
    private const string CatalogResourceName = "WinProvision.Store.Assets.notices.json";
    private const string TextResourceName = "WinProvision.Store.Assets.Localization.notices.pt-BR.json";

    public NoticeCatalogService()
    {
        Catalog = LoadCatalog();
        Text = LoadText();
    }

    public NoticeCatalog Catalog { get; }
    public IReadOnlyDictionary<string, string> Text { get; }

    public string ResolveText(string key) =>
        Text.TryGetValue(key, out string? value) ? value : key;

    private static NoticeCatalog LoadCatalog()
    {
        try
        {
            using Stream stream = typeof(NoticeCatalogService).Assembly.GetManifestResourceStream(CatalogResourceName)
                ?? throw new FileNotFoundException("Catálogo de avisos embedded não encontrado.");
            using var reader = new StreamReader(stream);
            return NoticeCatalog.Load(reader.ReadToEnd(),
                message => CoreLog.Information("NoticeCatalog", message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(exception, "NoticeCatalog.LoadEmbedded");
            return NoticeCatalog.Default;
        }
    }

    private static IReadOnlyDictionary<string, string> LoadText()
    {
        try
        {
            using Stream stream = typeof(NoticeCatalogService).Assembly.GetManifestResourceStream(TextResourceName)
                ?? throw new FileNotFoundException("Recursos de localização de avisos embedded não encontrados.");
            return JsonSerializer.Deserialize(stream, UiJsonContext.Default.ElevationSettingsText)
                ?? new Dictionary<string, string>();
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            CoreLog.Warning(exception, "NoticeCatalog.LoadText");
            return new Dictionary<string, string>();
        }
    }
}
