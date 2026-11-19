using System.Text.Json;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed class ElevationSettingsCatalogService
{
    private const string CatalogResourceName = "WinProvision.Store.Assets.elevation-settings.json";
    private const string TextResourceName = "WinProvision.Store.Assets.Localization.elevation-settings.pt-BR.json";

    public ElevationSettingsCatalogService()
    {
        Catalog = LoadCatalog();
        Text = LoadText();
    }

    public ElevationSettingsCatalog Catalog { get; }
    public IReadOnlyDictionary<string, string> Text { get; }

    public string ResolveText(string key) =>
        Text.TryGetValue(key, out string? value) ? value : key;

    private static ElevationSettingsCatalog LoadCatalog()
    {
        try
        {
            using Stream stream = typeof(ElevationSettingsCatalogService).Assembly.GetManifestResourceStream(CatalogResourceName)
                ?? throw new FileNotFoundException("Catálogo de configurações do Elevator embedded não encontrado.");
            using var reader = new StreamReader(stream);
            return ElevationSettingsCatalog.Load(reader.ReadToEnd(),
                message => CoreLog.Information("ElevationSettingsCatalog", message));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(exception, "ElevationSettingsCatalog.LoadEmbedded");
            return ElevationSettingsCatalog.Default;
        }
    }

    private static IReadOnlyDictionary<string, string> LoadText()
    {
        try
        {
            using Stream stream = typeof(ElevationSettingsCatalogService).Assembly.GetManifestResourceStream(TextResourceName)
                ?? throw new FileNotFoundException("Recursos de texto do Elevator embedded não encontrados.");
            return JsonSerializer.Deserialize(
                stream, UiJsonContext.Default.ElevationSettingsText)
                ?? new Dictionary<string, string>();
        }
        catch (Exception exception) when (exception is IOException or JsonException or NotSupportedException)
        {
            CoreLog.Warning(exception, "ElevationSettingsCatalog.LoadText");
            return new Dictionary<string, string>();
        }
    }
}
