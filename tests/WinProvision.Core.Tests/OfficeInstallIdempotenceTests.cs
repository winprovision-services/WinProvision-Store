using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Office;
using Xunit;

namespace WinProvision.Core.Tests;

public class OfficeInstallIdempotenceTests
{
    [Fact]
    public void SkipsWhenRequestedSuiteIsInstalled()
    {
        var request = Request(OfficePlan.Family);
        Assert.True(OfficeInstallIdempotence.IsAlreadyInstalled(request, (OfficeInstalledProduct[])[Installed(OfficePlan.Family)]));
    }

    [Fact]
    public void AllowsMissingAdditionalProduct()
    {
        var request = Request(OfficePlan.Family) with { AdditionalProducts = (OfficePlan[])[OfficePlan.VisioProSubscription] };
        Assert.False(OfficeInstallIdempotence.IsAlreadyInstalled(request, (OfficeInstalledProduct[])[Installed(OfficePlan.Family)]));
        Assert.True(OfficeInstallIdempotence.IsAlreadyInstalled(request,
            (OfficeInstalledProduct[])[Installed(OfficePlan.Family), Installed(OfficePlan.VisioProSubscription)]));
    }

    [Fact]
    public void AllowsDifferentSuite()
    {
        Assert.False(OfficeInstallIdempotence.IsAlreadyInstalled(Request(OfficePlan.Family),
            (OfficeInstalledProduct[])[Installed(OfficePlan.Microsoft365Business)]));
    }

    private static OfficeInstallRequest Request(OfficePlan plan) => new(plan, 64, "pt-br", []);
    private static OfficeInstalledProduct Installed(OfficePlan plan) =>
        new(plan.ProductId, plan, null, null, null, []);
}
