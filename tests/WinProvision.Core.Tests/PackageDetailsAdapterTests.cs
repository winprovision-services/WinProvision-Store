using WinProvision.Core.Models;
using WinProvision.Store.Services;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class PackageDetailsAdapterTests
{
    [Fact]
    public void ChocolateyAdapterOmitsWingetOnlyManifestAndInstallerData()
    {
        var app = new AppEntry
        {
            Id = "steamcmd",
            Name = "SteamCMD",
            Source = "chocolatey",
            WingetManifestAvailable = true,
            Installers = [new CatalogInstaller { Type = "exe", Url = "https://example.test/setup.exe", Sha256 = "abc" }]
        };

        var adapter = new ChocolateyPackageDetailsAdapter();
        PackageDetailsRecord record = adapter.CreateRecord(app);

        Assert.Equal("chocolatey:steamcmd", record.Id);
        Assert.Equal("chocolatey", record.Source);
        Assert.Null(record.ManifestUrl);
        Assert.Null(record.Installer);
        Assert.False(adapter.Manager.Capabilities.ManifestLink);
        Assert.False(adapter.Manager.Capabilities.InstallerInfo);
        Assert.True(adapter.Manager.Capabilities.SkipHashCheck);
        Assert.False(adapter.Manager.Capabilities.SelectScope);
        Assert.False(adapter.Manager.Capabilities.CustomVersion);
        Assert.True(adapter.Manager.Capabilities.VersionSelector);
        Assert.True(adapter.Manager.Capabilities.PackageOptions);
        Assert.True(adapter.Manager.Capabilities.GlobalOptions);
    }

    [Fact]
    public void WingetAdapterMapsInstallerAndManifestIntoSharedRecord()
    {
        var app = new AppEntry
        {
            Id = "VideoLAN.VLC",
            Name = "VLC",
            Source = "winget",
            Version = "3.0.0",
            WingetManifestAvailable = true,
            Installers = [new CatalogInstaller { Type = "exe", Url = "https://example.test/vlc.exe", Sha256 = "abc" }]
        };

        PackageDetailsRecord record = new WingetPackageDetailsAdapter(new WingetPackageDetailsService()).CreateRecord(app);

        Assert.Equal("winget:VideoLAN.VLC", record.Id);
        Assert.Contains("github.com/microsoft/winget-pkgs", record.ManifestUrl, StringComparison.Ordinal);
        Assert.Equal("exe", record.Installer?.Type);
        Assert.Equal("https://example.test/vlc.exe", record.Installer?.Url);
        Assert.Equal("abc", record.Installer?.Sha256);
    }

    [Fact]
    public void MissingCatalogValuesStayAbsentInSharedRecord()
    {
        var app = new AppEntry { Id = "BleachBit.BleachBit", Name = "BleachBit", Source = "winget", WingetManifestAvailable = false };
        PackageDetailsRecord record = new WingetPackageDetailsAdapter(new WingetPackageDetailsService()).CreateRecord(app);

        Assert.Null(record.Homepage);
        Assert.Null(record.License);
        Assert.Null(record.Installer);
        Assert.Equal("https://github.com/microsoft/winget-pkgs/tree/master/manifests/b/BleachBit/BleachBit", record.ManifestUrl);
        Assert.Empty(record.Dependencies);
        Assert.Empty(record.Screenshots);
    }

    [Fact]
    public void WingetManifestPathFollowsRepositorySegmentsForMultiSegmentIds()
    {
        var app = new AppEntry { Id = "Python.Python.3.14", Name = "Python", Source = "winget" };
        PackageDetailsRecord record = new WingetPackageDetailsAdapter(new WingetPackageDetailsService()).CreateRecord(app);

        Assert.Equal("https://github.com/microsoft/winget-pkgs/tree/master/manifests/p/Python/Python/3/14", record.ManifestUrl);
    }

    [Fact]
    public void WingetManifestPathMatchesUniGetUiForIdWithoutDots()
    {
        var app = new AppEntry { Id = "BleachBit", Name = "BleachBit", Source = "winget" };
        PackageDetailsRecord record = new WingetPackageDetailsAdapter(new WingetPackageDetailsService()).CreateRecord(app);

        Assert.Equal("https://github.com/microsoft/winget-pkgs/tree/master/manifests/b/BleachBit/BleachBit", record.ManifestUrl);
    }

    [Fact]
    public void ExistingDetailsJsonWithoutManifestUrlRemainsCompatible()
    {
        const string legacyJson = "{\"schemaVersion\":1,\"id\":\"winget:demo\",\"source\":\"winget\",\"packageId\":\"demo\",\"name\":\"Demo\"}";
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

        PackageDetailsRecord record = JsonSerializer.Deserialize<PackageDetailsRecord>(legacyJson, options)!;

        Assert.Null(record.ManifestUrl);
    }

    [Fact]
    public void SharedRecordHasVersionedJsonShapeAndExtensionBlock()
    {
        var record = new PackageDetailsRecord
        {
            Id = "chocolatey:demo",
            Source = "chocolatey",
            PackageId = "demo",
            Name = "Demo",
            Extra = new Dictionary<string, JsonElement> { ["channel"] = JsonDocument.Parse("\"stable\"").RootElement.Clone() }
        };
        var options = new JsonSerializerOptions { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(record, options));
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("chocolatey", json.RootElement.GetProperty("source").GetString());
        Assert.Equal("stable", json.RootElement.GetProperty("extra").GetProperty("channel").GetString());
    }

    [Fact]
    public void MicrosoftStoreDoesNotExposeReusablePackageOptions()
    {
        var app = new AppEntry { Id = "9WZDNCRFJ3TJ", Name = "Store app", Source = "msstore" };
        var adapter = new MicrosoftStorePackageDetailsAdapter(new WingetPackageDetailsService());

        Assert.False(adapter.Manager.Capabilities.PackageOptions);
        Assert.False(adapter.Manager.Capabilities.GlobalOptions);
        Assert.False(adapter.Manager.Capabilities.InteractiveInstall);
        PackageDetailsRecord record = adapter.CreateRecord(app);
        Assert.Equal("msstore", record.Source);
        Assert.Equal("https://apps.microsoft.com/detail/9WZDNCRFJ3TJ", record.ManifestUrl);
    }
}
