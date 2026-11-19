using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Office;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OfficeProductApplicationTests
{
    [Theory]
    [InlineData("O365HomePremRetail", "Access", true)]
    [InlineData("O365HomePremRetail", "Publisher", false)]
    [InlineData("O365HomePremRetail", "Teams", false)]
    [InlineData("O365BusinessRetail", "Access", true)]
    [InlineData("HomeStudent2021Retail", "Outlook", false)]
    [InlineData("HomeBusiness2024Retail", "Outlook", true)]
    [InlineData("ProPlus2024Volume", "Access", true)]
    [InlineData("ProPlus2024Volume", "Publisher", false)]
    [InlineData("Standard2024Volume", "Access", false)]
    [InlineData("ProPlus2021Volume", "Publisher", true)]
    [InlineData("VisioProRetail", "Visio", true)]
    [InlineData("VisioProRetail", "Word", false)]
    [InlineData("ProjectProRetail", "Project", true)]
    [InlineData("ProjectProRetail", "Visio", false)]
    public void ProductCatalogDeclaresApplicableApplications(string productId, string appId, bool expected)
    {
        OfficePlan plan = Assert.IsType<OfficePlan>(OfficePlanCatalog.ByProductId(productId));
        Assert.Equal(expected, plan.IncludedApplicationIds.Contains(appId, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("O365ProPlusRetail")]
    [InlineData("O365BusinessRetail")]
    public void TeamsAvailabilityIsConditionalOnCommercialServicePlan(string productId)
    {
        OfficePlan plan = Assert.IsType<OfficePlan>(OfficePlanCatalog.ByProductId(productId));
        Assert.DoesNotContain("Teams", plan.IncludedApplicationIds);
        Assert.Contains("Teams", plan.ConditionalApplicationIds);
    }

    [Fact]
    public void DetectsOfficeTeamsAndOneDriveByExecutableAndAppPathRatherThanExcludeList()
    {
        const string officeRoot = @"C:\Office";
        const string localAppData = @"C:\Users\Test\AppData\Local";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(officeRoot, "root", "Office16", "MSACCESS.EXE"),
            Path.Combine(localAppData, "Microsoft", "Teams", "current", "Teams.exe"),
            Path.Combine(localAppData, "Microsoft", "OneDrive", "OneDrive.exe"),
            @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE"
        };
        var product = new OfficeInstalledProduct("O365HomePremRetail", null, null, "x64", "pt-br",
            new[] { "Access" }, InstallationPath: officeRoot);

        IReadOnlySet<string> detected = OfficeInstalledProductsDetector.DetectInstalledApplicationIds(
            new[] { product }, files.Contains,
            executable => executable.Equals("WINWORD.EXE", StringComparison.OrdinalIgnoreCase)
                ? @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE" : null,
            localAppData, @"C:\Program Files", @"C:\Program Files (x86)");

        Assert.Contains("Access", detected);
        Assert.Contains("Word", detected);
        Assert.Contains("Teams", detected);
        Assert.Contains("OneDrive", detected);
        Assert.DoesNotContain("Publisher", detected);
    }

    [Fact]
    public void DetectsStandaloneTeamsAndOneDriveWithoutClickToRunProduct()
    {
        const string localAppData = @"C:\Users\Test\AppData\Local";
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(localAppData, "Microsoft", "WindowsApps", "ms-teams.exe"),
            Path.Combine(localAppData, "Microsoft", "OneDrive", "OneDrive.exe")
        };

        IReadOnlySet<string> detected = OfficeInstalledProductsDetector.DetectInstalledApplicationIds(
            [], files.Contains, _ => null, localAppData);

        Assert.Contains("Teams", detected);
        Assert.Contains("OneDrive", detected);
        Assert.DoesNotContain("Word", detected);
    }
}
