using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed record PackageVisualPreferencesCatalogDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("fields")] IReadOnlyList<PackageManagerSettingsField> Fields);

public sealed class PackageVisualPreferencesCatalog
{
    private const string ResourceName = "WinProvision.Store.Assets.package-visual-preferences.json";
    private static readonly Lazy<PackageVisualPreferencesCatalog> DefaultCatalog = new(LoadEmbedded);
    private static readonly PackageManagerSettingsField[] FallbackFields =
    [
        new()
        {
            Id = "showPackageIcons",
            Type = "toggle",
            Section = "appearance",
            Label = "Ícones dos aplicativos",
            Description = "Exibe os ícones dos pacotes nas listas, nos cartões e nos detalhes.",
            DefaultValue = ApplicationPreferencesService.DefaultShowPackageIcons.ToString().ToLowerInvariant()
        },
        new()
        {
            Id = "showPackageScreenshots",
            Type = "toggle",
            Section = "appearance",
            Label = "Capturas de tela dos aplicativos",
            Description = "Exibe a galeria de capturas de tela nos detalhes dos pacotes.",
            DefaultValue = ApplicationPreferencesService.DefaultShowPackageScreenshots.ToString().ToLowerInvariant()
        }
    ];

    private readonly IReadOnlyDictionary<string, PackageManagerSettingsField> _fields;

    private PackageVisualPreferencesCatalog(IReadOnlyList<PackageManagerSettingsField> fields)
    {
        Fields = fields;
        _fields = fields.ToDictionary(field => field.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<PackageManagerSettingsField> Fields { get; }
    public static PackageVisualPreferencesCatalog Default => DefaultCatalog.Value;

    public static PackageVisualPreferencesCatalog Load(string? json, Action<string>? log = null)
    {
        try
        {
            PackageVisualPreferencesCatalogDocument? document = JsonSerializer.Deserialize(
                json ?? string.Empty, UiJsonContext.Default.PackageVisualPreferencesCatalogDocument);
            if (document is null || document.SchemaVersion != 1 || document.Fields is null)
            {
                log?.Invoke("Catálogo de preferências visuais ausente ou com schemaVersion incompatível; usando padrões.");
                return CreateFallback();
            }

            var supplied = document.Fields
                .Where(field => field is not null)
                .Where(field => field.Id is "showPackageIcons" or "showPackageScreenshots")
                .GroupBy(field => field.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            if (supplied.Count != document.Fields.Count)
                log?.Invoke("O catálogo de preferências visuais contém IDs desconhecidos ou duplicados; eles foram ignorados.");

            var fields = new List<PackageManagerSettingsField>(FallbackFields.Length);
            foreach (PackageManagerSettingsField fallback in FallbackFields)
            {
                if (!supplied.TryGetValue(fallback.Id, out PackageManagerSettingsField? field))
                {
                    log?.Invoke($"Preferência visual '{fallback.Id}' ausente; usando seu padrão.");
                    fields.Add(fallback);
                    continue;
                }

                if (!string.Equals(field.Type, "toggle", StringComparison.OrdinalIgnoreCase)
                    || !(string.Equals(field.DefaultValue, "true", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(field.DefaultValue, "false", StringComparison.OrdinalIgnoreCase))
                    || string.IsNullOrWhiteSpace(field.Label)
                    || string.IsNullOrWhiteSpace(field.Description))
                {
                    log?.Invoke($"Preferência visual '{fallback.Id}' inválida; usando sua definição padrão.");
                    fields.Add(fallback);
                    continue;
                }

                fields.Add(field with { Type = "toggle", Section = "appearance" });
            }

            return new PackageVisualPreferencesCatalog(fields);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            log?.Invoke($"Não foi possível ler o catálogo de preferências visuais: {exception.Message}");
            return CreateFallback();
        }
    }

    public bool GetDefaultValue(string id)
    {
        if (!_fields.TryGetValue(id, out PackageManagerSettingsField? field))
            throw new KeyNotFoundException($"Preferência visual não cadastrada: '{id}'.");
        return bool.TryParse(field.DefaultValue, out bool value) && value;
    }

    private static PackageVisualPreferencesCatalog LoadEmbedded()
    {
        try
        {
            using Stream stream = typeof(PackageVisualPreferencesCatalog).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new FileNotFoundException("Catálogo de preferências visuais embedded não encontrado.");
            using var reader = new StreamReader(stream);
            return Load(reader.ReadToEnd(), message => CoreLog.Information("PackageVisualPreferencesCatalog", message));
        }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "PackageVisualPreferencesCatalog.LoadEmbedded");
            return CreateFallback();
        }
    }

    private static PackageVisualPreferencesCatalog CreateFallback() => new(FallbackFields);
}
