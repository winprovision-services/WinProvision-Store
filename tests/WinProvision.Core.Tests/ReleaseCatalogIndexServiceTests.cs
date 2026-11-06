using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Net.Http.Headers;
using Microsoft.Data.Sqlite;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ReleaseCatalogIndexServiceTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public async Task RefreshDownloadsValidatesAndCachesVersionTwoRelease()
    {
        using var temp = new TemporaryDirectory();
        byte[] database = CreateDatabase(Commit, "a".PadLeft(64, 'a'));
        byte[] manifest = CreateManifest(database, Commit);
        var handler = new ReleaseHandler(Commit, database, manifest);
        using var http = new HttpClient(handler);
        using var service = new ReleaseCatalogIndexService(http, temp.Path);

        ReleaseCatalogSnapshot snapshot = await service.RefreshAsync();

        Assert.Equal(Commit, snapshot.SourceCommit);
        Assert.Equal(2, snapshot.SchemaVersion);
        Assert.Equal(1, snapshot.AppCount);
        Assert.NotNull(snapshot.LastValidatedUtc);
        Assert.True(File.Exists(snapshot.DatabasePath));
        Assert.Single(snapshot.Apps);
        var app = snapshot.Apps[0];
        Assert.Equal("Example.App", app.Id);
        Assert.Equal("Example App", app.Name);
        Assert.Equal("e/Example/App/app.json", app.CatalogDetailPath);
        Assert.Equal(new string('a', 64), app.CatalogDetailSha256);
        Assert.Equal(Commit, app.CatalogDetailCatalogSha256);
        Assert.Equal(WingetCdnCatalog.GitHubRawCatalogBaseUrl, app.CatalogDetailBaseUrl);

        ReleaseCatalogSnapshot? cached = await service.TryLoadLocalSnapshotAsync();
        Assert.NotNull(cached);
        Assert.Equal(Commit, cached.SourceCommit);
        Assert.Single(cached.Apps);
        ReleaseCatalogSnapshot revalidated = await service.RefreshAsync();
        Assert.Equal(Commit, revalidated.SourceCommit);
        Assert.True(revalidated.LastValidatedUtc >= snapshot.LastValidatedUtc);
        Assert.Equal(1, handler.DatabaseDownloads);
    }

    [Fact]
    public async Task InvalidHashDoesNotCreateOrReplaceCurrentCache()
    {
        using var temp = new TemporaryDirectory();
        byte[] database = CreateDatabase(Commit, new string('a', 64));
        using var http = new HttpClient(new ReleaseHandler(Commit, database,
            CreateManifest(database, Commit, hashOverride: new string('b', 64))));
        using var service = new ReleaseCatalogIndexService(http, temp.Path);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.RefreshAsync());

        Assert.Null(await service.TryLoadLocalSnapshotAsync());
        Assert.False(File.Exists(Path.Combine(temp.Path, "current.json")));
    }

    [Fact]
    public async Task ClearCacheRemovesValidatedDatabaseAndPointer()
    {
        using var temp = new TemporaryDirectory();
        byte[] database = CreateDatabase(Commit, new string('a', 64));
        using var http = new HttpClient(new ReleaseHandler(Commit, database, CreateManifest(database, Commit)));
        using var service = new ReleaseCatalogIndexService(http, temp.Path);
        ReleaseCatalogSnapshot snapshot = await service.RefreshAsync();

        await service.ClearCacheAsync();

        Assert.False(File.Exists(snapshot.DatabasePath));
        Assert.Null(await service.TryLoadLocalSnapshotAsync());
    }

    private static byte[] CreateDatabase(string commit, string appHash)
    {
        string path = Path.Combine(Path.GetTempPath(), "release-index-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE metadata (key TEXT PRIMARY KEY, value TEXT NOT NULL) STRICT;
                CREATE TABLE apps (
                    id TEXT PRIMARY KEY, source TEXT NOT NULL, name TEXT, publisher TEXT, description TEXT,
                    long_description TEXT, homepage TEXT, package_url TEXT, moniker TEXT, locale TEXT, license TEXT,
                    latest_version TEXT, architectures_json TEXT NOT NULL, tags_json TEXT NOT NULL,
                    catalog_score INTEGER, icon_path TEXT, icon_mime_type TEXT, icon_sha256 TEXT,
                    app_path TEXT NOT NULL UNIQUE, app_sha256 TEXT NOT NULL
                ) STRICT;
                INSERT INTO metadata VALUES ('schemaVersion', '2'), ('sourceCommit', $commit),
                    ('format', 'WinProvision Catalog SQLite Index'), ('appCount', '1'), ('versionCount', '1');
                INSERT INTO apps VALUES ('Example.App', 'winget', 'Example App', 'Example Publisher', 'Description',
                    NULL, 'https://example.test', NULL, 'example', 'en-US', 'MIT', '2.0', '["x64"]', '["example"]',
                    42, 'e/Example/App/media/icon.png', 'image/png', $iconHash,
                    'e/Example/App/app.json', $appHash);
                """;
            command.Parameters.AddWithValue("$commit", commit);
            command.Parameters.AddWithValue("$iconHash", new string('c', 64));
            command.Parameters.AddWithValue("$appHash", appHash);
            command.ExecuteNonQuery();
            command.Dispose();
            connection.Close();
            connection.Dispose();
            return File.ReadAllBytes(path);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    private static byte[] CreateManifest(byte[] database, string commit, string? hashOverride = null) =>
        Encoding.UTF8.GetBytes($"{{\"schemaVersion\":2,\"sourceCommit\":\"{commit}\",\"database\":\"winprovision-catalog.sqlite\",\"sha256\":\"{hashOverride ?? Convert.ToHexString(SHA256.HashData(database)).ToLowerInvariant()}\",\"sizeBytes\":{database.LongLength},\"appCount\":1,\"versionCount\":1}}");

    private sealed class ReleaseHandler(string commit, byte[] database, byte[] manifest) : HttpMessageHandler
    {
        public int DatabaseDownloads { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string url = request.RequestUri!.ToString();
            if (url == "https://github.com/winprovision-services/WinProvision-PKGS.git/info/refs?service=git-upload-pack")
                return Task.FromResult(GitResponse("application/x-git-upload-pack-advertisement",
                    Pkt("# service=git-upload-pack\n") + "0000" + Pkt("version 2\n") + Pkt("agent=test\n")
                    + Pkt("ls-refs=unborn\n") + "0000"));
            if (url == "https://github.com/winprovision-services/WinProvision-PKGS.git/git-upload-pack")
            {
                string body = Encoding.ASCII.GetString(((ByteArrayContent)request.Content!).ReadAsByteArrayAsync().GetAwaiter().GetResult());
                if (!body.Contains("ref-prefix refs/heads/manifests\n", StringComparison.Ordinal))
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
                return Task.FromResult(GitResponse("application/x-git-upload-pack-result", Pkt($"{commit} refs/heads/manifests\n") + "0000"));
            }
            if (url.EndsWith("/catalog-manifest.json", StringComparison.Ordinal)
                && url.StartsWith("https://github.com/", StringComparison.Ordinal))
                return Task.FromResult(Redirect("https://release-assets.githubusercontent.com/manifest"));
            if (url.EndsWith("/winprovision-catalog.sqlite", StringComparison.Ordinal)
                && url.StartsWith("https://github.com/", StringComparison.Ordinal))
                return Task.FromResult(Redirect("https://release-assets.githubusercontent.com/database"));
            if (url == "https://release-assets.githubusercontent.com/manifest") return Task.FromResult(Ok(manifest));
            if (url == "https://release-assets.githubusercontent.com/database")
            {
                DatabaseDownloads++;
                return Task.FromResult(Ok(database));
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

            static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            static HttpResponseMessage GitResponse(string mediaType, string data)
            {
                var response = Ok(Encoding.UTF8.GetBytes(data));
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
                return response;
            }
            static string Pkt(string line) => ReleaseCatalogIndexServiceTests.PktLine(line);
            static HttpResponseMessage Redirect(string location) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };
        }
    }

    [Fact]
    public void ParsesOnlyExactManifestRefFromPktLineResponse()
    {
        string other = "1111111111111111111111111111111111111111 refs/heads/main\n";
        string wanted = $"{Commit} refs/heads/manifests\n";
        string response = PktLine(other) + PktLine(wanted) + "0000";

        Assert.Equal(Commit, ReleaseCatalogIndexService.ParseManifestRefResponse(Encoding.UTF8.GetBytes(response)));
        Assert.Throws<InvalidDataException>(() => ReleaseCatalogIndexService.ParseManifestRefResponse(Encoding.UTF8.GetBytes(PktLine("1111111111111111111111111111111111111111 refs/heads/main\n") + "0000")));
    }

    private static string PktLine(string line) => (Encoding.ASCII.GetByteCount(line) + 4).ToString("x4") + line;

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wps-release-test-" + Guid.NewGuid().ToString("N"));
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } }
    }
}
