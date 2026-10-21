using System.Net;
using WinProvision.Core.Services.Backup;
using Xunit;

namespace WinProvision.Core.Tests;

public class GitHubBackupConnectTests
{
    [Fact]
    public async Task GistLookupFailureDoesNotReportConnected()
    {
        using var http = new HttpClient(new Handler(request =>
            request.RequestUri?.AbsolutePath == "/user"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"login\":\"test-user\"}") }
                : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"message\":\"Resource not accessible\"}") }));
        using var raw = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        string folder = Path.Combine(Path.GetTempPath(), "WinProvision-backup-test-" + Guid.NewGuid().ToString("N"));
        using var backup = new GitHubBackupService(folder, http, raw);

        GitHubConnectResult result = await backup.ConnectAsync("test-token");

        Assert.False(result.Success);
        Assert.False(backup.IsConnected);
        Assert.False(File.Exists(Path.Combine(folder, "github-token.dat")));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
