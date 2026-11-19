using Microsoft.Management.Deployment;
using WinProvision.Providers.WinGet.Com;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinGetComUninstallLookupPlanTests
{
    [Theory]
    [InlineData("winget")]
    [InlineData("msstore")]
    [InlineData(null)]
    public void UninstallLookup_UsesTheInstalledCompositeAndExactPackageId(string? source)
    {
        WinGetComUninstallLookupPlan plan = WinGetComUninstallLookupPlan.Create(source);

        Assert.Equal(source, plan.Source);
        Assert.Equal(CompositeSearchBehavior.LocalCatalogs, plan.SearchBehavior);
        Assert.Equal(PackageMatchField.Id, plan.Field);
        Assert.Equal(PackageFieldMatchOption.EqualsCaseInsensitive, plan.MatchOption);
        Assert.True(plan.RequireInstalledVersion);
    }
}
