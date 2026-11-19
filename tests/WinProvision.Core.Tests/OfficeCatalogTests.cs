using System.Text;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Office;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OfficeCatalogTests
{
    private static string EmbeddedJson => Encoding.UTF8.GetString(OfficeCatalogRuntime.GetEmbeddedBytes());

    [Fact]
    public void CatalogIsValidAndUnknownFieldsAreIgnored()
    {
        string json = EmbeddedJson.Replace("\"schemaVersion\":1,", "\"schemaVersion\":1,\"futureField\":true,", StringComparison.Ordinal);
        OfficeCatalogDocument catalog = OfficeCatalogDocument.Parse(json);
        Assert.Contains(catalog.Apps, app => app.Id == "Word");
        Assert.Contains(catalog.Products, product => product.Group == "visio" && product.IsDefault);
        Assert.Contains(catalog.Products, product => product.Group == "project" && product.IsDefault);
    }

    [Fact]
    public void InvalidJsonAndInvalidOdtIdsAreRejected()
    {
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => OfficeCatalogDocument.Parse("{"));
        Assert.Throws<InvalidDataException>(() => OfficeCatalogDocument.Parse(EmbeddedJson.Replace("\"odtId\":\"Word\"", "\"odtId\":\"Word-!\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void CatalogDeclaresConditionalAppsAndAliases()
    {
        OfficeCatalogDocument catalog = OfficeCatalogDocument.Parse(EmbeddedJson);
        OfficeCatalogProduct enterprise = Assert.Single(catalog.Products, product => product.ProductId == "O365ProPlusRetail");
        Assert.Contains("Teams", enterprise.ConditionalApps, StringComparer.OrdinalIgnoreCase);
        Assert.False(catalog.Apps.Single(app => app.Id == "Teams").DefaultSelected);
        Assert.Equal("O365ProPlusEEANoTeamsRetail", OfficePlanCatalog.ByProductId("O365ProPlusEEANoTeamsRetail")?.ProductId);
        Assert.All(enterprise.AddOns, addOn => Assert.False(string.IsNullOrWhiteSpace(addOn.LicenseWarning)));
    }

    [Fact]
    public void TeamsCanBeDetectedFromRegisteredPackageInventory()
    {
        IReadOnlySet<string> installed = OfficeInstalledProductsDetector.DetectInstalledApplicationIds(
            [], _ => false, _ => null, packageInstalled: name => name == "MSTeams");
        Assert.Contains("Teams", installed);
    }

    [Fact]
    public void BingFromLegacyProfilesIsSilentlyIgnoredInOdtXml()
    {
        var request = new OfficeInstallRequest(OfficePlanCatalog.ByProductId("O365HomePremRetail")!, 64, "pt-br", (string[])["Bing"]);
        string xml = OfficeConfigXmlBuilder.Build(request).ToString(System.Xml.Linq.SaveOptions.None);
        Assert.DoesNotContain("Bing", xml, StringComparison.OrdinalIgnoreCase);
    }
}
