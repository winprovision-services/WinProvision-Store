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
    public async Task DeletingOldGistThenBackingUpDoesNotRestoreItsApps()
    {
        const string oldId = "0123456789abcdef0123456789abcdef";
        const string newId = "dddddddddddddddddddddddddddddddd";
        bool oldExists = true;
        string? uploaded = null;
        using var http = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/user") return Json("{\"login\":\"tester\"}");
            if (path == "/gists" && request.Method == HttpMethod.Get)
                return Json(JsonSerializer.Serialize(oldExists ? new[] { Gist() } : Array.Empty<object>(), TestJson));
            if (path == "/gists/" + oldId && oldExists && request.Method == HttpMethod.Get)
                return Json(JsonSerializer.Serialize(Gist(), TestJson));
            if (path == "/gists/" + oldId && request.Method == HttpMethod.Delete)
            { oldExists = false; return new HttpResponseMessage(HttpStatusCode.NoContent); }
            if (path == "/gists" && request.Method == HttpMethod.Post)
            { uploaded = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Json($"{{\"id\":\"{newId}\"}}"); }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var raw = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var backup = new GitHubBackupService(Path.Combine(Path.GetTempPath(), "WinProvision-recreate-" + Guid.NewGuid().ToString("N")), http, raw);
        Assert.True((await backup.ConnectAsync("test-token")).Success);
        var deleted = await backup.DeleteAllAppGistsAsync();
        Assert.Null(deleted.Error);
        Assert.Equal(1, deleted.DeletedCount);
        var snapshot = new WinProvision.Core.Models.ProfileBackupSet
        { Tabs = [new() { Name = "Nova", Apps = [new() { Id = "Valve.Steam", Source = "winget" }] }] };
        Assert.True((await backup.UploadProfileAsync(snapshot)).Success);
        using var payload = JsonDocument.Parse(uploaded!);
        string profile = payload.RootElement.GetProperty("files").GetProperty("profile.json").GetProperty("content").GetString()!;
        Assert.Contains("Valve.Steam", profile);
        Assert.DoesNotContain("7zip.7zip", profile);
        Assert.False(oldExists);
    }

    [Fact]
    public async Task ListsAppGistsSeparatelyAndTargetsSelectedGistWhenNamesMatch()
    {
        const string first = "0123456789abcdef0123456789abcdef";
        const string second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        object other = new { id = second, description = Description, owner = new { login = "tester" },
            files = new Dictionary<string, object> { ["profile.json"] = new { content = Profile.Replace("7zip.7zip", "VideoLAN.VLC"), truncated = false } } };
        object unrelated = new { id = "cccccccccccccccccccccccccccccccc", description = "Outro aplicativo",
            files = new Dictionary<string, object> { ["profile.json"] = new { content = Profile } } };
        string? patchedPath = null;
        using var http = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/user") return Json("{\"login\":\"tester\"}");
            if (path == "/gists") return Json(JsonSerializer.Serialize(new[] { Gist(), other, unrelated }, TestJson));
            if (request.Method == HttpMethod.Patch) { patchedPath = path; return Json("{}"); }
            if (path == "/gists/" + first) return Json(JsonSerializer.Serialize(Gist(), TestJson));
            if (path == "/gists/" + second) return Json(JsonSerializer.Serialize(other, TestJson));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var raw = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var backup = new GitHubBackupService(Path.Combine(Path.GetTempPath(), "WinProvision-gists-" + Guid.NewGuid().ToString("N")), http, raw);
        Assert.True((await backup.ConnectAsync("test-token")).Success);
        var files = await backup.ListBackupFilesAsync();
        Assert.Contains(files, file => file.GistId == first && file.FileName == "profile.json" && file.AppCount == 1);
        Assert.Contains(files, file => file.GistId == second && file.Collections![0].Apps[0].Id == "VideoLAN.VLC");
        Assert.DoesNotContain(files, file => file.GistId == "cccccccccccccccccccccccccccccccc");
        var downloaded = await backup.DownloadBackupFileAsync("profile.json", gistId: second);
        Assert.Equal("VideoLAN.VLC", downloaded.BackupSet!.Tabs[0].Apps[0].Id);
        Assert.True((await backup.DeleteBackupFileAsync("profile.json", gistId: second)).Success);
        Assert.Equal("/gists/" + second, patchedPath);
    }

    [Fact]
    public async Task UploadReplacesSameNamedProfilesWithoutCombiningOldApps()
    {
        const string first = "0123456789abcdef0123456789abcdef";
        const string second = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        object other = new { id = second, description = Description, owner = new { login = "tester" },
            files = new Dictionary<string, object> { ["profile.json"] = new { content = Profile } } };
        object unrelated = new { id = "cccccccccccccccccccccccccccccccc", description = "Outro app", owner = new { login = "tester" },
            files = new Dictionary<string, object> { ["profile.json"] = new { content = Profile } } };
        var patches = new Dictionary<string, string>();
        using var http = new HttpClient(new Handler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/user") return Json("{\"login\":\"tester\"}");
            if (path == "/gists") return Json(JsonSerializer.Serialize(new[] { Gist(), other, unrelated }, TestJson));
            if (request.Method == HttpMethod.Patch) { patches[path] = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(); return Json("{}"); }
            if (path == "/gists/" + first) return Json(JsonSerializer.Serialize(Gist(), TestJson));
            if (path == "/gists/" + second) return Json(JsonSerializer.Serialize(other, TestJson));
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }));
        using var raw = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var backup = new GitHubBackupService(Path.Combine(Path.GetTempPath(), "WinProvision-upload-" + Guid.NewGuid().ToString("N")), http, raw);
        Assert.True((await backup.ConnectAsync("test-token")).Success);
        var snapshot = new WinProvision.Core.Models.ProfileBackupSet { Tabs = [new() { Name = "Atual", Apps = [new() { Id = "VideoLAN.VLC", Source = "winget" }] }] };
        Assert.True((await backup.UploadProfileAsync(snapshot)).Success);
        Assert.Equal(2, patches.Count);
        foreach (string payload in patches.Values)
        {
            using var document = JsonDocument.Parse(payload);
            string content = document.RootElement.GetProperty("files").GetProperty("profile.json").GetProperty("content").GetString()!;
            Assert.Contains("VideoLAN.VLC", content);
            Assert.DoesNotContain("7zip.7zip", content);
        }
        Assert.Single(JsonDocument.Parse(patches["/gists/" + second]).RootElement.GetProperty("files").EnumerateObject());
    }

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
        id = "0123456789abcdef0123456789abcdef", description = Description, owner = new { login = "tester" },
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
