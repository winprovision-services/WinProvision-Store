using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class NetworkEndpointsTests
{
    [Fact]
    public void CatalogFacadeUsesCentralizedNetworkValues()
    {
        Assert.Equal(NetworkEndpoints.CatalogPublicBaseUrl, CatalogEndpoints.PublicBaseUrl);
        Assert.Equal(NetworkEndpoints.CatalogBaseUrl, CatalogEndpoints.CatalogBaseUrl);
        Assert.Equal(NetworkEndpoints.FeaturedCatalogUrl, CatalogEndpoints.FeaturedUrl);
        Assert.Equal(NetworkEndpoints.OfficeIconBaseUrl, CatalogEndpoints.OfficeIconBaseUrl);
    }

    [Fact]
    public void CatalogUrlResolverPreservesExternalUrls()
    {
        const string url = "https://example.test/Store/Catalog/manifest.json";

        Assert.Equal(url, NetworkEndpoints.ResolveCatalogUrl(url));
    }

    [Fact]
    public void CatalogUrlResolverMigratesLegacyCatalogHost()
    {
        const string url = "https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/Catalog/app.json?x=1#part";

        Assert.Equal(
            "https://cdn.winprovisionstore.com.br/Store/Catalog/app.json?x=1#part",
            NetworkEndpoints.ResolveCatalogUrl(url));
    }
}
