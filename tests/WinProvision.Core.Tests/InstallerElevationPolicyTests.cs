using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class InstallerElevationPolicyTests
{
    [Theory]
    [InlineData("machine", false, false, true)]
    [InlineData("SYSTEM", false, false, true)]
    [InlineData(null, true, false, true)]
    [InlineData("user", false, false, false)]
    [InlineData(null, false, false, false)]
    [InlineData("machine", true, true, false)]
    [InlineData("user", true, false, true)]
    public void KnownElevationRequirementsRespectProhibitions(string? scope, bool required, bool prohibited, bool expected)
        => Assert.Equal(expected, InstallerElevationPolicy.RequiresElevation(scope, required, prohibited));
}
