using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WinProvision.Core.Services.Backup;
using Xunit;

namespace WinProvision.Core.Tests;

public class GitHubBackupFilesTests
{
    private const string Description = "WinProvision Store — backup automático de perfil (não editar manualmente)";
    private const string Profile = "{\"schemaVersion\":1,\"tabs\":[{\"name\":\"Coleção A\",\"apps\":[{\"id\":\"7zip.7zip\",\"source\":\"winget\"}]}]}";
    private static readonly JsonSerializerOptions TestJson = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    [Fact]
    public async Task ListsSummarizesImportsAndDeletesOnlySelectedJson()
    {
        string? patch = null;
        using var http = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/user") return Json("{\"login\":\"tester\"}");
            if (path == "/gists") return Json(JsonSerializer.Serialize(new[] { Gist() }, TestJson));
            if (path == "/gists/0123456789abcdef0123456789abcdef" && request.Method == HttpMethod.Get)
                return Json(JsonSerializer.Serialize(Gist(), TestJson));
            if (path == "/gists/0123456789abcdef0123456789abcdef" && request.Method == HttpMethod.Patch)
            {
                patch = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("{}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var raw = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var backup = new GitHubBackupService(Path.Combine(Path.GetTempPath(), "WinProvision-gist-test-" + Guid.NewGuid().ToString("N")), http, raw);
        Assert.True((await backup.ConnectAsync("test-token")).Success);

        IReadOnlyList<CloudBackupFile> files = await backup.ListBackupFilesAsync();
        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.FileName == "profile.json" && file.CanImport && file.CollectionCount == 1 && file.AppCount == 1 && file.Contents == "Coleção A" && file.CreatedAt is null);
        Assert.Contains(files, file => file.FileName == "other.json" && !file.CanImport);
        Assert.True((await backup.DownloadBackupFileAsync("profile.json")).Success);
        Assert.False((await backup.DownloadBackupFileAsync("missing.json")).Success);

        Assert.True((await backup.DeleteBackupFileAsync("other.json")).Success);
        using var document = JsonDocument.Parse(patch!);
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("files").GetProperty("other.json").ValueKind);
        Assert.Single(document.RootElement.GetProperty("files").EnumerateObject());
    }

    [Fact]
    public async Task UnauthorizedSessionStopsFurtherCloudUploads()
    {
        bool expired = false;
        int gistRequestsAfterExpiry = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/user") return Json("{\"login\":\"tester\"}");
            if (expired)
            {
                gistRequestsAfterExpiry++;
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{\"message\":\"Bad credentials\"}") };
            }
            if (request.RequestUri.AbsolutePath == "/gists") return Json(JsonSerializer.Serialize(new[] { Gist() }, TestJson));
            return Json(JsonSerializer.Serialize(Gist(), TestJson));
        }));
        using var raw = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var backup = new GitHubBackupService(Path.Combine(Path.GetTempPath(), "WinProvision-auth-test-" + Guid.NewGuid().ToString("N")), http, raw);
        Assert.True((await backup.ConnectAsync("test-token")).Success);
        expired = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => backup.ListBackupFilesAsync());
        Assert.False(backup.IsConnected);
        Assert.Equal(1, gistRequestsAfterExpiry);
        Assert.False((await backup.UploadProfileAsync(new())).Success);
        Assert.Equal(1, gistRequestsAfterExpiry);
    }

    [Fact]
    public async Task ExpiredAccessTokenRefreshesAndRetriesTheGitHubRequest()
    {
        bool expired = false;
        int gistRequests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/user") return Json("{\"login\":\"tester\"}");
            if (path == "/gists")
            {
                gistRequests++;
                if (expired && request.Headers.Authorization?.Parameter != "fresh-access")
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                return Json("[]");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var raw = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri?.Host == "github.com" && request.RequestUri.AbsolutePath == "/login/oauth/access_token")
                return Json("{\"access_token\":\"fresh-access\",\"refresh_token\":\"rotated-refresh\"}");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        string folder = Path.Combine(Path.GetTempPath(), "WinProvision-refresh-test-" + Guid.NewGuid().ToString("N"));
        using var backup = new GitHubBackupService(folder, http, raw);

        Assert.True((await backup.ConnectAsync("old-access", refreshToken: "old-refresh")).Success);
        expired = true;
        IReadOnlyList<CloudBackupFile> files = await backup.ListBackupFilesAsync();

        Assert.Empty(files);
        Assert.True(backup.IsConnected);
        Assert.Equal(3, gistRequests); // lookup during login, expired request, retry after refresh
    }

    [Fact]
    public async Task DeletesOnlyVerifiedAppGists()
    {
        const string bootstrap = "WinProvision Store — bootstrap de instalação e perfil";
        string[] deleted = [];
        object backupGist = Gist();
        object bootstrapGist = new { id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", description = bootstrap,
            files = new Dictionary<string, object> { ["WinProvision-Bootstrap.ps1"] = new { content = "test" } } };
        object unrelated = new { id = "cccccccccccccccccccccccccccccccc", description = "Outro app",
            files = new Dictionary<string, object> { ["profile.json"] = new { content = Profile } } };
        using var http = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/user") return Json("{\"login\":\"tester\"}");
            if (path == "/gists") return Json(JsonSerializer.Serialize(new[] { backupGist, bootstrapGist, unrelated }, TestJson));
            if (request.Method == HttpMethod.Delete)
            {
                deleted = [.. deleted, path];
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return Json(JsonSerializer.Serialize(path.EndsWith("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", StringComparison.Ordinal)
                ? bootstrapGist : backupGist, TestJson));
        }));
        using var raw = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var backup = new GitHubBackupService(Path.Combine(Path.GetTempPath(), "WinProvision-delete-test-" + Guid.NewGuid().ToString("N")), http, raw);
        Assert.True((await backup.ConnectAsync("test-token")).Success);
        var result = await backup.DeleteAllAppGistsAsync();
        Assert.Null(result.Error);
        Assert.Equal(2, result.DeletedCount);
        Assert.DoesNotContain(deleted, path => path.Contains("cccccccccccccccccccccccccccccccc", StringComparison.Ordinal));
    }

    private static object Gist() => new
    {
        id = "0123456789abcdef0123456789abcdef", description = Description,
        files = new Dictionary<string, object>
        {
            ["profile.json"] = new { content = Profile, truncated = false },
            ["other.json"] = new { content = "{\"unexpected\":true}", truncated = false }
        }
    };
    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
