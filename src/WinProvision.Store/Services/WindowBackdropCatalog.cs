using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Composition.SystemBackdrops;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed record WindowBackdropDefinition(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("variant")] string? Variant = null);

public sealed record WindowBackdropCatalogDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("default")] string Default,
    [property: JsonPropertyName("fallbackOrder")] string[] FallbackOrder,
    [property: JsonPropertyName("backdrops")] WindowBackdropDefinition[] Backdrops);

public readonly record struct WindowBackdropSupport(bool Mica, bool Acrylic)
{
    public static WindowBackdropSupport Current => new(MicaController.IsSupported(), DesktopAcrylicController.IsSupported());
}

public sealed record WindowBackdropResolution(WindowBackdropDefinition Backdrop, bool UsedFallback, string? Warning);

public sealed class WindowBackdropCatalog
{
    private const string ResourceName = "WinProvision.Store.Assets.window-backdrops.json";
    private static readonly Lazy<WindowBackdropCatalog> DefaultCatalog = new(LoadEmbedded);
    private readonly Dictionary<string, WindowBackdropDefinition> _backdrops;

    private WindowBackdropCatalog(WindowBackdropCatalogDocument document)
    {
        Document = document;
        _backdrops = document.Backdrops.ToDictionary(backdrop => backdrop.Id, StringComparer.OrdinalIgnoreCase);
    }

    public WindowBackdropCatalogDocument Document { get; }
    public IReadOnlyList<WindowBackdropDefinition> Backdrops => Document.Backdrops;
    public WindowBackdropDefinition DefaultBackdrop => _backdrops[Document.Default];
    public WindowBackdropDefinition NoneBackdrop => _backdrops["none"];
    public static WindowBackdropCatalog Default => DefaultCatalog.Value;

    public static WindowBackdropCatalog Load(string? json, Action<string>? log = null)
    {
        try
        {
            WindowBackdropCatalogDocument? document = JsonSerializer.Deserialize(
                json ?? string.Empty, UiJsonContext.Default.WindowBackdropCatalogDocument);
            if (document is null || document.SchemaVersion != 1
                || string.IsNullOrWhiteSpace(document.Default)
                || document.FallbackOrder is null
                || document.Backdrops is null
                || document.Backdrops.Length != 5
                || document.Backdrops.Select(backdrop => backdrop.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 5
                || !new[] { "mica", "mica-alt", "acrylic", "acrylic-thin", "none" }
                    .All(id => document.Backdrops.Any(backdrop => backdrop.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                || !document.Backdrops.Any(backdrop => backdrop.Id.Equals(document.Default, StringComparison.OrdinalIgnoreCase))
                || !document.Backdrops.Any(backdrop => backdrop.Id.Equals("none", StringComparison.OrdinalIgnoreCase))
                || !document.FallbackOrder.Contains("none", StringComparer.OrdinalIgnoreCase)
                || document.FallbackOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.FallbackOrder.Length
                || document.FallbackOrder.Any(id => !document.Backdrops.Any(backdrop => backdrop.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                || document.Backdrops.Any(backdrop => string.IsNullOrWhiteSpace(backdrop.Id)
                    || string.IsNullOrWhiteSpace(backdrop.Label)
                    || backdrop.Kind is not ("mica" or "acrylic" or "none")))
                throw new JsonException("Catálogo de planos de fundo inválido ou schemaVersion não suportado.");

            return new WindowBackdropCatalog(document);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            WinProvisionLog.Warning(exception, "WindowBackdropCatalog.Load");
            log?.Invoke($"Catálogo de planos de fundo inválido; usando definições seguras. {exception.Message}");
            return CreateFallback();
        }
    }

    public WindowBackdropResolution Resolve(string? requestedId, WindowBackdropSupport support)
    {
        string? warning = null;
        string initialId = requestedId ?? string.Empty;
        if (!_backdrops.ContainsKey(initialId))
        {
            warning = $"Plano de fundo '{initialId}' desconhecido; usando o padrão '{Document.Default}'.";
            initialId = Document.Default;
        }

        WindowBackdropDefinition requested = _backdrops[initialId];
        if (IsSupported(requested, support))
            return new(requested, false, warning);

        foreach (string fallbackId in Document.FallbackOrder)
            if (_backdrops.TryGetValue(fallbackId, out WindowBackdropDefinition? fallback)
                && IsSupported(fallback, support))
                return new(fallback, true, warning);

        return new(NoneBackdrop, true, warning);
    }

    private static bool IsSupported(WindowBackdropDefinition backdrop, WindowBackdropSupport support) =>
        backdrop.Kind switch
        {
            "mica" => support.Mica,
            "acrylic" => support.Acrylic,
            "none" => true,
            _ => false
        };

    private static WindowBackdropCatalog LoadEmbedded()
    {
        try
        {
            using Stream stream = typeof(WindowBackdropCatalog).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new FileNotFoundException("Recurso embutido de planos de fundo não encontrado.");
            using var reader = new StreamReader(stream);
            return Load(reader.ReadToEnd(), message => CoreLog.Information("WindowBackdropCatalog", message));
        }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "WindowBackdropCatalog.LoadEmbedded");
            return CreateFallback();
        }
    }

    private static WindowBackdropCatalog CreateFallback() => new(new WindowBackdropCatalogDocument(
        1,
        "mica",
        ["mica", "acrylic", "none"],
        [
            new("mica", "Mica", "mica", "base"),
            new("mica-alt", "Mica Alt", "mica", "baseAlt"),
            new("acrylic", "Acrylic", "acrylic", "base"),
            new("acrylic-thin", "Acrylic Thin", "acrylic", "thin"),
            new("none", "Nenhum", "none")
        ]));
}
