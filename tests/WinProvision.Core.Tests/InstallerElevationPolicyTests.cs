using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class InstallerElevationPolicyTests
{
    [Theory]
    [InlineData("winget", "machine", false, false, true)]
    [InlineData("WINGET", "user", true, false, true)]
    [InlineData("winget", null, true, false, true)]
    [InlineData("winget", "user", false, false, false)]
    [InlineData("winget", "machine", true, true, false)]
    [InlineData("msstore", "machine", true, false, false)]
    [InlineData("other", "machine", true, false, false)]
    public void AdministrativeComRequiresAnElevatedLocalHost(string source, string? scope,
        bool required, bool prohibited, bool expected)
        => Assert.Equal(expected, InstallerElevationPolicy.RequiresElevatedComHost(source, scope, required, prohibited));

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
