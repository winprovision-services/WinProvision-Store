using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>Leitor e atualizador do índice SQLite publicado nas releases de WinProvision-PKGS.</summary>
public interface IReleaseCatalogIndexService
{
    /// <summary>Lê o último banco local validado. Retorna null quando ainda não há cache.</summary>
    Task<ReleaseCatalogSnapshot?> TryLoadLocalSnapshotAsync(CancellationToken cancellationToken = default);

    /// <summary>Consulta o HEAD de manifests, baixa a release correspondente e publica o cache validado.</summary>
    Task<ReleaseCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task ClearCacheAsync(CancellationToken cancellationToken = default);
}

public sealed record ReleaseCatalogSnapshot(
    string SourceCommit,
    int SchemaVersion,
    int AppCount,
    int VersionCount,
    string DatabasePath,
    IReadOnlyList<AppEntry> Apps,
    DateTimeOffset? LastValidatedUtc = null,
    string? DatabaseSha256 = null);

/// <summary>Busca releases por commit, valida o manifesto, SHA-256 e conteúdo SQLite antes de ativar o cache.</summary>
public sealed class ReleaseCatalogIndexService : IReleaseCatalogIndexService, IDisposable
{
    public const string CacheDirectoryName = "ReleaseCatalogIndex";
    private const string Repository = "winprovision-services/WinProvision-PKGS";
    private const string DatabaseAssetName = "winprovision-catalog.sqlite";
    private const string ManifestAssetName = "catalog-manifest.json";
    private const long MaximumDatabaseBytes = 256L * 1024 * 1024;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 16 };
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _cacheDirectory;
    private readonly Uri _gitBaseUri;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly object _workSync = new();
    private CancellationTokenSource _activeWork = new();
    private long _cacheGeneration;
    private bool _disposed;

    public ReleaseCatalogIndexService(HttpClient? httpClient = null, string? cacheDirectory = null,
        Uri? gitBaseUri = null)
    {
        _ownsHttp = httpClient is null;
        _http = httpClient ?? NetworkClientFactory.Create(TimeSpan.FromSeconds(45));
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinProvisionStore", "1.0"));
        _cacheDirectory = cacheDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore", "Cache", CacheDirectoryName);
        _gitBaseUri = gitBaseUri ?? new Uri($"https://github.com/{Repository}.git/");
        if (_gitBaseUri.Scheme != Uri.UriSchemeHttps || !_gitBaseUri.IsDefaultPort || _gitBaseUri.UserInfo.Length != 0
            || !_gitBaseUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || !_gitBaseUri.AbsolutePath.Equals($"/{Repository}.git/", StringComparison.Ordinal))
            throw new ArgumentException("A URI do repositório Git deve ser o endpoint HTTPS público esperado.", nameof(gitBaseUri));
    }

    public async Task<ReleaseCatalogSnapshot?> TryLoadLocalSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string pointerPath = Path.Combine(_cacheDirectory, "current.json");
        if (!File.Exists(pointerPath)) return null;

        try
        {
            using JsonDocument pointer = JsonDocument.Parse(await File.ReadAllBytesAsync(pointerPath, cancellationToken), JsonOptions);
            string commit = Required(pointer.RootElement, "sourceCommit");
            DateTimeOffset? lastValidated = OptionalString(pointer.RootElement, "updatedUtc") is { } updatedText
                && DateTimeOffset.TryParse(updatedText, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset updated)
                    ? updated.ToUniversalTime() : null;
            if (!IsCommit(commit)) return null;
            string directory = GetCommitDirectory(commit);
            string databasePath = Path.Combine(directory, DatabaseAssetName);
            string manifestPath = Path.Combine(directory, ManifestAssetName);
            if (!File.Exists(databasePath) || !File.Exists(manifestPath)) return null;
            byte[] manifestBytes = await File.ReadAllBytesAsync(manifestPath, cancellationToken);
            ReleaseManifest manifest = ParseManifest(manifestBytes, commit);
            if (!await VerifyDatabaseFileAsync(databasePath, manifest, cancellationToken)) return null;
            return await ReadSnapshotAsync(databasePath, manifest, cancellationToken, lastValidated);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
            or InvalidDataException or SqliteException or FormatException or OverflowException)
        {
            CoreLog.Debug($"[ReleaseCatalogIndex] Cache local indisponível: {exception.Message}");
            return null;
        }
    }

    public async Task<ReleaseCatalogSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CancellationTokenSource operationSource;
        long generation;
        lock (_workSync)
        {
            operationSource = _activeWork;
            generation = _cacheGeneration;
        }
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, operationSource.Token);
        CancellationToken token = linkedCancellation.Token;
        await _refreshLock.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            string commit = await GetManifestBranchHeadAsync(token).ConfigureAwait(false);
            string directory = GetCommitDirectory(commit);
            string databasePath = Path.Combine(directory, DatabaseAssetName);
            string manifestPath = Path.Combine(directory, ManifestAssetName);
            if (File.Exists(databasePath) && File.Exists(manifestPath))
            {
                try
                {
                    ReleaseManifest existing = ParseManifest(await File.ReadAllBytesAsync(manifestPath, token), commit);
                    if (await VerifyDatabaseFileAsync(databasePath, existing, token))
                    {
                        ReleaseCatalogSnapshot snapshot = await ReadSnapshotAsync(databasePath, existing, token);
                        DateTimeOffset validatedUtc = DateTimeOffset.UtcNow;
                        File.SetLastWriteTimeUtc(databasePath, validatedUtc.UtcDateTime);
                        await WriteCurrentPointerAsync(existing, token, generation, validatedUtc).ConfigureAwait(false);
                        return snapshot with { LastValidatedUtc = validatedUtc };
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                    or InvalidDataException or SqliteException or FormatException or OverflowException)
                {
                    CoreLog.Warning(exception, "ReleaseCatalogIndex.InvalidCachedRelease");
                }
            }

            string tag = "catalog-v2-" + commit;
            Uri manifestAsset = GetReleaseAssetUri(tag, ManifestAssetName);
            Uri databaseAsset = GetReleaseAssetUri(tag, DatabaseAssetName);
            byte[] manifestBytes = await ReadAssetBytesAsync(manifestAsset, tag, ManifestAssetName, 1024 * 1024, token).ConfigureAwait(false);
            ReleaseManifest parsed = ParseManifest(manifestBytes, commit);
            if (parsed.DatabaseName != DatabaseAssetName)
                throw new InvalidDataException("Nome do banco SQLite não corresponde ao contrato da release.");

            Directory.CreateDirectory(directory);
            string tempPath = Path.Combine(directory, DatabaseAssetName + ".download-" + Guid.NewGuid().ToString("N"));
            string tempManifestPath = Path.Combine(directory, ManifestAssetName + ".download-" + Guid.NewGuid().ToString("N"));
            try
            {
                await DownloadToFileAsync(databaseAsset, tag, tempPath, parsed.SizeBytes, token).ConfigureAwait(false);
                if (!await VerifyDatabaseFileAsync(tempPath, parsed, token).ConfigureAwait(false))
                    throw new InvalidDataException("O banco baixado não passou nas verificações de SHA-256 ou SQLite.");
                ReleaseCatalogSnapshot staged = await ReadSnapshotAsync(tempPath, parsed, token).ConfigureAwait(false);
                await File.WriteAllBytesAsync(tempManifestPath, manifestBytes, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                EnsureGeneration(generation);
                File.Move(tempPath, databasePath, overwrite: true);
                File.Move(tempManifestPath, manifestPath, overwrite: true);
                DateTimeOffset validatedUtc = DateTimeOffset.UtcNow;
                File.SetLastWriteTimeUtc(databasePath, validatedUtc.UtcDateTime);
                await WriteCurrentPointerAsync(parsed, token, generation, validatedUtc).ConfigureAwait(false);
                return staged with { DatabasePath = databasePath, LastValidatedUtc = validatedUtc };
            }
            finally
            {
                TryDelete(tempPath);
                TryDelete(tempManifestPath);
            }
        }
        finally { _refreshLock.Release(); }
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_workSync)
        {
            _cacheGeneration++;
            _activeWork.Cancel();
        }
        bool acquired = false;
        try
        {
            await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(_cacheDirectory)) ClearDirectoryContentsSafely(_cacheDirectory);
        }
        finally
        {
            lock (_workSync)
            {
                _activeWork.Dispose();
                _activeWork = new CancellationTokenSource();
            }
            if (acquired) _refreshLock.Release();
        }
    }

    private static void ClearDirectoryContentsSafely(string root)
    {
        string fullRoot = Path.GetFullPath(root);
        if ((File.GetAttributes(fullRoot) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("O diretório do cache não pode ser um reparse point.");
        foreach (string entry in Directory.EnumerateFileSystemEntries(fullRoot))
        {
            string fullEntry = Path.GetFullPath(entry);
            if (!string.Equals(Path.GetDirectoryName(fullEntry), fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Entrada do cache fora do diretório esperado.");
            DeleteEntryWithoutFollowingReparsePoints(fullEntry);
        }
    }

    private static void DeleteEntryWithoutFollowingReparsePoints(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0)
        {
            File.Delete(path);
            return;
        }
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path, recursive: false);
            return;
        }
        string fullDirectory = Path.GetFullPath(path);
        foreach (string child in Directory.EnumerateFileSystemEntries(fullDirectory))
        {
            string fullChild = Path.GetFullPath(child);
            if (!string.Equals(Path.GetDirectoryName(fullChild), fullDirectory, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Entrada do cache fora do diretório esperado.");
            DeleteEntryWithoutFollowingReparsePoints(fullChild);
        }
        Directory.Delete(fullDirectory, recursive: false);
    }

    private void EnsureGeneration(long expected)
    {
        lock (_workSync)
            if (expected != _cacheGeneration) throw new OperationCanceledException("O cache foi limpo durante a atualização.");
    }

    private async Task<string> GetManifestBranchHeadAsync(CancellationToken token)
    {
        Uri refsUri = new(_gitBaseUri, "info/refs?service=git-upload-pack");
        using var request = new HttpRequestMessage(HttpMethod.Get, refsUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-git-upload-pack-advertisement"));
        request.Headers.TryAddWithoutValidation("Git-Protocol", "version=2");
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        ValidateGitResponse(response, "application/x-git-upload-pack-advertisement", "info/refs");
        byte[] advertisement = await ReadResponseBytesAsync(response, 1024 * 1024, token).ConfigureAwait(false);
        if (!HasV2Capability(advertisement, "ls-refs"))
            throw new InvalidDataException("O servidor Git não anunciou a capacidade ls-refs do protocolo v2.");

        Uri uploadPackUri = new(_gitBaseUri, "git-upload-pack");
        using var post = new HttpRequestMessage(HttpMethod.Post, uploadPackUri);
        post.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-git-upload-pack-result"));
        post.Headers.TryAddWithoutValidation("Git-Protocol", "version=2");
        post.Content = new ByteArrayContent(BuildLsRefsRequest());
        post.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-git-upload-pack-request");
        using HttpResponseMessage refsResponse = await _http.SendAsync(post, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        refsResponse.EnsureSuccessStatusCode();
        ValidateGitResponse(refsResponse, "application/x-git-upload-pack-result", "git-upload-pack");
        byte[] refs = await ReadResponseBytesAsync(refsResponse, 64 * 1024, token).ConfigureAwait(false);
        return ParseManifestRefResponse(refs);
    }

    private void ValidateGitResponse(HttpResponseMessage response, string mediaType, string endpoint)
    {
        Uri expectedUri = new(_gitBaseUri, endpoint);
        Uri finalUri = response.RequestMessage?.RequestUri ?? expectedUri;
        if (finalUri.Scheme != Uri.UriSchemeHttps
            || !finalUri.IsDefaultPort || finalUri.UserInfo.Length != 0
            || !finalUri.Host.Equals(expectedUri.Host, StringComparison.OrdinalIgnoreCase)
            || !finalUri.AbsolutePath.Equals(expectedUri.AbsolutePath, StringComparison.Ordinal)
            || !string.Equals(response.Content.Headers.ContentType?.MediaType, mediaType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Resposta de refs GitHub inválida.");
    }

    private static byte[] BuildLsRefsRequest()
    {
        using var stream = new MemoryStream();
        WritePacket(stream, "command=ls-refs\n");
        stream.Write("0001"u8);
        WritePacket(stream, "ref-prefix refs/heads/manifests\n");
        stream.Write("0000"u8);
        return stream.ToArray();
    }

    private static void WritePacket(Stream stream, string value)
    {
        byte[] payload = Encoding.UTF8.GetBytes(value);
        int packetLength = payload.Length + 4;
        if (packetLength > 0xFFFF) throw new InvalidDataException("Pkt-line Git grande demais.");
        Span<byte> header = stackalloc byte[4];
        const string hex = "0123456789abcdef";
        for (int i = 3; i >= 0; i--)
        {
            header[i] = (byte)hex[packetLength & 15];
            packetLength >>= 4;
        }
        stream.Write(header);
        stream.Write(payload);
    }

    private static bool HasV2Capability(ReadOnlySpan<byte> bytes, string capability)
    {
        List<string> lines = ParsePktLines(bytes, requireServiceHeader: true);
        return lines.Count > 0 && lines[0] == "version 2\n"
            && lines.Skip(1).Any(line => line.TrimEnd('\n').Split('=', 2)[0] == capability);
    }

    internal static string ParseManifestRefResponse(ReadOnlySpan<byte> bytes)
    {
        List<string> lines = ParsePktLines(bytes, requireServiceHeader: false);
        string? manifestCommit = null;
        foreach (string originalLine in lines)
        {
            string line = originalLine;
            if (line is "version 1\n" or "version 2\n") continue;
            int nul = line.IndexOf('\0');
            if (nul >= 0) line = line[..nul];
            int newline = line.IndexOf('\n');
            if (newline >= 0) line = line[..newline];
            int separator = line.IndexOf(' ');
            if (separator <= 0) continue;
            string refName = line[(separator + 1)..];
            if (!refName.Equals("refs/heads/manifests", StringComparison.Ordinal)) continue;
            string sha = line[..separator];
            if (!IsCommit(sha) || manifestCommit is not null)
                throw new InvalidDataException("Ref manifests ausente ou duplicada/inválida.");
            manifestCommit = sha.ToLowerInvariant();
        }
        return manifestCommit ?? throw new InvalidDataException("Resposta ls-refs não contém refs/heads/manifests.");
    }

    private static List<string> ParsePktLines(ReadOnlySpan<byte> bytes, bool requireServiceHeader = false)
    {
        var lines = new List<string>();
        int offset = 0;
        int packetCount = 0;
        bool serviceSeen = !requireServiceHeader;
        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 4 || ++packetCount > 100_000)
                throw new InvalidDataException("Pkt-line Git truncado ou excessivo.");
            int length = 0;
            for (int i = 0; i < 4; i++)
            {
                byte digit = bytes[offset + i];
                int value = digit switch
                {
                    >= (byte)'0' and <= (byte)'9' => digit - (byte)'0',
                    >= (byte)'a' and <= (byte)'f' => digit - (byte)'a' + 10,
                    >= (byte)'A' and <= (byte)'F' => digit - (byte)'A' + 10,
                    _ => -1
                };
                if (value < 0) throw new InvalidDataException("Comprimento pkt-line Git inválido.");
                length = (length << 4) | value;
            }
            offset += 4;
            if (length is 0 or 1 or 2) continue;
            if (length < 4 || length - 4 > bytes.Length - offset)
                throw new InvalidDataException("Pkt-line Git excede a resposta.");
            string line = Encoding.UTF8.GetString(bytes.Slice(offset, length - 4));
            offset += length - 4;
            if (!serviceSeen)
            {
                if (!line.Equals("# service=git-upload-pack\n", StringComparison.Ordinal))
                    throw new InvalidDataException("Cabeçalho de serviço Git inesperado.");
                serviceSeen = true;
                continue;
            }
            lines.Add(line);
        }
        if (!serviceSeen) throw new InvalidDataException("Cabeçalho de serviço Git ausente.");
        return lines;
    }

    private static Uri GetReleaseAssetUri(string tag, string name) =>
        new($"https://github.com/{Repository}/releases/download/{tag}/{name}");

    private async Task<byte[]> ReadAssetBytesAsync(Uri uri, string tag, string assetName, int maximumBytes, CancellationToken token)
    {
        using var response = await SendAssetRequestAsync(uri, tag, assetName, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await ReadResponseBytesAsync(response, maximumBytes, token).ConfigureAwait(false);
    }

    private async Task DownloadToFileAsync(Uri uri, string tag, string path, long expectedSize, CancellationToken token)
    {
        if (expectedSize is <= 0 or > MaximumDatabaseBytes)
            throw new InvalidDataException("Tamanho do banco SQLite fora do limite permitido.");
        using var response = await SendAssetRequestAsync(uri, tag, DatabaseAssetName, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && length != expectedSize)
            throw new InvalidDataException("Tamanho HTTP do banco não corresponde ao manifesto.");
        await using Stream input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > expectedSize || total > MaximumDatabaseBytes)
                throw new InvalidDataException("O banco baixado excede o tamanho informado.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        await output.FlushAsync(token).ConfigureAwait(false);
        if (total != expectedSize) throw new InvalidDataException("Download incompleto do banco SQLite.");
    }

    private async Task<bool> VerifyDatabaseFileAsync(string path, ReleaseManifest manifest, CancellationToken token)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length != manifest.SizeBytes || info.Length <= 0 || info.Length > MaximumDatabaseBytes)
            return false;
        await using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                         128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            byte[] digest = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(digest, Convert.FromHexString(manifest.Sha256))) return false;
        }
        await using SqliteConnection connection = OpenReadOnly(path);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        object? result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (!string.Equals(Convert.ToString(result, CultureInfo.InvariantCulture), "ok", StringComparison.Ordinal)) return false;
        Dictionary<string, string> metadata = await ReadMetadataAsync(connection, token).ConfigureAwait(false);
        return MetadataMatches(metadata, manifest);
    }

    private async Task<ReleaseCatalogSnapshot> ReadSnapshotAsync(string path, ReleaseManifest manifest, CancellationToken token,
        DateTimeOffset? lastValidatedUtc = null)
    {
        await using SqliteConnection connection = OpenReadOnly(path);
        await connection.OpenAsync(token).ConfigureAwait(false);
        await using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA query_only = ON";
            await pragma.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        Dictionary<string, string> metadata = await ReadMetadataAsync(connection, token).ConfigureAwait(false);
        if (!MetadataMatches(metadata, manifest)) throw new InvalidDataException("Metadados do SQLite não correspondem ao manifesto.");
        var apps = new List<AppEntry>(manifest.AppCount);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, source, name, publisher, description, long_description, homepage, package_url,
                   moniker, locale, license, latest_version, architectures_json, tags_json, catalog_score,
                   icon_path, icon_mime_type, icon_sha256, app_path, app_sha256
            FROM apps ORDER BY id COLLATE NOCASE
            """;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        string detailBaseUrl = WingetCdnCatalog.GitHubRawCatalogBaseUrl;
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            string id = reader.GetString(0);
            string source = reader.GetString(1);
            string appPath = reader.GetString(18);
            string? appHash = NullableString(reader, 19);
            if (string.IsNullOrWhiteSpace(id) || !source.Equals("winget", StringComparison.Ordinal)
                || !IsSafeAppPath(appPath) || appHash is null || !IsSha256(appHash))
                throw new InvalidDataException("Registro de app inválido no SQLite.");
            var app = new AppEntry
            {
                Id = id,
                Source = source,
                Name = NullableString(reader, 2) ?? id,
                Publisher = NullableString(reader, 3) ?? string.Empty,
                Description = NullableString(reader, 4),
                Homepage = NullableString(reader, 6),
                PackageUrl = NullableString(reader, 7),
                Moniker = NullableString(reader, 8),
                PackageLocale = NullableString(reader, 9),
                License = NullableString(reader, 10),
                Version = NullableString(reader, 11) ?? string.Empty,
                Architectures = ParseStringList(reader.GetString(12)),
                Tags = ParseStringList(reader.GetString(13)),
                Score = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                CatalogDetailPath = appPath,
                CatalogDetailSha256 = appHash,
                CatalogDetailBaseUrl = detailBaseUrl,
                CatalogDetailCatalogSha256 = manifest.SourceCommit
            };
            string? iconPath = NullableString(reader, 15);
            if (iconPath is not null)
            {
                app.Media = new CatalogMedia
                {
                    Icon = iconPath,
                    IconSha256 = NullableString(reader, 17)
                };
            }
            apps.Add(app);
        }
        if (apps.Count != manifest.AppCount) throw new InvalidDataException("Quantidade de aplicativos no SQLite divergente.");
        return new ReleaseCatalogSnapshot(manifest.SourceCommit, manifest.SchemaVersion, manifest.AppCount,
            manifest.VersionCount, path, apps,
            lastValidatedUtc ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero), manifest.Sha256);
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        return new SqliteConnection(builder.ToString());
    }

    private static async Task<Dictionary<string, string>> ReadMetadataAsync(SqliteConnection connection, CancellationToken token)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM metadata";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            if (!values.TryAdd(reader.GetString(0), reader.GetString(1)))
                throw new InvalidDataException("Chave duplicada nos metadados SQLite.");
        return values;
    }

    private static bool MetadataMatches(IReadOnlyDictionary<string, string> metadata, ReleaseManifest manifest)
    {
        return metadata.TryGetValue("schemaVersion", out string? schema) && int.TryParse(schema, out int schemaVersion)
            && schemaVersion == manifest.SchemaVersion
            && metadata.TryGetValue("sourceCommit", out string? commit)
            && string.Equals(commit, manifest.SourceCommit, StringComparison.OrdinalIgnoreCase)
            && metadata.TryGetValue("appCount", out string? appCount) && int.TryParse(appCount, out int apps)
            && apps == manifest.AppCount
            && metadata.TryGetValue("versionCount", out string? versionCount) && int.TryParse(versionCount, out int versions)
            && versions == manifest.VersionCount;
    }

    private ReleaseManifest ParseManifest(byte[] bytes, string expectedCommit)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, JsonOptions);
        JsonElement root = document.RootElement;
        int schema = RequiredInt(root, "schemaVersion");
        string commit = Required(root, "sourceCommit");
        string database = Required(root, "database");
        string hash = Required(root, "sha256");
        long size = RequiredLong(root, "sizeBytes");
        int apps = RequiredInt(root, "appCount");
        int versions = RequiredInt(root, "versionCount");
        if (schema != 2 || !string.Equals(commit, expectedCommit, StringComparison.OrdinalIgnoreCase)
            || !IsCommit(commit) || database != DatabaseAssetName || !IsSha256(hash)
            || size <= 0 || size > MaximumDatabaseBytes || apps <= 0 || versions < apps)
            throw new InvalidDataException("Manifesto da release SQLite inválido ou incompatível.");
        return new ReleaseManifest(schema, commit.ToLowerInvariant(), database, hash.ToLowerInvariant(), size, apps, versions);
    }

    private static Uri FindAsset(JsonElement release, string name, string tag)
    {
        if (!release.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("A release não lista assets.");
        Uri? match = null;
        foreach (JsonElement asset in assets.EnumerateArray())
        {
            if (!string.Equals(OptionalString(asset, "name"), name, StringComparison.Ordinal)) continue;
            string url = Required(asset, "browser_download_url");
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || !IsExpectedGitHubAsset(parsed, tag, name))
                throw new InvalidDataException("URL de asset da release inválida.");
            if (match is not null) throw new InvalidDataException("Asset duplicado na release.");
            match = parsed;
        }
        return match ?? throw new InvalidDataException($"Asset {name} ausente na release.");
    }

    private async Task<HttpResponseMessage> SendAssetRequestAsync(Uri initialUri, string tag, string assetName, CancellationToken token)
    {
        if (_http.DefaultRequestHeaders.Authorization is not null)
            throw new InvalidOperationException("O cliente HTTP de assets não pode incluir credenciais padrão.");
        if (!IsExpectedGitHubAsset(initialUri, tag, assetName))
            throw new InvalidDataException("URL inicial do asset não corresponde à release esperada.");
        Uri current = initialUri;
        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                Uri? next = response.Headers.Location is { } location
                    ? (location.IsAbsoluteUri ? location : new Uri(current, location)) : null;
                response.Dispose();
                if (redirects >= 5 || next is null || !IsAllowedRedirect(next))
                    throw new InvalidDataException("Redirecionamento de asset GitHub inválido ou excedeu o limite.");
                current = next;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                response.Dispose();
                throw new HttpRequestException("Falha ao baixar asset da release.");
            }
            if (!IsAllowedRedirect(response.RequestMessage?.RequestUri ?? current))
            {
                response.Dispose();
                throw new InvalidDataException("Download do asset saiu dos hosts HTTPS autorizados.");
            }
            return response;
        }
    }

    private static bool IsExpectedGitHubAsset(Uri uri, string tag, string name) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath.Equals($"/{Repository}/releases/download/{tag}/{name}", StringComparison.Ordinal);

    private static bool IsAllowedRedirect(Uri uri) => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && uri.UserInfo.Length == 0 && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private async Task WriteCurrentPointerAsync(ReleaseManifest manifest, CancellationToken token, long generation,
        DateTimeOffset validatedUtc)
    {
        Directory.CreateDirectory(_cacheDirectory);
        string destination = Path.Combine(_cacheDirectory, "current.json");
        string temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("sourceCommit", manifest.SourceCommit);
                writer.WriteString("updatedUtc", validatedUtc);
                writer.WriteEndObject();
            }
            bytes = buffer.ToArray();
        }
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            EnsureGeneration(generation);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private string GetCommitDirectory(string commit) => Path.Combine(_cacheDirectory, commit.ToLowerInvariant());

    private static async Task<byte[]> ReadResponseBytesAsync(HttpResponseMessage response, int maxBytes, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength is long length && length > maxBytes)
            throw new InvalidDataException("Resposta excede o limite permitido.");
        await using Stream stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maxBytes) throw new InvalidDataException("Resposta excede o limite permitido.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static List<string> ParseStringList(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Lista de strings SQLite inválida.");
        var values = new List<string>();
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                values.Add(item.GetString()!);
            else if (item.ValueKind == JsonValueKind.Number)
                values.Add(item.GetRawText());
            else
                throw new InvalidDataException("Item de lista SQLite inválido.");
        }
        return values;
    }

    private static string? NullableString(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static string Required(JsonElement element, string property) => OptionalString(element, property)
        ?? throw new InvalidDataException($"Campo obrigatório ausente: {property}.");
    private static string? OptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static int RequiredInt(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int result)
            ? result : throw new InvalidDataException($"Campo inteiro inválido: {property}.");
    private static long RequiredLong(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt64(out long result)
            ? result : throw new InvalidDataException($"Campo inteiro inválido: {property}.");
    private static bool IsCommit(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static bool IsSafeAppPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || value.Contains('\\')
            || value.Contains(':') || value.Contains('?') || value.Contains('#') || value.Contains('\0')) return false;
        string[] segments = value.Split('/');
        return segments.Length >= 3 && segments[0].Length == 1 && segments[^1] == "app.json"
            && segments.All(segment => segment.Length > 0 && segment is not "." and not ".."
                && !segment.Any(char.IsControl));
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttp) _http.Dispose();
        lock (_workSync) { _activeWork.Cancel(); _activeWork.Dispose(); }
        _refreshLock.Dispose();
    }

    private sealed record ReleaseManifest(int SchemaVersion, string SourceCommit, string DatabaseName,
        string Sha256, long SizeBytes, int AppCount, int VersionCount);
}
