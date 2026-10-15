using System.Text;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services.Office;

namespace WinProvision.Core.Services;

/// <summary>Contrato único de perfis para coleções, backup e execução por qualquer interface.</summary>
public static class ProfileDocumentService
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public const int MaximumApps = 10000;
    public static readonly JsonDocumentOptions DocumentOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static ProfileBackupSet Parse(string json, string? fallbackName = null)
    {
        json = json.TrimStart('\uFEFF');
        if (Encoding.UTF8.GetByteCount(json) > MaximumBytes) throw new InvalidDataException("O perfil excede 16 MiB.");
        using var document = JsonDocument.Parse(json, DocumentOptions);
        JsonElement root = document.RootElement;
        ProfileBackupSet backup;
        if (root.ValueKind == JsonValueKind.Object && TryProperty(root, "tabs", out _))
            backup = AotJson.Deserialize<ProfileBackupSet>(json, WinProvisionJsonOptions.Profile) ?? throw new InvalidDataException("Backup vazio.");
        else if (root.ValueKind == JsonValueKind.Object && TryProperty(root, "apps", out _))
            backup = new() { Tabs = [AotJson.Deserialize<ProfileManifest>(json, WinProvisionJsonOptions.Profile) ?? throw new InvalidDataException("Perfil vazio.")] };
        else if (root.ValueKind == JsonValueKind.Array)
            backup = new() { Tabs = [new() { Name = fallbackName, Apps = ReadCompatibilityList(root, null, false) }] };
        else if (root.ValueKind == JsonValueKind.Object && TryProperty(root, "Sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            var apps = new List<ProfileAppRef>();
            foreach (var source in sources.EnumerateArray())
            {
                if (!TryProperty(source, "SourceDetails", out var details) || !TryProperty(details, "Name", out var name)
                    || !TryProperty(source, "Packages", out var packages)) throw new ArgumentException("Exportação WinGet inválida.");
                apps.AddRange(ReadCompatibilityList(packages, name.GetString(), true));
            }
            backup = new() { Tabs = [new() { Name = fallbackName, Apps = apps }] };
        }
        else throw new InvalidDataException("O JSON não contém um perfil WinProvision, backup de coleções ou lista de aplicativos compatível.");
        Validate(backup, fallbackName);
        return backup;
    }

    public static string Serialize(ProfileManifest profile)
    {
        Validate(new ProfileBackupSet { Tabs = [profile] }, profile.Name);
        return AotJson.Serialize(profile, WinProvisionJsonOptions.Profile);
    }

    public static IReadOnlyList<ProfileAppRef> CreateExecutionPlan(ProfileBackupSet backup)
    {
        Validate(backup, null);
        var result = new List<ProfileAppRef>();
        var identities = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in backup.Tabs.SelectMany(tab => tab.Apps))
        {
            string key = $"{app.Source}:{app.Id}";
            string options = (app.PinnedVersion ?? "") + "\n" + (app.OfficeOptions is null ? "" : AotJson.Serialize(app.OfficeOptions, WinProvisionJsonOptions.Profile));
            if (identities.TryGetValue(key, out string? previous))
            {
                if (previous != options) throw new InvalidDataException($"As coleções contêm opções diferentes para {app.Id}. Exporte a coleção desejada individualmente antes de executar.");
                continue;
            }
            identities.Add(key, options);
            result.Add(app);
        }
        return result;
    }

    private static List<ProfileAppRef> ReadCompatibilityList(JsonElement entries, string? source, bool winget)
    {
        if (entries.ValueKind != JsonValueKind.Array) throw new ArgumentException("A lista de aplicativos deve ser um array.");
        var apps = new List<ProfileAppRef>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (!TryProperty(entry, winget ? "PackageIdentifier" : "id", out var id)) throw new ArgumentException("Aplicativo sem identificador.");
            string? origin = source;
            if (!winget && TryProperty(entry, "source", out var originElement)) origin = originElement.GetString();
            apps.Add(new() { Id = id.GetString() ?? "", Source = origin });
            if (apps.Count > MaximumApps) throw new ArgumentException("O perfil excede 10000 aplicativos.");
        }
        return apps;
    }

    private static void Validate(ProfileBackupSet backup, string? fallbackName)
    {
        if (backup.SchemaVersion is < 0 or > 1 || backup.Tabs is null || backup.Tabs.Count > 1000)
            throw new InvalidDataException("Versão ou estrutura de backup incompatível.");
        int count = 0;
        foreach (var profile in backup.Tabs)
        {
            if (profile is null || profile.SchemaVersion is < 0 or > 1 || profile.Apps is null)
                throw new InvalidDataException("Perfil incompatível ou sem lista de aplicativos.");
            profile.Name ??= fallbackName;
            profile.Provisioning ??= backup.Provisioning;
            foreach (var app in profile.Apps)
            {
                if (++count > MaximumApps) throw new InvalidDataException("O perfil excede 10000 aplicativos.");
                if (app is null || string.IsNullOrWhiteSpace(app.Id)) throw new ArgumentException("Aplicativo sem identificador.");
                app.Source = app.Source?.Trim().ToLowerInvariant() ?? (app.OfficeOptions is not null ? "office"
                    : app.Id.Length == 12 && app.Id.StartsWith('9') && app.Id.All(char.IsAsciiLetterOrDigit) ? "msstore" : "winget");
                // The app has exported Office configurations with AppEntry's default source.
                // A complete, validated Office configuration determines its actual execution engine.
                if (app.OfficeOptions is not null && app.Source == "winget") app.Source = "office";
                if (app.Source == "office")
                {
                    if (app.OfficeOptions is null) throw new InvalidDataException($"O plano {app.Id} não contém suas opções do Office.");
                    _ = OfficeProfileConfiguration.Build(app.OfficeOptions);
                    if (!string.IsNullOrEmpty(app.PinnedVersion)) throw new InvalidDataException("Versões fixadas de Office não são suportadas pelo perfil.");
                }
                else
                {
                    _ = InteractiveCliInput.ParseRequest(["install", "--id", app.Id, "--source", app.Source]);
                    if (app.OfficeOptions is not null) throw new InvalidDataException("Opções Office exigem a origem office.");
                }
                if (app.PinnedVersion is { } version && (string.IsNullOrWhiteSpace(version) || version.Length > 256 || version.StartsWith('-') || version.Any(char.IsControl)))
                    throw new InvalidDataException($"Versão fixada inválida para {app.Id}.");
            }
        }
    }

    internal static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { value = property.Value; return true; }
        value = default;
        return false;
    }
}
