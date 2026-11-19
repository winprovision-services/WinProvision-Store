using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class PackageSearchRankingTests
{
    private static PackageSearchRankingDefinition Options() => new()
    {
        Sources = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        { ["winget"] = .12, ["msstore"] = .12, ["chocolatey"] = 0 },
        ScoreWeight = .45,
        StarsWeight = .25,
        DownloadsWeight = .2,
        StoreRatingWeight = .2,
        CuratedIconBonus = .05,
        MetadataCompletenessWeight = .05,
        SourceMatchAdvantageThreshold = 1,
        DerivedPenalty = .15,
        DuplicatePenalty = .1,
        NoiseSuffixes = new[] { ".install", ".portable", ".commandline", ".app" }
    };

    [Fact]
    public void ExactNameOrIdOutranksContainsWithinSameSource()
    {
        AppEntry exactId = App("Alfa.Editor", "Editor de Alfa", "winget");
        AppEntry exactName = App("package.exact.name", "Alfa Editor", "winget");
        AppEntry contains = WithDescription(App("Tools.Alfa.Helper", "Ferramentas auxiliares", "winget"), "Suite para Alfa Editor");

        AppEntry[] ranked = PackageSearchRanking.Rank(new[] { contains, exactId, exactName }, "alfa.editor", Options()).ToArray();

        Assert.Same(exactName, ranked[0]);
        Assert.Contains(exactId, ranked.Take(2));
        Assert.Same(contains, ranked[2]);
    }

    [Fact]
    public void GenericDerivedAndVariantNamesFollowMainPackage()
    {
        AppEntry main = App("alfa.tools", "Alfa Tools", "winget", score: 70);
        AppEntry installedVariant = App("alfa-tools.install", "Alfa Tools install", "chocolatey");
        AppEntry portableVariant = App("alfa-tools.portable", "Alfa Tools Portable", "chocolatey");

        AppEntry[] ranked = PackageSearchRanking.Rank(new[] { portableVariant, installedVariant, main }, "alfa", Options()).ToArray();

        Assert.Same(main, ranked[0]);
        Assert.Contains(installedVariant, ranked);
        Assert.Contains(portableVariant, ranked);
    }

    [Fact]
    public void BetterMatchTierCanBeatHigherSourceWeightButEqualTierUsesSourceWeight()
    {
        PackageSearchRankingDefinition options = Options();
        AppEntry exactLowerWeight = App("alfa.editor", "Editor genérico", "chocolatey");
        AppEntry containsHigherWeight = WithDescription(App("suite.tools", "Ferramenta genérica", "winget"), "Alfa Editor collection");
        AppEntry equalTierHigherWeight = App("alfa.editor.tool", "Ferramenta principal", "winget");
        AppEntry equalTierLowerWeight = App("alfa.editor.addon", "Ferramenta adicional", "chocolatey");

        AppEntry[] ranked = PackageSearchRanking.Rank(new[] { containsHigherWeight, exactLowerWeight, equalTierHigherWeight, equalTierLowerWeight }, "alfa editor", options).ToArray();

        Assert.Same(exactLowerWeight, ranked[0]);
        Assert.Same(equalTierHigherWeight, ranked[1]);
        Assert.Same(equalTierLowerWeight, ranked[2]);
    }

    [Fact]
    public void DuplicatePackagesAcrossSourcesRemainAvailableAfterPrimary()
    {
        AppEntry primary = App("alfa.tools", "Alfa Tools", "winget", score: 80);
        AppEntry alternative = App("alfa.tools", "ALFA-TOOLS", "chocolatey");

        AppEntry[] ranked = PackageSearchRanking.Rank(new[] { alternative, primary }, "alfa tools", Options()).ToArray();

        Assert.Same(primary, ranked[0]);
        Assert.Equal(2, ranked.Length);
        Assert.Same(alternative, ranked[1]);
    }

    [Fact]
    public void AccentsCaseAndSeparatorsNormalizeAndOrderingIsDeterministic()
    {
        AppEntry accented = App("café.outils", "Café-Outils", "winget");
        AppEntry plain = App("cafe_outils", "CAFE_OUTILS", "winget");

        AppEntry[] first = PackageSearchRanking.Rank(new[] { plain, accented }, "cafe outils", Options()).ToArray();
        AppEntry[] second = PackageSearchRanking.Rank(new[] { plain, accented }, "cafe outils", Options()).ToArray();

        Assert.Equal(first.Select(app => app.Id), second.Select(app => app.Id));
        Assert.Equal(0, PackageSearchRanking.MatchTier(accented, "cafe outils"));
        Assert.Equal(0, PackageSearchRanking.MatchTier(plain, "cafe outils"));
    }

    [Fact]
    public void UpdatingSourceWeightChangesOrderWithoutRebuildingRankingService()
    {
        PackageSearchRankingDefinition options = Options();
        AppEntry catalog = App("alfa", "Alfa catalog", "winget");
        AppEntry community = App("alfa", "Alfa community", "chocolatey");

        Assert.Same(catalog, PackageSearchRanking.Rank(new[] { community, catalog }, "alfa", options)[0]);
        options.Sources["chocolatey"] = .3;
        Assert.Same(community, PackageSearchRanking.Rank(new[] { community, catalog }, "alfa", options)[0]);
    }

    [Fact]
    public void ConfiguredMatchAdvantageThresholdControlsWhenSourceWeightMayOverride()
    {
        PackageSearchRankingDefinition options = Options();
        AppEntry exactLowerWeight = App("alfa.editor", "Editor genérico", "chocolatey");
        AppEntry prefixHigherWeight = App("catalog.editor", "Alfa Editor Suite", "winget");

        Assert.Same(exactLowerWeight, PackageSearchRanking.Rank(new[] { prefixHigherWeight, exactLowerWeight }, "alfa editor", options)[0]);
        options.SourceMatchAdvantageThreshold = 2;
        Assert.Same(prefixHigherWeight, PackageSearchRanking.Rank(new[] { prefixHigherWeight, exactLowerWeight }, "alfa editor", options)[0]);
    }

    [Fact]
    public void MissingPopularityMetricsUseNeutralSourceScaleInsteadOfZero()
    {
        PackageSearchRankingDefinition options = Options();
        options.Sources["chocolatey"] = .2;
        AppEntry noMetrics = App("alfa.editor", "Alfa package", "chocolatey");
        noMetrics.HasCatalogScore = false;
        noMetrics.HasGitHubMetrics = false;
        AppEntry measured = App("alfa.editor", "Editor package", "winget", score: 50);

        Assert.Same(noMetrics, PackageSearchRanking.Rank(new[] { measured, noMetrics }, "alfa editor", options)[0]);
    }

    private static AppEntry App(string id, string name, string source, int score = 0, int? stars = null) => new()
    {
        Id = id, Name = name, Publisher = "Alfa Labs", Source = source, Score = score,
        HasCatalogScore = true, GitHubStars = stars, HasGitHubMetrics = stars is not null, Version = "1.0", Description = "Ferramenta genérica"
    };

    private static AppEntry WithDescription(AppEntry app, string description)
    {
        app.Description = description;
        return app;
    }
}
