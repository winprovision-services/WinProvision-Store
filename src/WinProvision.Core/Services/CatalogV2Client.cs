using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>Cliente offline-first do catálogo v2 assinado, publicado na CDN.</summary>
public sealed class CatalogV2Client : IDisposable
{
    public const string BaseUrl = "https://cdn.winprovisionstore.com.br/winget/v2/";
    private static readonly int[] SupportedSchemaVersions = [2, 3];
    private const long MaximumCatalogBytes = 512L * 1024 * 1024;
    private const int MaximumJsonBytes = 32 * 1024 * 1024;
    private static readonly Regex SafeRelativePath = new("^[a-zA-Z0-9_@./~-]+$", RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false, MaxDepth = 32 };
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheDirectory;
    private readonly string _databasePath;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CatalogState? _state;
    public bool LastRefreshWasRemote { get; private set; }

    public CatalogV2Client(HttpClient? httpClient = null, string? cacheDirectory = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? NetworkClientFactory.Create(TimeSpan.FromSeconds(45));
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinProvisionStore", "1.0"));
        _cacheDirectory = cacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore", "catalog-v2");
        _databasePath = Path.Combine(_cacheDirectory, "catalog.db");
    }

    public string DatabasePath => _databasePath;
    public CatalogState? CurrentState => _state;

