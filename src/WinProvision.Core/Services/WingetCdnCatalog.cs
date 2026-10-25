using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>Leitor do contrato público winget/v1. Os caminhos são sempre relativos à CDN.</summary>
public static class WingetCdnCatalog
{
    public const string BaseUrl = CatalogEndpoints.PublicBaseUrl + "/winget/v1/";
    private const int MaximumJsonBytes = 64 * 1024 * 1024;

    public sealed record Snapshot(string Generation, string IndexSha256, DateTimeOffset GeneratedAt,
        List<AppEntry> Apps, byte[] CatalogBytes, byte[] IndexBytes);
    public sealed record Version(string Number, string DetailPath, string Sha256, List<string> Architectures);
    public sealed record App(AppEntry Entry, List<Version> Versions);

    public static async Task<Snapshot> LoadIndexAsync(HttpClient http, CancellationToken token, Snapshot? cached = null)
    {
        byte[] catalogBytes = await ReadAsync(http, "catalog.json", token);
        using var catalogDocument = JsonDocument.Parse(catalogBytes);
        JsonElement catalog = catalogDocument.RootElement;
        if (Int(catalog, "schemaVersion") != 1 || Int(catalog, "packageCount") is not > 0)
            throw new InvalidDataException("Catálogo WinGet v1 inválido.");
        string indexPath = Required(catalog, "searchIndexPath");
        string hash = Required(catalog, "searchIndexSha256");
        if (!IsSha256(hash) || indexPath != "winget/v1/search/" + hash + ".json")
            throw new InvalidDataException("Referência do índice WinGet inválida.");
        byte[] indexBytes = cached is not null && cached.IndexSha256.Equals(hash, StringComparison.OrdinalIgnoreCase)
            ? cached.IndexBytes
            : await ReadAsync(http, indexPath["winget/v1/".Length..], token, hash);
        return ParseSnapshot(catalogBytes, indexBytes);
    }

    public static Snapshot ParseSnapshot(byte[] catalogBytes, byte[] indexBytes)
    {
        using var catalogDocument = JsonDocument.Parse(catalogBytes);
        JsonElement catalog = catalogDocument.RootElement;
        if (Int(catalog, "schemaVersion") != 1 || Int(catalog, "packageCount") is not > 0)
            throw new InvalidDataException("Catálogo WinGet v1 inválido.");
        string indexPath = Required(catalog, "searchIndexPath");
        string hash = Required(catalog, "searchIndexSha256");
        if (!IsSha256(hash) || indexPath != "winget/v1/search/" + hash + ".json"
            || !SHA256.HashData(indexBytes).AsSpan().SequenceEqual(Convert.FromHexString(hash)))
            throw new InvalidDataException("Índice WinGet não corresponde ao catálogo.");
        using var indexDocument = JsonDocument.Parse(indexBytes);
        if (indexDocument.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Índice WinGet inválido.");
        var apps = new List<AppEntry>(Int(catalog, "packageCount"));
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement item in indexDocument.RootElement.EnumerateArray())
        {
            string id = Required(item, "id");
            string path = Required(item, "appPath");
            string appHash = Required(item, "appSha256");
            if (!ids.Add(id) || !SafePath(path, "manifests/", "/app.json"))
                throw new InvalidDataException("Entrada ou caminho duplicado no índice WinGet.");
            if (!IsSha256(appHash)) throw new InvalidDataException("Hash do app geral inválido.");
            apps.Add(new AppEntry
            {
                Id = id, Source = "winget", Name = Required(item, "name"),
                Publisher = Optional(item, "publisher") ?? "", Description = Optional(item, "description"),
                Version = Required(item, "latestVersion"), Tags = Strings(item, "tags"),
                PackageLocale = Optional(item, "locale"), Score = Int(item, "score"),
                Architectures = Strings(item, "architectures"), CatalogDetailPath = path,
                CatalogDetailSha256 = appHash,
                CatalogDetailBaseUrl = BaseUrl.TrimEnd('/'), CatalogDetailCatalogSha256 = hash
            });
            if (item.TryGetProperty("icon", out JsonElement icon) && icon.ValueKind == JsonValueKind.Object)
            {
                string mediaPath = Required(icon, "path");
                string mediaHash = Required(icon, "sha256");
                if (!SafePath(mediaPath, "media/", ".png") && !SafePath(mediaPath, "media/", ".jpg")
                    && !SafePath(mediaPath, "media/", ".webp") && !SafePath(mediaPath, "media/", ".jpeg"))
                    throw new InvalidDataException("Caminho do ícone WinGet inválido.");
                if (!IsSha256(mediaHash)) throw new InvalidDataException("Hash do ícone WinGet inválido.");
                apps[^1].Media = new CatalogMedia { Icon = mediaPath, IconSha256 = mediaHash };
            }
        }
        if (apps.Count != Int(catalog, "packageCount"))
            throw new InvalidDataException("Contagem do índice WinGet não corresponde ao catálogo.");
        if (!DateTimeOffset.TryParse(Required(catalog, "generatedAtUtc"), out var generated))
            throw new InvalidDataException("Data do catálogo WinGet inválida.");
        return new Snapshot(Required(catalog, "generation"), hash,
            generated, apps, catalogBytes, indexBytes);
    }

