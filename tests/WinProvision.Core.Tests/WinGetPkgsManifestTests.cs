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
        ManifestType: installer
        ManifestVersion: 1.6.0
        InstallerLocale: en-US
        Platform:
        - Windows.Desktop
        MinimumOSVersion: 10.0.17763.0
        InstallModes:
        - silent
        UpgradeBehavior: install
        InstallerSwitches:
          Silent: /qn
          Custom: /custom
        UnsupportedArguments:
        - /unsupported
        DownloadCommandProhibited: false
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
        Assert.Equal("/qn", installer.SilentArgs);
        Assert.True(installer.SilentSupported);
        Assert.Equal("machine", installer.Scope);
        Assert.Equal("en-US", installer.Locale);
        Assert.Equal(["Windows.Desktop"], installer.Platforms);
        Assert.Equal("10.0.17763.0", installer.MinimumOsVersion);
        Assert.Equal(["silent"], installer.InstallModes);
        Assert.Equal("/custom", installer.InstallerSwitches["Custom"]);
        Assert.Equal("install", installer.UpgradeBehavior);
        Assert.Equal(["/unsupported"], installer.UnsupportedArguments);
        Assert.False(installer.DownloadCommandProhibited);
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
    public void RejectsManifestWithoutOfficialRequiredInstallerFields()
    {
        string missingHash = Chrome.Replace(
            "InstallerSha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
            "InstallerSha256: invalid",
            StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() =>
            WinGetPkgsManifestService.ParseInstallerManifest(missingHash, "Google.Chrome", "155.0.8059.85"));
    }

    [Theory]
    [InlineData("msix", "https://example.com/app.msix")]
    [InlineData("appx", "https://example.com/app.appx")]
    [InlineData("msixbundle", "https://example.com/app.msixbundle")]
    [InlineData("appxbundle", "https://example.com/app.appxbundle")]
    [InlineData("zip", "https://example.com/app.zip")]
    [InlineData("portable", "https://example.com/app.exe")]
    [InlineData("font", "https://example.com/app.ttf")]
    public void ReadsOfficialInstallerTypesWithoutRejectingUnsupportedDirectExecution(string type, string url)
    {
        string yaml = $"""
            PackageIdentifier: Contoso.App
            PackageVersion: 1.0.0
            ManifestType: installer
            ManifestVersion: 1.6.0
            Installers:
            - Architecture: x64
              InstallerType: {type}
              InstallerUrl: {url}
              InstallerSha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            """;
        var manifest = WinGetPkgsManifestService.ParseInstallerManifest(yaml, "Contoso.App", "1.0.0");
        var installer = Assert.Single(manifest.Installers);
        Assert.Equal(type, installer.Type);
    }

    [Fact]
    public void ReadsNestedZipInstallerFields()
    {
        const string yaml = """
            PackageIdentifier: Contoso.Portable
            PackageVersion: 1.0.0
            ManifestType: installer
            ManifestVersion: 1.6.0
            Installers:
            - Architecture: x64
              InstallerType: zip
              NestedInstallerType: portable
              NestedInstallerFiles:
              - contoso.exe
              InstallerUrl: https://example.com/contoso.zip
              InstallerSha256: AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA
            """;
        var installer = Assert.Single(WinGetPkgsManifestService.ParseInstallerManifest(
            yaml, "Contoso.Portable", "1.0.0").Installers);
        Assert.True(installer.IsZipWithNestedInstaller);
        Assert.Equal("contoso.exe", installer.NestedInstallerFile);
        Assert.True(installer.IsPortable);
    }

    [Fact]
    public void AlacrittyDependencyRequiresWingetInsteadOfDirectExecution()
    {
        const string yaml = """
            PackageIdentifier: Alacritty.Alacritty
            PackageVersion: 0.17.0
            InstallerType: wix
            ManifestType: installer
            ManifestVersion: 1.6.0
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
