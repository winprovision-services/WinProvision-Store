using System.Globalization;
using System.Reflection;
using System.Text.Json;
using WinProvision.Core.Services;

namespace WinProvision.Core.Models;

/// <summary>Catálogo versionado de gerenciadores, traduções e capacidades compartilhado entre telas.</summary>
public sealed class PackageManagerCatalog
{
    private readonly Dictionary<string, PackageManagerDescriptor> _managers;
    private readonly Dictionary<string, Dictionary<string, string>> _translations;
    private readonly Dictionary<string, string> _statusStyles;
    private readonly HashSet<string> _rejectedManagerIds;
    private readonly Action<string>? _log;

    private PackageManagerCatalog(IEnumerable<PackageManagerDescriptor> managers,
        Dictionary<string, Dictionary<string, string>> translations,
        Dictionary<string, string> statusStyles, Action<string>? log, IEnumerable<string>? rejectedManagerIds = null)
    {
        _managers = managers.Where(manager => !string.IsNullOrWhiteSpace(manager.Id))
            .GroupBy(manager => manager.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last()).ToDictionary(manager => manager.Id, StringComparer.OrdinalIgnoreCase);
        _translations = translations;
        _statusStyles = statusStyles;
        _rejectedManagerIds = new HashSet<string>(rejectedManagerIds ?? [], StringComparer.OrdinalIgnoreCase);
        _log = log;
    }

    private static readonly Lazy<PackageManagerCatalog> DefaultCatalog = new(LoadEmbedded);
    public static PackageManagerCatalog Default => DefaultCatalog.Value;
    public IReadOnlyList<PackageManagerDescriptor> Managers => _managers.Values.ToArray();
    public IReadOnlyList<PackageManagerDescriptor> SettingsManagers => _managers.Values.Where(manager => manager.ShowInSettings)
        .OrderBy(manager => manager.SortOrder).ToArray();
    public bool IsSchemaRejected(string id) => _rejectedManagerIds.Contains(id);

    public PackageManagerDescriptor GetDescriptor(string id)
    {
        if (!_managers.TryGetValue(id, out PackageManagerDescriptor? descriptor))
        {
            _log?.Invoke($"Gerenciador '{id}' ausente no catálogo; usando descritor genérico.");
            return new PackageManagerDescriptor { Id = id, Source = id, DisplayName = id };
        }

        string displayName = GetText(descriptor.DisplayNameKey, descriptor.DisplayName);
        string description = GetText(descriptor.DescriptionKey, string.Empty);
        return WithLocalizedText(descriptor, displayName, description);
    }

