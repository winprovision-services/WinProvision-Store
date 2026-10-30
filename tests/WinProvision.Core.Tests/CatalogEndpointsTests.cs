using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class CatalogEndpointsTests
{
    [Fact]
    public void NetworkEndpointBuildersKeepPathsRelativeToTheirConfiguredHosts()
    {
        Assert.Equal("https://api.github.com/repos/owner/repo/releases/latest",
            NetworkEndpoints.GitHubRepositoryUrl("owner/repo", "releases/latest"));
        Assert.Equal("https://gist.githubusercontent.com",
            NetworkEndpoints.GitHubRawBaseUrl);
        Assert.EndsWith("/v7.0/products", NetworkEndpoints.DisplayCatalogProductsUrl, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyMediaMovesWithoutChangingPathsOrHashQuery()
    {
        string original = "https://" + "pub-166b41912a994dbe86583ba10596d673.r2." + "dev/Store/Catalog/media/banner%20image.png?v=abcd#preview";
        Assert.Equal("https://cdn.winprovisionstore.com.br/Store/Catalog/media/banner%20image.png?v=abcd#preview",
            CatalogEndpoints.ResolvePublicUrl(original));
    }

    public static IEnumerable<object?[]> NonLegacyUrls()
    {
        string legacyHost = "pub-166b41912a994dbe86583ba10596d673.r2." + "dev";
        yield return ["https://example.com/Store/Catalog/icon.png"];
        yield return [$"https://{legacyHost}.evil.example/Store/a.png"];
        yield return [$"https://{legacyHost}:444/Store/a.png"];
        yield return [$"https://user@{legacyHost}/Store/a.png"];
        yield return [$"https://{legacyHost}/Other/a.png"];
        yield return ["media/icon.png"];
        yield return [null];
    }

    [Theory]
    [MemberData(nameof(NonLegacyUrls))]
    public void OnlyLegacyPublicStoreAssetsAreRemapped(string? original)
        => Assert.Equal(original, CatalogEndpoints.ResolvePublicUrl(original, "https://new.example"));
}
