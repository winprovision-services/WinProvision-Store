using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WingetCdnCatalogTests
{
    [Theory]
    [InlineData("manifests/c/app/app.json", true)]
    [InlineData("manifests//app/app.json", false)]
    [InlineData("manifests/./app/app.json", false)]
    [InlineData("manifests/../app/app.json", false)]
    [InlineData("manifests/c\\app/app.json", false)]
    [InlineData("manifests/c?/app.json", false)]
    [InlineData("manifests/c#/app.json", false)]
    [InlineData("manifests/c\u0001/app.json", false)]
    public void ValidatesIndexPathsWithoutChangingCatalogRules(string appPath, bool valid)
    {
        string index = $$"""[{"id":"Contoso.App","appPath":"{{appPath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\u0001", "\\u0001", StringComparison.Ordinal)}}","appSha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","name":"App","latestVersion":"1.0"}]""";
        string hash = Hash(index);
        string catalog = """{"schemaVersion":1,"generation":"commit","generatedAtUtc":"2026-10-21T00:00:00Z","packageCount":1,"searchIndexPath":"winget/v1/search/INDEXHASH.json","searchIndexSha256":"INDEXHASH"}"""
            .Replace("INDEXHASH", hash, StringComparison.Ordinal);

        if (valid)
            Assert.Single(WingetCdnCatalog.ParseSnapshot(Encoding.UTF8.GetBytes(catalog), Encoding.UTF8.GetBytes(index)).Apps);
        else
            Assert.Throws<InvalidDataException>(() => WingetCdnCatalog.ParseSnapshot(Encoding.UTF8.GetBytes(catalog), Encoding.UTF8.GetBytes(index)));
    }

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

    [Fact]
    public async Task ReadsWinProvisionPkgsAtPinnedCommitAndPreservesNestedElevation()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        const string detail = """
            {"schemaVersion":1,"source":"winget","id":"Contoso.App","version":"2.0","installers":[{"id":"x64-machine-http","priority":1,"architecture":"x64","scope":"machine","type":"exe","url":"http://contoso.example/insecure.exe","sha256":"CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC"},{"id":"x64-machine-zip","priority":2,"architecture":"x64","scope":"machine","type":"zip","nestedInstallerType":"portable","nestedInstallerFiles":[{"relativeFilePath":"tools/app.exe","portableCommandAlias":"contoso"},{"relativeFilePath":"../evil.exe","portableCommandAlias":"evil"}],"url":"https://contoso.example/setup.zip","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","silent":{"arguments":"/S","source":"manifest"},"elevation":{"requirement":"elevationRequired","source":"manifest"},"successCodes":[],"dependencies":[]}]}
            """;
        string app = """
            {"schemaVersion":1,"source":"winget","id":"Contoso.App","package":{"name":"Contoso App","publisher":"Contoso","homepage":"https://contoso.example"},"latestVersion":"2.0","architectures":["x64"],"versions":[{"version":"2.0","architectures":["x64"],"detailPath":"versions/2.0/app.json","detailSha256":"DETAILHASH"}]}
            """.Replace("DETAILHASH", Hash(detail), StringComparison.Ordinal);
        const string appPath = "c/Contoso/7%2BTaskbarTweaker/app.json";
        string expectedAppHash = Hash(app);
        using var http = new HttpClient(new PinnedGitHubHandler(new Dictionary<string, string>
        {
            [$"/winprovision-services/WinProvision-PKGS/{commit}/c/Contoso/7%252BTaskbarTweaker/app.json"] = app,
            [$"/winprovision-services/WinProvision-PKGS/{commit}/c/Contoso/7%252BTaskbarTweaker/versions/2.0/app.json"] = detail
        }));
        var summary = new WinProvision.Core.Models.AppEntry
        {
            Id = "Contoso.App", Name = "Contoso App", Version = "2.0",
            CatalogDetailPath = appPath, CatalogDetailSha256 = expectedAppHash,
            CatalogDetailCatalogSha256 = commit
        };

        var package = await WingetCdnCatalog.LoadGitHubAppAsync(http, summary, CancellationToken.None);
        Assert.Equal("https://contoso.example", package.Entry.Homepage);
        PackageManifest version = await WingetCdnCatalog.LoadGitHubVersionAsync(http, package, "2.0", CancellationToken.None);
        CatalogInstaller installer = Assert.Single(version.Installers);
        Assert.Equal("elevationRequired", installer.ElevationRequirement);
        Assert.Equal("/S", installer.SilentArgs);
        Assert.True(installer.SilentSupported);
        Assert.Equal("tools/app.exe", installer.NestedInstallerFile);
        Assert.Equal(new[] { "tools/app.exe" }, installer.NestedInstallerFiles);
        Assert.Equal("contoso", installer.PortableCommandAlias);
    }

    [Fact]
    public async Task EncodesPercentInPinnedVersionPathAndRejectsMismatchedHash()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        const string versionName = "2 #%";
        const string detail = """
            {"schemaVersion":1,"source":"winget","id":"Contoso.App","version":"2 #%","installers":[{"architecture":"x64","type":"portable","url":"https://contoso.example/app.zip","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","successCodes":[],"dependencies":[]}]}
            """;
        string app = """
            {"schemaVersion":1,"source":"winget","id":"Contoso.App","package":{"name":"Contoso App","publisher":"Contoso"},"latestVersion":"2 #%","versions":[{"version":"2 #%","detailPath":"versions/2%20%23%25/app.json","detailSha256":"DETAILHASH"}]}
            """.Replace("DETAILHASH", Hash(detail), StringComparison.Ordinal);
        const string appPath = "c/Contoso/7%2BTaskbarTweaker/app.json";
        using var http = new HttpClient(new PinnedGitHubHandler(new Dictionary<string, string>
        {
            [$"/winprovision-services/WinProvision-PKGS/{commit}/c/Contoso/7%252BTaskbarTweaker/app.json"] = app,
            [$"/winprovision-services/WinProvision-PKGS/{commit}/c/Contoso/7%252BTaskbarTweaker/versions/2%2520%2523%2525/app.json"] = detail
        }));
        var summary = new WinProvision.Core.Models.AppEntry
        {
            Id = "Contoso.App", Name = "Contoso App", Version = versionName,
            CatalogDetailPath = appPath, CatalogDetailSha256 = Hash(app), CatalogDetailCatalogSha256 = commit
        };
        var package = await WingetCdnCatalog.LoadGitHubAppAsync(http, summary, CancellationToken.None);
        Assert.Equal(versionName, package.Entry.Version);
        PackageManifest manifest = await WingetCdnCatalog.LoadGitHubVersionAsync(http, package, versionName, CancellationToken.None);
        Assert.Equal(versionName, manifest.Version);

        summary.CatalogDetailSha256 = new string('0', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WingetCdnCatalog.LoadGitHubAppAsync(http, summary, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsVersionWhenEveryInstallerIsInsecure()
    {
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        const string detail = """
            {"schemaVersion":1,"source":"winget","id":"Contoso.App","version":"1.0","installers":[{"architecture":"x64","type":"exe","url":"http://contoso.example/setup.exe","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]}
            """;
        string app = """
            {"schemaVersion":1,"source":"winget","id":"Contoso.App","package":{"name":"Contoso App","publisher":"Contoso"},"latestVersion":"1.0","versions":[{"version":"1.0","detailPath":"versions/1.0/app.json","detailSha256":"DETAILHASH"}]}
            """.Replace("DETAILHASH", Hash(detail), StringComparison.Ordinal);
        const string appPath = "c/Contoso/7%2BTaskbarTweaker/app.json";
        using var http = new HttpClient(new PinnedGitHubHandler(new Dictionary<string, string>
        {
            [$"/winprovision-services/WinProvision-PKGS/{commit}/c/Contoso/7%252BTaskbarTweaker/app.json"] = app,
            [$"/winprovision-services/WinProvision-PKGS/{commit}/c/Contoso/7%252BTaskbarTweaker/versions/1.0/app.json"] = detail
        }));
        var summary = new WinProvision.Core.Models.AppEntry
        {
            Id = "Contoso.App", Name = "Contoso App", Version = "1.0",
            CatalogDetailPath = appPath, CatalogDetailSha256 = Hash(app), CatalogDetailCatalogSha256 = commit
        };
        var package = await WingetCdnCatalog.LoadGitHubAppAsync(http, summary, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            WingetCdnCatalog.LoadGitHubVersionAsync(http, package, "1.0", CancellationToken.None));
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

    private sealed class PinnedGitHubHandler(Dictionary<string, string> documents) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            Assert.Contains("/0123456789abcdef0123456789abcdef01234567/", path, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(documents[path]))
            });
        }
    }
}
