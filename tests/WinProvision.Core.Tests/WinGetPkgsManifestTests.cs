using System.Net;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinGetPkgsManifestTests
{
    private const string Chrome = """
        PackageIdentifier: Google.Chrome
        PackageVersion: 155.0.8059.85
        InstallerType: wix
        Scope: machine
        Installers:
        - Architecture: x64
          InstallerUrl: https://example.com/chrome.msi
          InstallerSha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
        """;

    [Fact]
    public void ReadsOfficialInstallerFieldsAndMsiSilentSwitches()
    {
        var manifest = WinGetPkgsManifestService.ParseInstallerManifest(Chrome, "Google.Chrome", "155.0.8059.85");
        var installer = Assert.Single(manifest.Installers);
        Assert.Equal("https://example.com/chrome.msi", installer.Url);
        Assert.Equal("/qn /norestart", installer.SilentArgs);
        Assert.True(installer.SilentSupported);
        Assert.Equal("machine", installer.Scope);
    }

    [Fact]
    public void RejectsMismatchedPackageAndUnsupportedSilentInstaller()
    {
        Assert.Throws<InvalidDataException>(() =>
            WinGetPkgsManifestService.ParseInstallerManifest(Chrome, "Other.Package", "155.0.8059.85"));
        string exe = Chrome.Replace("InstallerType: wix", "InstallerType: exe", StringComparison.Ordinal);
        var installer = Assert.Single(WinGetPkgsManifestService.ParseInstallerManifest(exe, "Google.Chrome", "155.0.8059.85").Installers);
        Assert.False(installer.SilentSupported);
    }

    [Fact]
    public void AlacrittyDependencyRequiresWingetInsteadOfDirectExecution()
    {
        const string yaml = """
            PackageIdentifier: Alacritty.Alacritty
            PackageVersion: 0.17.0
            InstallerType: wix
            Dependencies:
              PackageDependencies:
              - PackageIdentifier: Microsoft.VCRedist.2015+.x64
            Installers:
            - Architecture: x64
              InstallerUrl: https://example.com/alacritty.msi
              InstallerSha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            """;
        var manifest = WinGetPkgsManifestService.ParseInstallerManifest(yaml, "Alacritty.Alacritty", "0.17.0");
        Assert.False(Assert.Single(manifest.Installers).SilentSupported);
    }

    [Fact]
    public async Task ReadsRequestedVersionFromOfficialRepository()
    {
        using var client = new HttpClient(new FakeGithubHandler());
        var service = new WinGetPkgsManifestService(client);
        var manifest = await service.GetAsync("Google.Chrome", "155.0.8059.85", CancellationToken.None);
        Assert.Equal("155.0.8059.85", manifest.Version);
        Assert.True(Assert.Single(manifest.Installers).SilentSupported);
    }

    private sealed class FakeGithubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != "raw.githubusercontent.com"
                || !request.RequestUri.AbsolutePath.Contains("/155.0.8059.85/", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Chrome) });
        }
    }
}
