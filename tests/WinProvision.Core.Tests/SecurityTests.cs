using System.Net;
using System.Net.Http.Headers;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Core.Services.Backup;
using WinProvision.Core.Services.Office;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class SecurityTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = new StringContent("{}"), RequestMessage = request });
        }
    }

    [Fact]
    public async Task RawBackupNeverReceivesApiCredential()
    {
        var apiHandler = new RecordingHandler();
        var rawHandler = new RecordingHandler();
        using var api = new HttpClient(apiHandler);
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-only-credential");
        using var raw = new HttpClient(rawHandler);
        using var backup = new GitHubBackupService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), api, raw);
        using var apiResponse = await backup.SendWithTransientRetryAsync(() => new(HttpMethod.Get, "https://api.github.com/gists/test"), default);
        using var rawResponse = await backup.SendWithTransientRetryAsync(() => new(HttpMethod.Get, "https://gist.githubusercontent.com/user/test/raw/profile.json"), default);
        Assert.Equal("Bearer test-only-credential", apiHandler.Authorization);
        Assert.Null(rawHandler.Authorization);
        Assert.Equal(1, rawHandler.Calls);
    }

    [Theory]
    [InlineData("https://attacker.example/profile.json")]
    [InlineData("https://api.github.com.attacker.example/gists/test")]
    [InlineData("http://gist.githubusercontent.com/user/test/raw/profile.json")]
    [InlineData("https://user:secret@api.github.com/gists/test")]
    [InlineData("https://api.github.com:444/gists/test")]
    public async Task BackupRejectsUnexpectedDestinationsBeforeSending(string url)
    {
        var handler = new RecordingHandler();
        using var api = new HttpClient(handler);
        using var raw = new HttpClient(new RecordingHandler());
        using var backup = new GitHubBackupService(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), api, raw);
        await Assert.ThrowsAsync<HttpRequestException>(() => backup.SendWithTransientRetryAsync(() => new(HttpMethod.Get, url), default));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("https://attacker.example/WinProvision.Store-Setup.exe")]
    [InlineData("https://github.com/other/repo/releases/download/v1/WinProvision.Store-Setup.exe")]
    [InlineData("https://user:secret@github.com/winprovision-services/WinProvision-Store/releases/download/v1/WinProvision.Store-Setup.exe")]
    public async Task UpdaterRejectsUnrelatedInstallerBeforeDownloading(string url)
    {
        var handler = new RecordingHandler();
        using var http = new HttpClient(handler);
        using var updater = new AppUpdateService(http);
        var update = new AppUpdateCheckResult(Success: true, UpdateAvailable: true,
            CurrentVersion: new Version(1, 0), DownloadUrl: url, Sha256: new string('a', 64));
        await Assert.ThrowsAsync<ArgumentException>(() => updater.DownloadInstallerAsync(update));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("Authorization: Bearer test-credential", "test-credential")]
    [InlineData("https://example.test/?access_token=secret-value&name=demo", "secret-value")]
    [InlineData("{\"client_secret\":\"secret-value\"}", "secret-value")]
    public void CredentialsAreRemovedFromDiagnostics(string message, string secret)
    {
        string redacted = SensitiveDataRedactor.Redact(message);
        Assert.DoesNotContain(secret, redacted);
        Assert.Contains("[REDACTED]", redacted);
    }

    [Fact]
    public void OperationLogsDoNotRetainGitHubToken()
    {
        string token = "ghp_" + new string('a', 36);
        var operation = new OperationItem("Test", OperationKind.Install);
        operation.AppendLog("Installer output: " + token);
        Assert.DoesNotContain(token, operation.GetFullLog());
    }

    [Fact]
    public async Task UpdaterAcceptsExpectedRepositoryAndChecksPayloadHash()
    {
        using var http = new HttpClient(new RecordingHandler());
        using var updater = new AppUpdateService(http, "winprovision-services/WinProvision-Store", "WinProvision.Store-Setup.exe");
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("{}")));
        var update = new AppUpdateCheckResult(true, true, DownloadUrl:
            "https://github.com/winprovision-services/WinProvision-Store/releases/download/v1/WinProvision.Store-Setup.exe", Sha256: hash);
        string path = await updater.DownloadInstallerAsync(update);
        try { Assert.Equal("{}", File.ReadAllText(path)); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OfficeRejectsUnsignedCachedExecutable()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            File.WriteAllText(path, "not a signed Microsoft executable");
            Assert.False(OfficeDeploymentToolService.IsCurrentSetup(path));
        }
        finally { File.Delete(path); }
    }

}