    public string GetText(string? key, string? fallback = null, string? language = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return fallback ?? string.Empty;
        string locale = NormalizeLocale(language ?? CultureInfo.CurrentUICulture.Name);
        if (_translations.TryGetValue(locale, out Dictionary<string, string>? values)
            && values.TryGetValue(key, out string? translated)) return translated;
        if (_translations.TryGetValue("pt-BR", out values) && values.TryGetValue(key, out translated)) return translated;
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            _log?.Invoke($"Chave de tradução '{key}' ausente; usando valor alternativo.");
            return fallback;
        }
        _log?.Invoke($"Chave de tradução '{key}' ausente; usando a própria chave.");
        return key;
    }

    public string GetStatusStyle(string status) => _statusStyles.TryGetValue(status, out string? style) ? style : "info";

    public static PackageManagerCatalog Load(string? json, Action<string>? log = null)
    {
        var rejectedManagerIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json ?? string.Empty);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out JsonElement schema) || schema.GetInt32() != 1
                || !root.TryGetProperty("managers", out JsonElement managerArray) || managerArray.ValueKind != JsonValueKind.Array)
                throw new JsonException("Formato do catálogo de gerenciadores inválido.");

            var managers = new List<PackageManagerDescriptor>();
            foreach (JsonElement item in managerArray.EnumerateArray())
            {
                if (!item.TryGetProperty("schemaVersion", out JsonElement managerSchema)
                    || !managerSchema.TryGetInt32(out int managerVersion)
                    || managerVersion != 1)
                {
                    string managerId = item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("id", out JsonElement idValue)
                        && idValue.ValueKind == JsonValueKind.String
                            ? idValue.GetString() ?? "(sem id)"
                            : "(sem id)";
                    string message = $"Descritor do gerenciador '{managerId}' rejeitado: schemaVersion ausente, inválido ou não suportado.";
                    CoreLog.Warning(new JsonException(message), "PackageManagerCatalog.UnsupportedManagerSchema");
                    log?.Invoke(message);
                    if (!managerId.Equals("(sem id)", StringComparison.Ordinal)) rejectedManagerIds.Add(managerId);
                    continue;
                }
                PackageManagerDescriptor? manager = JsonSerializer.Deserialize(item.GetRawText(), CoreJsonContext.Default.PackageManagerDescriptor);
                if (manager is { SchemaVersion: 1 } && !string.IsNullOrWhiteSpace(manager.Id)) managers.Add(manager);
                else
                {
                    const string message = "Descritor do gerenciador rejeitado: schemaVersion não suportado ou id ausente.";
                    CoreLog.Warning(new JsonException(message), "PackageManagerCatalog.InvalidManagerDescriptor");
                    log?.Invoke(message);
                }
            }
            if (!managers.Any(manager => manager.ShowInSettings)) throw new JsonException("Catálogo sem gerenciadores de configurações válidos.");

            var translations = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("translations", out JsonElement translationRoot)
                && translationRoot.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty locale in translationRoot.EnumerateObject())
                    if (locale.Value.ValueKind == JsonValueKind.Object)
                        translations[NormalizeLocale(locale.Name)] = locale.Value.EnumerateObject()
                            .Where(item => item.Value.ValueKind == JsonValueKind.String)
                            .ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            var styles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("statusStyles", out JsonElement styleRoot) && styleRoot.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty style in styleRoot.EnumerateObject())
                    if (style.Value.ValueKind == JsonValueKind.String) styles[style.Name] = style.Value.GetString() ?? "info";

            return new PackageManagerCatalog(managers, translations, styles, log, rejectedManagerIds);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            log?.Invoke($"Catálogo de gerenciadores inválido; usando definições seguras. {exception.Message}");
            return CreateFallback(log, rejectedManagerIds);
        }
    }

    private static PackageManagerCatalog LoadEmbedded()
    {
        try
        {
            using Stream stream = typeof(PackageManagerCatalog).Assembly.GetManifestResourceStream("WinProvision.Core.Models.package-managers.json")
                ?? throw new FileNotFoundException("Recurso embutido do catálogo de gerenciadores não encontrado.");
            using var reader = new StreamReader(stream);
            return Load(reader.ReadToEnd(), message => CoreLog.Information("PackageManagerCatalog", message));
        }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "PackageManagerCatalog.LoadEmbedded");
            return CreateFallback(message => CoreLog.Information("PackageManagerCatalog", message));
        }
    }

    private static PackageManagerCatalog CreateFallback(Action<string>? log, IEnumerable<string>? rejectedManagerIds = null) => new(
    [
        new PackageManagerDescriptor { Id = "winget", Source = "winget", DisplayName = "WinGet", DisplayNameKey = "manager.winget.name", DescriptionKey = "manager.winget.description", IconKey = "winget", IconGlyph = "\uE896", IconUri = "ms-appx:///Assets/Icons/winget.png", ShowInSettings = true, CanToggle = true, HasDetailsPage = true },
        new PackageManagerDescriptor { Id = "msstore", Source = "msstore", DisplayName = "Microsoft Store", DisplayNameKey = "manager.msstore.name", IconKey = "store", IconGlyph = "\uE719", IconUri = "ms-appx:///Assets/Icons/microsoft-store.svg", ParentId = "winget", ShowInSettings = false, CanToggle = false },
        new PackageManagerDescriptor { Id = "chocolatey", Source = "chocolatey", DisplayName = "Chocolatey", DisplayNameKey = "manager.chocolatey.name", DescriptionKey = "manager.chocolatey.description", IconKey = "chocolatey", IconGlyph = "\uE8F1", IconUri = "ms-appx:///Assets/Icons/chocolatey.svg", ShowInSettings = true, CanToggle = true, HasDetailsPage = true },
    ],
    new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["pt-BR"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["status.ready"] = "Pronto", ["status.disabled"] = "Desativado", ["status.checking"] = "Verificando",
            ["status.notFound"] = "Não encontrado", ["status.error"] = "Falha na verificação", ["status.notChecked"] = "Não verificado"
        }
    },
    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    { ["ready"] = "success", ["disabled"] = "warning", ["checking"] = "info", ["notFound"] = "warning", ["error"] = "error", ["notChecked"] = "info" }, log, rejectedManagerIds);

    private static string NormalizeLocale(string language) => language.Equals("pt", StringComparison.OrdinalIgnoreCase)
        ? "pt-BR" : language.Equals("en", StringComparison.OrdinalIgnoreCase) ? "en-US" : language;

    private static PackageManagerDescriptor WithLocalizedText(PackageManagerDescriptor descriptor, string displayName, string description) => new()
    {
        SchemaVersion = descriptor.SchemaVersion,
        Id = descriptor.Id, Source = descriptor.Source, DisplayName = displayName,
        Description = description,
        DisplayNameKey = descriptor.DisplayNameKey, DescriptionKey = descriptor.DescriptionKey,
        DisabledReasonKey = descriptor.DisabledReasonKey,
        IconKey = descriptor.IconKey, IconGlyph = descriptor.IconGlyph, IconUri = descriptor.IconUri, ParentId = descriptor.ParentId,
        ShowInSettings = descriptor.ShowInSettings, CanToggle = descriptor.CanToggle,
        HasDetailsPage = descriptor.HasDetailsPage, Check = descriptor.Check ?? new PackageManagerCheckDefinition(), SortOrder = descriptor.SortOrder,
        CommandLabel = descriptor.CommandLabel, ManifestLabel = descriptor.ManifestLabel,
        VersionOptionsLabel = descriptor.VersionOptionsLabel, GlobalOptionsLabel = descriptor.GlobalOptionsLabel,
        CustomArgumentsDescription = descriptor.CustomArgumentsDescription, OptionsUnavailableMessage = descriptor.OptionsUnavailableMessage,
        LoadsExtendedMetadata = descriptor.LoadsExtendedMetadata, SupportedArchitectures = descriptor.SupportedArchitectures ?? [],
        Capabilities = descriptor.Capabilities ?? new PackageManagerCapabilities()
    };
}
