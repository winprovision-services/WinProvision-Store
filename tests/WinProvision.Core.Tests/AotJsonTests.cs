using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class AotJsonTests
{
    [Fact]
    public void CuratedCatalogMetadataDeserializesWithoutReflection()
    {
        const string json = """
            {"id":"WinProvision.Store","catalogOrigin":"curated","wingetManifestAvailable":false,
             "publisher":"WinProvision Services","author":"WinProvision Services",
             "releaseDate":"2026-10-15T01:53:09Z","catalogSyncedUtc":"2026-10-16T15:00:00Z",
             "releaseNotes":"Versão publicada.","installers":[{"architecture":"x64","type":"inno",
             "url":"https://example.com/setup.exe","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}]}
            """;
        var app = AotJson.Deserialize<AppEntry>(json)!;
        Assert.Equal("curated", app.CatalogOrigin);
        Assert.False(app.WingetManifestAvailable);
        Assert.Equal("WinProvision Services", app.Author);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T01:53:09Z"), app.ReleaseDate);
        Assert.Equal("inno", Assert.Single(app.Installers).Type);
        var restored = AotJson.Deserialize<AppEntry>(AotJson.Serialize(app))!;
        Assert.Equal(app.CatalogSyncedUtc, restored.CatalogSyncedUtc);
        Assert.Equal(app.ReleaseNotes, restored.ReleaseNotes);
    }
    [Fact]
    public void ProfileRoundTripPreservesPinnedVersionAndOfficeOptions()
    {
        Assert.False(JsonSerializer.IsReflectionEnabledByDefault);
        var profile = new ProfileManifest
        {
            Name = "Coleção de teste",
            Apps = [new() { Id = "Microsoft.PowerToys", PinnedVersion = "0.96" },
                new() { Id = "Office", OfficeOptions = new OfficeInstallOptions { DownloadBeforeInstall = true, UseViperDownload = true } }]
        };
        string json = AotJson.Serialize(profile, WinProvisionJsonOptions.Profile);
        var restored = AotJson.Deserialize<ProfileManifest>(json, WinProvisionJsonOptions.Profile)!;
        Assert.Equal(profile.Name, restored.Name);
        Assert.Equal("0.96", restored.Apps[0].PinnedVersion);
        Assert.NotNull(restored.Apps[1].OfficeOptions);
        Assert.True(restored.Apps[1].OfficeOptions!.DownloadBeforeInstall);
        Assert.True(restored.Apps[1].OfficeOptions!.UseViperDownload);
        Assert.DoesNotContain("\"source\": null", json);
    }

    [Fact]
    public void LegacyProfileAcceptsCommentsTrailingCommasAndCaseInsensitiveNames()
    {
        var profile = AotJson.Deserialize<ProfileManifest>(
            "{ /* legado */ \"NAME\": \"Teste\", \"apps\": [{\"id\":\"Test.App\",}],}",
            WinProvisionJsonOptions.Profile)!;
        Assert.Equal("Teste", profile.Name);
        Assert.Equal("Test.App", Assert.Single(profile.Apps).Id);
    }

    [Fact]
    public void PayloadSupportsNestedCollectionsAndEscapesText()
    {
        var payload = new Dictionary<string, object?>
        {
            ["success"] = true,
            ["checks"] = new[] { "Português", "\"aspas\"" }.Select(s => s),
            ["error"] = null
        };
        using var document = JsonDocument.Parse(AotJson.Serialize(payload));
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("\"aspas\"", document.RootElement.GetProperty("checks")[1].GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("error").ValueKind);
    }

    [Fact]
    public void PackageOptionsRetainAllExecutionArguments()
    {
        var options = new PackageInstallOptions(true, "machine", "arm64", true, @"C:\Apps",
            true, "1.2.3", "/custom", true, 2);
        Assert.Equal(options, AotJson.Deserialize<PackageInstallOptions>(AotJson.Serialize(options)));
    }

    [Fact]
    public void HistoryKeepsStringEnumsAndAcceptsLegacyNumericEnums()
    {
        var entry = new OperationHistoryEntry(Guid.NewGuid(), "Teste", OperationKind.Install,
            OperationState.Completed, DateTimeOffset.UtcNow, WingetMethod.ComApi);
        string json = AotJson.Serialize(entry, WinProvisionJsonOptions.Compact);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.String, document.RootElement.GetProperty("kind").ValueKind);
        Assert.NotNull(AotJson.Deserialize<OperationHistoryEntry>("{\"kind\":0,\"state\":0}", WinProvisionJsonOptions.Compact));
    }

    [Fact]
    public void GitHubPayloadKeepsSecretGistContractAndLiteralFileNames()
    {
        var payload = WinProvision.Core.Services.Backup.GitHubBackupService.BuildGistPayload(
            new Dictionary<string, string> { ["profile.json"] = "{\"name\":\"Coleção\"}" }, "Backup");
        using var document = JsonDocument.Parse(AotJson.Serialize(payload));
        Assert.False(document.RootElement.GetProperty("public").GetBoolean());
        Assert.False(document.RootElement.TryGetProperty("@public", out _));
        Assert.Equal("{\"name\":\"Coleção\"}", document.RootElement.GetProperty("files")
            .GetProperty("profile.json").GetProperty("content").GetString());
    }
}