    public async Task<CatalogState> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            long acceptedGeneration = await ReadAcceptedGenerationAsync(cancellationToken).ConfigureAwait(false);
            string? etagText = await ReadSmallTextAsync(Path.Combine(_cacheDirectory, "state.etag"), cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(BaseUrl), "meta/state.json"));
            if (EntityTagHeaderValue.TryParse(etagText, out var etag)) request.Headers.IfNoneMatch.Add(etag);
            using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
            {
                CatalogState? cached = await TryLoadAcceptedStateAsync(cancellationToken).ConfigureAwait(false);
                if (cached is not null) { LastRefreshWasRemote = true; return _state = cached; }
                using var unconditional = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(BaseUrl), "meta/state.json"));
                using HttpResponseMessage retry = await _http.SendAsync(unconditional, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                retry.EnsureSuccessStatusCode();
                LastRefreshWasRemote = true;
                return _state = await AcceptStateResponseAsync(retry, acceptedGeneration, cancellationToken).ConfigureAwait(false);
            }
            response.EnsureSuccessStatusCode();
            LastRefreshWasRemote = true;
            return _state = await AcceptStateResponseAsync(response, acceptedGeneration, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (File.Exists(_databasePath))
        {
            CatalogState? cached = await TryLoadAcceptedStateAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null) { LastRefreshWasRemote = false; return _state = cached; }
            throw;
        }
        finally { _refreshLock.Release(); }
    }

    private async Task<CatalogState> AcceptStateResponseAsync(HttpResponseMessage response, long acceptedGeneration, CancellationToken token)
    {
        byte[] stateBytes = await ReadLimitedAsync(response.Content, 256 * 1024, token).ConfigureAwait(false);
        byte[] signatureBytes = await GetBytesAsync("meta/state.sig", 16 * 1024, token).ConfigureAwait(false);
        var state = ParseState(stateBytes, signatureBytes);
        EnsureNotDowngrade(state, acceptedGeneration);

        string currentSha = await ReadSmallTextAsync(Path.Combine(_cacheDirectory, "catalog.sha256"), token).ConfigureAwait(false) ?? "";
        if (!string.Equals(currentSha, state.CatalogSha256, StringComparison.OrdinalIgnoreCase) || !File.Exists(_databasePath))
            await DownloadAndActivateCatalogAsync(state, token).ConfigureAwait(false);
        else
            await ValidateDatabaseAsync(_databasePath, state, token).ConfigureAwait(false);

        await AtomicFile.WriteAllBytesAsync(Path.Combine(_cacheDirectory, "state.json"), stateBytes, token).ConfigureAwait(false);
        await AtomicFile.WriteAllBytesAsync(Path.Combine(_cacheDirectory, "state.sig"), signatureBytes, token).ConfigureAwait(false);
        await AtomicFile.WriteAllTextAsync(Path.Combine(_cacheDirectory, "generation.txt"), state.Generation.ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false);
        await AtomicFile.WriteAllTextAsync(Path.Combine(_cacheDirectory, "catalog.sha256"), state.CatalogSha256, token).ConfigureAwait(false);
        if (response.Headers.ETag is { } responseEtag)
            await AtomicFile.WriteAllTextAsync(Path.Combine(_cacheDirectory, "state.etag"), responseEtag.ToString(), token).ConfigureAwait(false);
        return state;
    }

    private async Task DownloadAndActivateCatalogAsync(CatalogState state, CancellationToken token)
    {
        byte[] compressed = await GetBytesAsync(state.CatalogPath, checked((int)Math.Min(state.CatalogSize + 2_000_000, MaximumCatalogBytes)), token).ConfigureAwait(false);
        if (!HashEquals(compressed, state.CatalogSha256)) throw new InvalidDataException("O SHA-256 do catálogo Brotli não corresponde ao estado assinado.");
        string temporaryPath = _databasePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new MemoryStream(compressed, writable: false))
            await using (var brotli = new BrotliStream(input, CompressionMode.Decompress))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
                long total = 0;
                try
                {
                    int read;
                    while ((read = await brotli.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) != 0)
                    {
                        total += read;
                        if (total > MaximumCatalogBytes) throw new InvalidDataException("O catálogo descomprimido excede o limite permitido.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            if (!HashEquals(await File.ReadAllBytesAsync(temporaryPath, token).ConfigureAwait(false), state.RawCatalogSha256))
                throw new InvalidDataException("O SHA-256 do catálogo descomprimido não corresponde ao estado assinado.");
            await ValidateDatabaseAsync(temporaryPath, state, token).ConfigureAwait(false);
            File.Move(temporaryPath, _databasePath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    internal static async Task ValidateDatabaseAsync(string path, CatalogState state, CancellationToken token)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != state.UserVersion)
                throw new InvalidDataException("A versão SQLite do catálogo não corresponde a userVersion no estado assinado.");
        }
        var meta = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key,value FROM meta WHERE key IN ('generation','upstreamCommit','schemaVersion')";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) meta[reader.GetString(0)] = reader.GetString(1);
        }
        if (!meta.TryGetValue("generation", out string? generation) || generation != state.Generation.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("meta.generation do catálogo não corresponde à geração assinada.");
        if (!meta.TryGetValue("upstreamCommit", out string? commit) || !string.Equals(commit, state.UpstreamCommit, StringComparison.Ordinal))
            throw new InvalidDataException("O commit upstream do catálogo não corresponde ao estado assinado.");
        if (!meta.TryGetValue("schemaVersion", out string? schema) || schema != state.SchemaVersion.ToString(CultureInfo.InvariantCulture))
            throw new InvalidDataException("meta.schemaVersion do catálogo não corresponde à versão assinada.");
    }

    public async Task<IReadOnlyList<AppEntry>> SearchAsync(string? query, int limit = 100, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_databasePath)) await RefreshAsync(cancellationToken).ConfigureAwait(false);
        limit = Math.Clamp(limit, 1, 50000);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        int schemaVersion = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (!SupportedSchemaVersions.Contains(schemaVersion))
            throw new InvalidDataException($"user_version do catálogo desconhecido: {schemaVersion}.");
        string match = SanitizeFtsQuery(query);
        if (!string.IsNullOrWhiteSpace(query) && match.Length == 0) return [];
        bool hasQuery = match.Length > 0;
        await using var command = connection.CreateCommand();
        string elevationColumn = schemaVersion == 2 ? "p.elev" : "0";
        command.CommandText = hasQuery
            ? $"SELECT p.id,p.slug,p.name,p.publisher,p.latest,p.released,p.arch,p.itype,p.scope,{elevationColumn},p.score,p.nver,p.icon,p.shots,p.license,p.homepage,p.descr,p.tags,p.h,bm25(fts,10.0,6.0,4.0,2.0,1.0,1.0) AS rank FROM fts JOIN pkg p ON p.rowid=fts.rowid WHERE fts MATCH $query ORDER BY rank,p.score DESC,p.name COLLATE NOCASE LIMIT $limit"
            : $"SELECT id,slug,name,publisher,latest,released,arch,itype,scope,{(schemaVersion == 2 ? "elev" : "0")},score,nver,icon,shots,license,homepage,descr,tags,h,0.0 AS rank FROM pkg ORDER BY score DESC,name COLLATE NOCASE LIMIT $limit";
        if (hasQuery) command.Parameters.AddWithValue("$query", match);
        command.Parameters.AddWithValue("$limit", limit);
        var result = new List<AppEntry>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new AppEntry
            {
                Id = reader.GetString(0), Source = "winget", Name = reader.GetString(2), Publisher = reader.GetString(3), Version = reader.GetString(4),
                ReleaseDate = reader.IsDBNull(5) || !DateTimeOffset.TryParse(reader.GetString(5), out var releaseDate) ? null : releaseDate,
                Architectures = DecodeArchitectureBits(reader.GetInt32(6)).ToList(), InstallerTypes = DecodeInstallerTypes(reader.GetInt32(7)).ToList(),
                InstallScopes = DecodeScopeBits(reader.GetInt32(8)).ToList(), CatalogArchitectureBits = reader.GetInt32(6),
                CatalogInstallerTypeBits = reader.GetInt32(7), CatalogScopeBits = reader.GetInt32(8), CatalogElevationBits = reader.GetInt32(9),
                Score = reader.GetInt32(10), CatalogDetailPath = BuildAppPath(reader.GetString(0), reader.GetString(1), reader.GetString(18)), CatalogDetailSha256 = reader.GetString(18),
                Description = reader.IsDBNull(16) ? null : reader.GetString(16),
                Homepage = reader.IsDBNull(15) ? null : reader.GetString(15), License = reader.IsDBNull(14) ? null : reader.GetString(14),
                ScreenshotUrls = [], CatalogOrigin = "catalog-v2"
            });
        }
        return result;
    }

    public async Task<byte[]> GetAppJsonAsync(AppEntry entry, CancellationToken cancellationToken = default)
    {
        string relative = entry.CatalogDetailPath ?? throw new InvalidDataException("O catálogo não forneceu o caminho do app.json.");
        byte[] bytes = await ReadHashedObjectAsync(relative, entry.CatalogDetailSha256, cancellationToken, allowTruncatedAppHash: true).ConfigureAwait(false);
        ValidateAppJson(bytes, entry.Id, entry.CatalogDetailSha256!);
        return bytes;
    }

    public async Task<CatalogV2App> GetPackageAsync(AppEntry entry, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await GetAppJsonAsync(entry, cancellationToken).ConfigureAwait(false);
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        JsonElement package = root.GetProperty("package");
        var result = new AppEntry
        {
            Id = entry.Id, Source = "winget", Name = OptionalString(package, "name") ?? entry.Name,
            Publisher = OptionalString(package, "publisher") ?? entry.Publisher,
            Author = OptionalString(package, "author"), Moniker = OptionalString(package, "moniker"),
            Description = OptionalString(package, "description") ?? entry.Description,
            Homepage = OptionalString(package, "homepage") ?? entry.Homepage, PackageUrl = OptionalString(package, "packageUrl"),
            License = OptionalString(package, "license") ?? entry.License, Version = RequiredString(root, "latestVersion"),
            Architectures = ReadStringArray(root, "architectures"), CatalogDetailPath = entry.CatalogDetailPath,
            CatalogDetailSha256 = entry.CatalogDetailSha256, CatalogDetailBaseUrl = BaseUrl.TrimEnd('/'),
            Score = root.TryGetProperty("catalog", out var catalog) && catalog.TryGetProperty("score", out var score) ? score.GetInt32() : entry.Score,
            Tags = ReadStringArray(package, "tags"), CatalogOrigin = "catalog-v2"
        };
        if (package.TryGetProperty("documentations", out var docs) && docs.ValueKind == JsonValueKind.Array)
            result.DocumentationUrls = docs.EnumerateArray().Select(item => OptionalString(item, "DocumentUrl"))
                .Where(url => !string.IsNullOrWhiteSpace(url)).Cast<string>().ToList();
        var media = new CatalogMedia();
        if (root.TryGetProperty("media", out var mediaElement))
        {
            if (mediaElement.TryGetProperty("icons", out var icons) && icons.ValueKind == JsonValueKind.Array)
            {
                JsonElement? chosen = icons.EnumerateArray().Where(item => OptionalString(item, "path") is not null)
                    .OrderBy(item => item.TryGetProperty("priority", out var priority) ? priority.GetInt32() : int.MaxValue)
                    .Cast<JsonElement?>().FirstOrDefault();
                if (chosen is { } icon) { media.Icon = OptionalString(icon, "path"); media.IconSha256 = OptionalString(icon, "sha256"); }
            }
            if (mediaElement.TryGetProperty("screenshots", out var shots) && shots.ValueKind == JsonValueKind.Array)
                foreach (JsonElement shot in shots.EnumerateArray())
                    if (OptionalString(shot, "path") is { } path)
                    {
                        media.Screenshots.Add(path);
                        if (OptionalString(shot, "sha256") is { } hash) media.ScreenshotSha256[path] = hash;
                    }
        }
        result.Media = media;
        result.ScreenshotUrls = media.Screenshots.Select(path => IconService.ResolveCatalogMediaUrl(result, path, media.ScreenshotSha256.GetValueOrDefault(path)))
            .Where(url => url is not null).Cast<string>().ToList();
        var versions = new List<CatalogV2Version>();
        if (root.TryGetProperty("versions", out var versionsElement) && versionsElement.ValueKind == JsonValueKind.Array)
            foreach (JsonElement version in versionsElement.EnumerateArray())
            {
                DateTimeOffset? releaseDate = OptionalString(version, "releaseDate") is { } dateText
                    && DateTimeOffset.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;
                versions.Add(new CatalogV2Version(RequiredString(version, "version"), OptionalString(version, "detailPath"), OptionalString(version, "detailSha256"), releaseDate));
            }
        result.ReleaseDate = versions.FirstOrDefault()?.ReleaseDate;
        return new CatalogV2App(result, versions);
    }

    public async Task<PackageManifest> GetVersionDetailAsync(AppEntry entry, string version, CancellationToken cancellationToken = default)
    {
        byte[] appBytes = await GetAppJsonAsync(entry, cancellationToken).ConfigureAwait(false);
        using JsonDocument app = JsonDocument.Parse(appBytes);
        JsonElement versions = app.RootElement.GetProperty("versions");
        JsonElement reference = versions.EnumerateArray().FirstOrDefault(item => RequiredString(item, "version") == version);
        if (reference.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A versão solicitada não está publicada no catálogo.");
        if (!reference.TryGetProperty("detailPath", out var pathElement) || pathElement.ValueKind == JsonValueKind.Null)
            throw new InvalidDataException("Esta versão está disponível apenas como informação; não há detalhe de instalação publicado.");
        string relative = pathElement.GetString() ?? "";
        if (!relative.StartsWith("@versions/", StringComparison.Ordinal) || !relative.EndsWith(".json", StringComparison.Ordinal))
            throw new InvalidDataException("O caminho do detalhe de versão é inválido.");
        string detailHash = RequiredString(reference, "detailSha256");
        byte[] detailBytes = await ReadHashedObjectAsync(JoinPackagePath(entry, relative), detailHash, cancellationToken).ConfigureAwait(false);
        return ValidateVersionDetail(detailBytes, entry.Id, version, detailHash);
    }

    private async Task<byte[]> ReadHashedObjectAsync(string relative, string? expectedHash, CancellationToken token,
        bool allowTruncatedAppHash = false)
    {
        if (!IsSha256(expectedHash)) throw new InvalidDataException("O catálogo contém uma referência sem SHA-256 válido.");
        string cachePath = Path.Combine(_cacheDirectory, "objects", expectedHash![..2], expectedHash.ToLowerInvariant());
        if (File.Exists(cachePath)) return await File.ReadAllBytesAsync(cachePath, token).ConfigureAwait(false);
        byte[] bytes = await GetBytesAsync(relative, MaximumJsonBytes, token).ConfigureAwait(false);
        bool hashMatches = allowTruncatedAppHash ? AppHashEquals(bytes, expectedHash) : HashEquals(bytes, expectedHash);
        if (!hashMatches) throw new InvalidDataException("O SHA-256 do objeto do catálogo não confere; o conteúdo foi recusado.");
        await AtomicFile.WriteAllBytesAsync(cachePath, bytes, token).ConfigureAwait(false);
        return bytes;
    }

    private Task<byte[]> GetBytesAsync(string relative, int maximum, CancellationToken token)
    {
        if (!SafeRelativePath.IsMatch(relative) || relative.StartsWith('/') || relative.Contains("..", StringComparison.Ordinal))
            throw new InvalidDataException("O catálogo contém um caminho relativo inválido.");
        return GetBytesCoreAsync(relative, maximum, token);
    }

    private async Task<byte[]> GetBytesCoreAsync(string relative, int maximum, CancellationToken token)
    {
        using var response = await _http.GetAsync(new Uri(new Uri(BaseUrl), relative), HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(response.Content, maximum, token).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpContent content, int maximum, CancellationToken token)
    {
        if (content.Headers.ContentLength is > 0 and var length && length > maximum) throw new InvalidDataException("Objeto do catálogo excede o tamanho máximo.");
        await using Stream stream = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
            {
                if (output.Length + count > maximum) throw new InvalidDataException("Objeto do catálogo excede o tamanho máximo.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static CatalogState ParseState(byte[] bytes, byte[] signature)
    {
        using JsonDocument json = JsonDocument.Parse(bytes);
        JsonElement root = json.RootElement;
        int stateSchema = ReadSchema(root, "state.json");
        string keyId = RequiredString(root, "keyId");
        if (!string.Equals(keyId, "k1", StringComparison.Ordinal)) throw new InvalidDataException($"A chave de assinatura '{keyId}' não é conhecida por esta versão do app.");
        byte[] signatureRaw;
        try { signatureRaw = Convert.FromBase64String(Encoding.UTF8.GetString(signature).Trim()); }
        catch (FormatException ex) { throw new InvalidDataException("A assinatura do catálogo não é base64 válido.", ex); }
        using ECDsa verifier = ECDsa.Create();
        using Stream publicKey = typeof(CatalogV2Client).Assembly.GetManifestResourceStream("WinProvision.Core.Assets.catalog-signing-public.pem")
            ?? throw new InvalidOperationException("A chave pública de assinatura do catálogo não está embutida no app.");
        using var keyReader = new StreamReader(publicKey, Encoding.UTF8);
        verifier.ImportFromPem(keyReader.ReadToEnd());
        if (!verifier.VerifyData(bytes, signatureRaw, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            throw new InvalidDataException("A assinatura ECDSA do estado do catálogo é inválida.");
        JsonElement catalog = root.GetProperty("catalog");
        JsonElement upstream = root.GetProperty("upstream");
        long generation = RequiredInt64(root, "generation");
        int userVersion = checked((int)RequiredInt64(catalog, "userVersion"));
        long size = RequiredInt64(catalog, "size");
        string path = RequiredString(catalog, "path");
        if (!path.StartsWith("meta/catalog.", StringComparison.Ordinal) || !path.EndsWith(".db.br", StringComparison.Ordinal))
            throw new InvalidDataException("O caminho do catálogo no estado é inválido.");
        string sha = RequiredString(catalog, "sha256"), rawSha = RequiredString(catalog, "rawSha256");
        if (!IsSha256(sha) || !IsSha256(rawSha) || generation < 1 || userVersion < 1 || size < 1 || size > MaximumCatalogBytes)
            throw new InvalidDataException("Os metadados assinados do catálogo são inválidos.");
        if (stateSchema != userVersion) throw new InvalidDataException("schemaVersion e userVersion do catálogo não correspondem.");
        return new CatalogState(generation, stateSchema, RequiredString(root, "keyId"), RequiredString(upstream, "commit"), path,
            sha.ToLowerInvariant(), rawSha.ToLowerInvariant(), size, userVersion, RequiredInt64(catalog, "packages"), bytes);
    }

    private async Task<CatalogState?> TryLoadAcceptedStateAsync(CancellationToken token)
    {
        string statePath = Path.Combine(_cacheDirectory, "state.json"), signaturePath = Path.Combine(_cacheDirectory, "state.sig");
        if (!File.Exists(statePath) || !File.Exists(signaturePath) || !File.Exists(_databasePath)) return null;
        CatalogState state = ParseState(await File.ReadAllBytesAsync(statePath, token).ConfigureAwait(false), await File.ReadAllBytesAsync(signaturePath, token).ConfigureAwait(false));
        if (state.Generation < await ReadAcceptedGenerationAsync(token).ConfigureAwait(false)) return null;
        await ValidateDatabaseAsync(_databasePath, state, token).ConfigureAwait(false);
        return state;
    }

    private async Task<long> ReadAcceptedGenerationAsync(CancellationToken token)
    {
        string? value = await ReadSmallTextAsync(Path.Combine(_cacheDirectory, "generation.txt"), token).ConfigureAwait(false);
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long generation) ? generation : 0;
    }

    private static async Task<string?> ReadSmallTextAsync(string path, CancellationToken token) =>
        File.Exists(path) ? await File.ReadAllTextAsync(path, token).ConfigureAwait(false) : null;

    public static string SanitizeFtsQuery(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        string[] terms = Regex.Matches(input, "[\\p{L}\\p{N}_-]+", RegexOptions.CultureInvariant)
            .Select(match => match.Value).Where(term => term.Length > 0).Take(12)
            .Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"*").ToArray();
        return string.Join(" AND ", terms);
    }

    internal static PackageManifest ParseDetail(JsonElement root)
    {
        int schema = ReadSchema(root, "detalhe de versão");
        var manifest = new PackageManifest { Id = RequiredString(root, "id"), Version = RequiredString(root, "version"), Schema = schema };
        if (root.TryGetProperty("releaseDate", out var release) && DateTimeOffset.TryParse(release.GetString(), out var parsedDate)) manifest.ReleaseDate = parsedDate;
        if (root.TryGetProperty("installers", out var installers) && installers.ValueKind == JsonValueKind.Array)
        {
            JsonElement defaults = root.TryGetProperty("defaults", out var defaultValue) && defaultValue.ValueKind == JsonValueKind.Object ? defaultValue : default;
            foreach (JsonElement rawInstaller in installers.EnumerateArray())
            {
            byte[] mergedBytes;
            using (var output = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(output))
                {
                    writer.WriteStartObject();
                    if (defaults.ValueKind == JsonValueKind.Object)
                        foreach (JsonProperty property in defaults.EnumerateObject())
                            if (!rawInstaller.TryGetProperty(property.Name, out _)) property.WriteTo(writer);
                    foreach (JsonProperty property in rawInstaller.EnumerateObject()) property.WriteTo(writer);
                    writer.WriteEndObject();
                }
                mergedBytes = output.ToArray();
            }
            using JsonDocument mergedDocument = JsonDocument.Parse(mergedBytes);
            JsonElement item = mergedDocument.RootElement;
            JsonElement silent = item.TryGetProperty("silent", out var sv) ? sv : default;
            JsonElement silentProgress = item.TryGetProperty("silentWithProgress", out var sp) ? sp : default;
            JsonElement elevation = item.TryGetProperty("elevation", out var ev) ? ev : default;
            JsonElement uninstall = item.TryGetProperty("uninstall", out var un) ? un : default;
            var installer = new CatalogInstaller
            {
                Id = OptionalString(item, "id"), Architecture = OptionalString(item, "architecture"), Type = OptionalString(item, "type"), NestedType = OptionalString(item, "nestedType"), Scope = OptionalString(item, "scope"),
                ElevationRequirement = OptionalString(elevation, "requirement"), Url = RequiredString(item, "url"), Sha256 = RequiredString(item, "sha256"),
                SilentArgs = OptionalString(silent, "arguments"), SilentSource = OptionalString(silent, "source") ?? "unknown",
                SilentWithProgressArgs = OptionalString(silentProgress, "arguments"), SilentWithProgressSource = OptionalString(silentProgress, "source") ?? "unknown",
                InstallerSwitches = ReadDictionary(item, "installerSwitches"), SuccessCodes = ReadIntArray(item, "successCodes"),
                ProductCode = OptionalString(item, "productCode") ?? OptionalString(uninstall, "value"), UpgradeBehavior = OptionalString(item, "upgradeBehavior"),
                InstallModes = ReadStringArray(item, "installModes")
            };
            if (schema == 3)
            {
                installer.Elevation = new CatalogElevationState
                {
                    Decision = RequiredString(elevation, "decision"), Source = RequiredString(elevation, "source"),
                    Requirement = OptionalString(elevation, "requirement"),
                    Prohibited = elevation.TryGetProperty("prohibited", out JsonElement p) && p.ValueKind == JsonValueKind.True
                };
                if (installer.Elevation.Decision is not ("required" or "not-required" or "unknown")
                    || installer.Elevation.Source is not ("declared" or "scope" or "type" or "none"))
                    throw new InvalidDataException("Estado de elevação v3 inválido.");
            }
            else
                installer.Elevation = ToCatalogState(InstallerElevationResolver.Resolve(installer));
            manifest.Installers.Add(installer);
            }
        }
        if (manifest.Installers.Count == 0) throw new InvalidDataException("O detalhe de versão não contém instaladores.");
        foreach (CatalogInstaller installer in manifest.Installers)
            if (!IsHttps(installer.Url) || !IsSha256(installer.Sha256)) throw new InvalidDataException("O detalhe contém um instalador sem HTTPS ou SHA-256 válido.");
        return manifest;
    }

    internal static PackageManifest ParseDetailDocument(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes);
        _ = ReadSchema(document.RootElement, "detalhe de versão");
        return ParseDetail(document.RootElement);
    }

    private static string BuildAppPath(string id, string slug, string hash)
    {
        if (!IsAppHash(hash)) throw new InvalidDataException("O catálogo contém um hash de app.json inválido.");
        string first = id.Length > 0 && char.IsAsciiLetterOrDigit(id[0]) ? char.ToLowerInvariant(id[0]).ToString() : "_";
        return $"manifests/{first}/{slug}/app.{hash[..8]}.json";
    }
    private static string JoinPackagePath(AppEntry entry, string relative) => Path.GetDirectoryName(entry.CatalogDetailPath!.Replace('/', Path.DirectorySeparatorChar))!.Replace(Path.DirectorySeparatorChar, '/') + "/" + relative;
    private static string RequiredString(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new InvalidDataException($"Campo obrigatório '{name}' ausente no catálogo.");
    internal static void EnsureNotDowngrade(CatalogState state, long acceptedGeneration)
    {
        if (state.Generation < acceptedGeneration)
            throw new InvalidDataException($"O catálogo remoto tentou voltar da geração {acceptedGeneration} para {state.Generation}; atualização recusada.");
    }

    internal static void ValidateAppJson(byte[] bytes, string id, string hash)
    {
        if (!AppHashEquals(bytes, hash)) throw new InvalidDataException("O SHA-256 do app.json não corresponde à linha assinada do catálogo.");
        using JsonDocument document = JsonDocument.Parse(bytes);
        if (ReadSchema(document.RootElement, "app.json") != 2)
            throw new InvalidDataException("app.json: esta versão do cliente aceita somente schemaVersion 2.");
        if (RequiredString(document.RootElement, "id") != id) throw new InvalidDataException("O app.json não corresponde ao pacote indicado pelo catálogo.");
    }

    internal static PackageManifest ValidateVersionDetail(byte[] bytes, string id, string version, string hash)
    {
        if (!HashEquals(bytes, hash)) throw new InvalidDataException("O SHA-256 do detalhe de versão não corresponde ao app.json.");
        using JsonDocument document = JsonDocument.Parse(bytes);
        JsonElement root = document.RootElement;
        _ = ReadSchema(root, "detalhe de versão");
        if (RequiredString(root, "id") != id || RequiredString(root, "version") != version)
            throw new InvalidDataException("O detalhe de versão não corresponde ao pacote e à versão solicitados.");
        return ParseDetail(root);
    }
    private static string? OptionalString(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static long RequiredInt64(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out long number) ? number : throw new InvalidDataException($"Campo numérico obrigatório '{name}' ausente no catálogo.");
    private static int ReadSchema(JsonElement element, string label)
    {
        if (!element.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int actual) || !SupportedSchemaVersions.Contains(actual))
            throw new InvalidDataException($"{label}: schemaVersion desconhecido ou inválido (suportados: 2, 3).");
        return actual;
    }
    private static CatalogElevationState ToCatalogState(ResolvedInstallerElevation resolved) => new()
        { Decision = resolved.Decision, Source = resolved.Source, Requirement = resolved.Requirement, Prohibited = resolved.Prohibited };
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    internal static bool HashEquals(byte[] bytes, string expected) => IsSha256(expected)
        && CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), Convert.FromHexString(expected));
    internal static bool AppHashEquals(byte[] bytes, string? expected)
    {
        if (!IsAppHash(expected)) return false;
        byte[] actual = SHA256.HashData(bytes);
        byte[] wanted = Convert.FromHexString(expected!);
        return CryptographicOperations.FixedTimeEquals(actual.AsSpan(0, wanted.Length), wanted);
    }
    private static bool IsAppHash(string? value) => value is { Length: 32 or 64 } && value.All(Uri.IsHexDigit);
    private static bool IsHttps(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0;
    private static string[] DecodeArchitectureBits(int bits) => CatalogBitsContract.Decode("architecture", bits);
    private static string[] DecodeInstallerTypes(int bits) => CatalogBitsContract.Decode("installerType", bits);
    private static string[] DecodeScopeBits(int bits) => CatalogBitsContract.Decode("scope", bits);
    private static Dictionary<string, string> ReadDictionary(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase);
    private static List<int> ReadIntArray(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(v => v.TryGetInt32(out _)).Select(v => v.GetInt32()).ToList() : [];
    private static List<string> ReadStringArray(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList() : [];

    public void Dispose() { _refreshLock.Dispose(); if (_ownsHttp) _http.Dispose(); }
}

public static class CatalogBitsContract
{
    private static readonly JsonDocument Contract = Load();
    private static JsonDocument Load()
    {
        using Stream stream = typeof(CatalogBitsContract).Assembly.GetManifestResourceStream("WinProvision.Core.Assets.catalog-bits.json")
            ?? throw new InvalidOperationException("O contrato de bits do catálogo não está embutido no app.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        JsonDocument document = JsonDocument.Parse(reader.ReadToEnd());
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidDataException("schemaVersion do contrato de bits do catálogo não é suportado.");
        return document;
    }

    public static IReadOnlyDictionary<string, int> ReadMap(string field)
    {
        JsonElement objectElement = Contract.RootElement.GetProperty(field);
        return objectElement.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.GetInt32(), StringComparer.OrdinalIgnoreCase);
    }

    public static string[] Decode(string field, int bits) => ReadMap(field)
        .Where(item => field == "elevation" ? (bits & item.Value) == item.Value : (bits & item.Value) != 0)
        .Select(item => item.Key).ToArray();
}

public sealed record CatalogState(long Generation, int SchemaVersion, string KeyId, string UpstreamCommit, string CatalogPath, string CatalogSha256,
    string RawCatalogSha256, long CatalogSize, int UserVersion, long PackageCount, byte[] SignedStateBytes);

public sealed record CatalogV2Version(string Number, string? DetailPath, string? DetailSha256, DateTimeOffset? ReleaseDate = null);
public sealed record CatalogV2App(AppEntry Entry, IReadOnlyList<CatalogV2Version> Versions);
