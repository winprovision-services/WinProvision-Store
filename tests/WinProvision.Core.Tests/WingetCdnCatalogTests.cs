using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WingetCdnCatalogTests
{
    [Fact]
    public async Task ReadsRelativeMediaAndVersionInstallersAfterHashValidation()
    {
        const string detail = """
            {"schemaVersion":1,"id":"Contoso.App","source":"winget","version":"2.0","publisherUrl":"https://contoso.example","installers":[{"architecture":"x64","scope":"user","type":"exe","url":"https://contoso.example/app.exe","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","elevationRequirement":"elevationProhibited","silent":{"supported":true,"arguments":"/S","source":"manifest"}}]}
            """;
        string app = """{"schemaVersion":1,"id":"Contoso.App","source":"winget","latestVersion":"2.0","package":{"name":"App","publisher":"Contoso"},"architectures":["x64"],"media":{"icon":{"path":"media/icon.png","sha256":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"}},"versions":[{"version":"2.0","detailPath":"versions/2.0/app.json","sha256":"DETAILHASH"}]}"""
            .Replace("DETAILHASH", Hash(detail), StringComparison.Ordinal);
        string index = """[{"id":"Contoso.App","appPath":"manifests/c/contoso/app/app.json","appSha256":"APPHASH","name":"App","publisher":"Contoso","latestVersion":"2.0","architectures":["x64"],"tags":[],"icon":{"path":"media/icon.png","sha256":"BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB"}}]"""
            .Replace("APPHASH", Hash(app), StringComparison.Ordinal);
        string hash = Hash(index);
        string catalog = """{"schemaVersion":1,"generation":"commit","generatedAtUtc":"2026-10-21T00:00:00Z","packageCount":1,"searchIndexPath":"winget/v1/search/INDEXHASH.json","searchIndexSha256":"INDEXHASH"}"""
            .Replace("INDEXHASH", hash, StringComparison.Ordinal);
        using var http = new HttpClient(new StaticHandler(new Dictionary<string, string>
        {
            ["catalog.json"] = catalog,
            [$"search/{hash}.json"] = index,
            ["manifests/c/contoso/app/app.json"] = app,
            ["manifests/c/contoso/app/versions/2.0/app.json"] = detail
        }));

        var snapshot = await WingetCdnCatalog.LoadIndexAsync(http, CancellationToken.None);
        Assert.Single(snapshot.Apps);
        Assert.Contains("/winget/v1/manifests/c/contoso/app/media/icon.png",
            IconService.ResolveCatalogMediaUrl(snapshot.Apps[0], snapshot.Apps[0].Media!.Icon,
                snapshot.Apps[0].Media!.IconSha256));
        var summary = await WingetCdnCatalog.LoadAppAsync(http, snapshot.Apps[0], CancellationToken.None);
        Assert.Equal("2.0", Assert.Single(summary.Versions).Number);
        var version = await WingetCdnCatalog.LoadVersionAsync(http, summary, "2.0", CancellationToken.None);
        Assert.Equal("user", Assert.Single(version.Installers).Scope);
        Assert.True(version.Installers[0].SilentSupported);
        Assert.Equal("elevationProhibited", version.Installers[0].ElevationRequirement);
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class StaticHandler(Dictionary<string, string> documents) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsoluteUri[WingetCdnCatalog.BaseUrl.Length..];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(documents[path]))
            });
        }
    }
}