    public static async Task<App> LoadAppAsync(HttpClient http, AppEntry summary, CancellationToken token,
        string? cacheDirectory = null)
    {
        string path = summary.CatalogDetailPath ?? "";
        if (!SafePath(path, "manifests/", "/app.json"))
            throw new InvalidDataException("Caminho do aplicativo WinGet inválido.");
        using var document = JsonDocument.Parse(await ReadCachedOrRemoteAsync(
            http, path, token, summary.CatalogDetailSha256, cacheDirectory));
        JsonElement root = document.RootElement;
        if (Int(root, "schemaVersion") != 1 || Required(root, "id") != summary.Id
            || Required(root, "source") != "winget")
            throw new InvalidDataException("Identidade do aplicativo WinGet inválida.");
        var versions = new List<Version>();
        if (!root.TryGetProperty("versions", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("O aplicativo WinGet não lista versões.");
        foreach (JsonElement item in list.EnumerateArray())
        {
            string number = Required(item, "version");
            string detailPath = Required(item, "detailPath");
            string hash = Required(item, "sha256");
            if (!SafePath(detailPath, "versions/", "/app.json")
                || detailPath != $"versions/{number}/app.json" || !IsSha256(hash))
                throw new InvalidDataException("Referência da versão WinGet inválida.");
            versions.Add(new Version(number, detailPath, hash, Strings(item, "architectures")));
        }
        string latest = Required(root, "latestVersion");
        if (versions.Count == 0 || !versions.Any(item => item.Number == latest)
            || !string.IsNullOrWhiteSpace(summary.Version)
                && !latest.Equals(summary.Version, StringComparison.Ordinal))
            throw new InvalidDataException("A versão mais recente não corresponde ao índice ou não está na lista do aplicativo.");
        if (!root.TryGetProperty("package", out JsonElement package) || package.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Metadados do aplicativo WinGet ausentes.");
        var app = new AppEntry
        {
            Id = summary.Id, Source = "winget", Name = Optional(package, "name") ?? summary.Name,
            Publisher = Optional(package, "publisher") ?? summary.Publisher,
            Description = Optional(package, "description") ?? summary.Description,
            Homepage = Optional(package, "homepage"), License = Optional(package, "license"),
            Moniker = Optional(package, "moniker"), PackageLocale = Optional(package, "locale"),
            Tags = Strings(package, "tags"), RegionTags = Strings(package, "regionTags"),
            Version = latest, Architectures = Strings(root, "architectures"),
            CatalogDetailPath = path, CatalogDetailBaseUrl = BaseUrl.TrimEnd('/'),
            CatalogDetailCatalogSha256 = summary.CatalogDetailCatalogSha256,
            Media = ReadMedia(root)
        };
        if (root.TryGetProperty("catalog", out JsonElement score)) app.Score = Int(score, "score");
        return new App(app, versions);
    }

    public static async Task<PackageManifest> LoadVersionAsync(HttpClient http, App app,
        string? requestedVersion, CancellationToken token, string? cacheDirectory = null)
    {
        string version = string.IsNullOrWhiteSpace(requestedVersion) ? app.Entry.Version : requestedVersion;
        Version reference = app.Versions.FirstOrDefault(item => item.Number == version)
            ?? throw new InvalidDataException("A versão solicitada não consta no catálogo publicado.");
        string basePath = app.Entry.CatalogDetailPath![..^"app.json".Length];
        byte[] bytes = await ReadCachedOrRemoteAsync(
            http, basePath + reference.DetailPath, token, reference.Sha256, cacheDirectory);
        using var document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        if (Int(root, "schemaVersion") != 1 || Required(root, "id") != app.Entry.Id
            || Required(root, "version") != version || Required(root, "source") != "winget")
            throw new InvalidDataException("O detalhe da versão não corresponde à referência validada.");
        if (!root.TryGetProperty("installers", out JsonElement installers) || installers.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("A versão não contém instaladores.");
        var result = new PackageManifest
        {
            Id = app.Entry.Id, Source = "winget", Version = version, Schema = 1,
            PublisherUrl = Optional(root, "publisherUrl"), LicenseUrl = Optional(root, "licenseUrl"),
            ReleaseNotesUrl = Optional(root, "releaseNotesUrl")
        };
        if (DateTimeOffset.TryParse(Optional(root, "releaseDate"), out var date)) result.ReleaseDate = date;
        foreach (JsonElement item in installers.EnumerateArray())
        {
            JsonElement silent = item.TryGetProperty("silent", out var value) ? value : default;
            var installer = new CatalogInstaller
            {
                Architecture = Optional(item, "architecture"), Type = Optional(item, "type"),
                NestedType = Optional(item, "nestedType"), NestedInstallerFile = Optional(item, "nestedInstallerFile"),
                PortableCommandAlias = Optional(item, "portableCommandAlias"),
                Scope = Optional(item, "scope"), ElevationRequirement = Optional(item, "elevationRequirement"),
                ProductCode = Optional(item, "productCode"), Url = Required(item, "url"),
                Sha256 = Required(item, "sha256"), SilentArgs = Optional(silent, "arguments"),
                SilentSource = Optional(silent, "source") ?? "none",
                SilentSupported = Bool(silent, "supported"), SuccessCodes = Integers(item, "successCodes")
            };
            result.Installers.Add(installer);
        }
        return result;
    }

    private static async Task<byte[]> ReadCachedOrRemoteAsync(
        HttpClient http, string path, CancellationToken token, string? sha256, string? cacheDirectory)
    {
        if (cacheDirectory is null || !IsSha256(sha256 ?? string.Empty))
            return await ReadAsync(http, path, token, sha256);

        string cacheRoot = Path.Combine(cacheDirectory, "winget-v1-details");
        string cachePath = Path.Combine(cacheRoot, sha256![..2], sha256 + ".json");
        try
        {
            if (File.Exists(cachePath))
            {
                var attributes = File.GetAttributes(cachePath);
                var info = new FileInfo(cachePath);
                if ((attributes & FileAttributes.ReparsePoint) == 0 && info.Length is > 0 and <= MaximumJsonBytes)
                {
                    byte[] cached = await File.ReadAllBytesAsync(cachePath, token);
                    if (SHA256.HashData(cached).AsSpan().SequenceEqual(Convert.FromHexString(sha256)))
                        return cached;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cache local inválido/inacessível não impede consulta remota.
            CoreLog.Warning(ex, "ReadWingetCatalogDetailCache");
        }

        byte[] remote = await ReadAsync(http, path, token, sha256);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await AtomicFile.WriteAllBytesAsync(cachePath, remote, token);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A resposta já foi íntegra; falha de persistência só reduz o desempenho futuro.
            CoreLog.Warning(ex, "WriteWingetCatalogDetailCache");
        }
        return remote;
    }

    public static void ApplyVersionMetadata(AppEntry entry, JsonElement detail)
    {
        entry.PublisherUrl = Optional(detail, "publisherUrl");
        entry.LicenseUrl = Optional(detail, "licenseUrl");
        entry.ReleaseNotesUrl = Optional(detail, "releaseNotesUrl");
        if (DateTimeOffset.TryParse(Optional(detail, "releaseDate"), out var date)) entry.ReleaseDate = date;
    }

    private static CatalogMedia? ReadMedia(JsonElement root)
    {
        if (!root.TryGetProperty("media", out JsonElement media) || media.ValueKind != JsonValueKind.Object)
            return null;
        var result = new CatalogMedia();
        if (media.TryGetProperty("icon", out JsonElement icon) && icon.ValueKind == JsonValueKind.Object)
        {
            result.Icon = Optional(icon, "path"); result.IconSha256 = Optional(icon, "sha256");
        }
        if (media.TryGetProperty("screenshots", out JsonElement screenshots) && screenshots.ValueKind == JsonValueKind.Array)
            foreach (JsonElement shot in screenshots.EnumerateArray())
            {
                string? path = Optional(shot, "path");
                string? hash = Optional(shot, "sha256");
                if (path is null || hash is null) continue;
                result.Screenshots.Add(path); result.ScreenshotSha256[path] = hash;
            }
        return result;
    }

    private static async Task<byte[]> ReadAsync(HttpClient http, string path, CancellationToken token, string? sha256 = null)
    {
        if (!SafePath(path, "", ".json")) throw new InvalidDataException("Caminho CDN inválido.");
        string encoded = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
        using var response = await http.GetAsync(BaseUrl + encoded, HttpCompletionOption.ResponseHeadersRead, token);
        if (response.RequestMessage?.RequestUri is not { } finalUri
            || finalUri.Scheme != Uri.UriSchemeHttps
            || !finalUri.IsDefaultPort || finalUri.UserInfo.Length != 0
            || !finalUri.Host.Equals(new Uri(BaseUrl).Host, StringComparison.OrdinalIgnoreCase)
            || !finalUri.AbsolutePath.StartsWith("/winget/v1/", StringComparison.Ordinal))
            throw new InvalidDataException("A resposta da CDN saiu da origem permitida.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumJsonBytes)
            throw new InvalidDataException("JSON CDN excede o limite de tamanho.");
        byte[] bytes = await response.Content.ReadAsByteArrayAsync(token);
        if (bytes.Length > MaximumJsonBytes) throw new InvalidDataException("JSON CDN excede o limite de tamanho.");
        if (sha256 is not null && !SHA256.HashData(bytes).AsSpan().SequenceEqual(Convert.FromHexString(sha256)))
            throw new InvalidDataException("Hash SHA-256 do JSON CDN divergente.");
        return bytes;
    }

    private static bool SafePath(string path, string prefix, string suffix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal)
            || !path.EndsWith(suffix, StringComparison.Ordinal) || path.Length > 512)
            return false;

        ReadOnlySpan<char> remaining = path;
        while (!remaining.IsEmpty)
        {
            int separator = remaining.IndexOf('/');
            ReadOnlySpan<char> segment = separator < 0 ? remaining : remaining[..separator];
            if (segment.IsEmpty || segment is "." or "..") return false;
            foreach (char character in segment)
                if (char.IsControl(character) || character is '\\' or '?' or '#') return false;
            if (separator < 0) return true;
            remaining = remaining[(separator + 1)..];
        }
        return false;
    }
    private static bool IsSha256(string value)
    {
        if (value.Length != 64) return false;
        foreach (char character in value)
            if (!Uri.IsHexDigit(character)) return false;
        return true;
    }
    private static string Required(JsonElement obj, string name) => Optional(obj, name)
        ?? throw new InvalidDataException($"Campo obrigatório ausente: {name}.");
    private static string? Optional(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int Int(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result) ? result : 0;
    private static bool Bool(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.True;
    private static List<string> Strings(JsonElement obj, string name)
    {
        var result = new List<string>();
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement item in value.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String) result.Add(item.GetString()!);
        return result;
    }
    private static List<int> Integers(JsonElement obj, string name)
    {
        var result = new List<int>();
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array) return result;
        foreach (JsonElement item in value.EnumerateArray())
            if (item.TryGetInt32(out int number)) result.Add(number);
        return result;
    }
}
