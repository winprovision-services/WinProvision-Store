using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class AppUpdatePrereleaseTests
{
    private sealed class ReleaseHandler(string stableRelease, string prereleaseList) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string body = request.RequestUri?.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal) == true
                ? stableRelease
                : request.RequestUri?.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal) == true
                    ? prereleaseList
                    : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }

    [Fact]
    public async Task IncludesOnlyPrereleasesNewerThanTheStableCandidate()
    {
        Version current = AppUpdateService.GetCurrentVersion();
        int stableMajor = current.Major + 2;
        int prereleaseMajor = current.Major + 3;
        string stable = Release($"v{stableMajor}.0.0", prerelease: false);
        string prerelease = Release($"v{prereleaseMajor}.0.0-beta.1", prerelease: true);
        using var http = new HttpClient(new ReleaseHandler(stable, $"[{prerelease}]"));
        using var updater = new AppUpdateService(http, "test/repo", "WinProvision.Store-Setup.exe");

        AppUpdateCheckResult result = await updater.CheckForUpdateAsync(includePrereleases: true);

        Assert.True(result.Success);
        Assert.True(result.UpdateAvailable);
        Assert.Equal(prereleaseMajor, result.LatestVersion?.Major);
        Assert.Contains("beta", result.ReleaseLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DoesNotOfferPrereleaseWithSameVersionCoreAsInstalledRelease()
    {
        Version current = AppUpdateService.GetCurrentVersion();
        string version = current.ToString(3);
        string stable = Release($"v{version}", prerelease: false);
        string prerelease = Release($"v{version}-beta.1", prerelease: true);
        using var http = new HttpClient(new ReleaseHandler(stable, $"[{prerelease}]"));
        using var updater = new AppUpdateService(http, "test/repo", "WinProvision.Store-Setup.exe");

        AppUpdateCheckResult result = await updater.CheckForUpdateAsync(includePrereleases: true);

        Assert.True(result.Success);
        Assert.False(result.UpdateAvailable);
    }

    private static string Release(string tag, bool prerelease)
    {
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tag)));
        string flag = prerelease ? "true" : "false";
        return $$"""
        {
          "tag_name": "{{tag}}",
          "name": "{{tag}}",
          "prerelease": {{flag}},
          "draft": false,
          "html_url": "https://github.com/test/repo/releases/tag/{{tag}}",
          "assets": [{
            "name": "WinProvision.Store-Setup.exe",
            "browser_download_url": "https://github.com/test/repo/releases/download/{{tag}}/WinProvision.Store-Setup.exe",
            "digest": "sha256:{{digest}}"
          }]
        }
        """;
    }
}
