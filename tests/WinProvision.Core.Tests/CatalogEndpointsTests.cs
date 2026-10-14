using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class CatalogEndpointsTests
{
    [Fact]
    public void LegacyMediaMovesWithoutChangingPathsOrHashQuery()
    {
        string original = CatalogEndpoints.LegacyPublicBaseUrl + "/Store/Catalog/media/banner%20image.png?v=abcd#preview";
        Assert.Equal("https://new.example/Store/Catalog/media/banner%20image.png?v=abcd#preview",
            CatalogEndpoints.ResolvePublicUrl(original, "https://new.example"));
    }

    [Theory]
    [InlineData("https://example.com/Store/Catalog/icon.png")]
    [InlineData("https://pub-166b41912a994dbe86583ba10596d673.r2.dev.evil.example/Store/a.png")]
    [InlineData("https://pub-166b41912a994dbe86583ba10596d673.r2.dev:444/Store/a.png")]
    [InlineData("https://user@pub-166b41912a994dbe86583ba10596d673.r2.dev/Store/a.png")]
    [InlineData("https://pub-166b41912a994dbe86583ba10596d673.r2.dev/Other/a.png")]
    [InlineData("media/icon.png")]
    [InlineData(null)]
    public void OnlyLegacyPublicStoreAssetsAreRemapped(string? original)
        => Assert.Equal(original, CatalogEndpoints.ResolvePublicUrl(original, "https://new.example"));
}
