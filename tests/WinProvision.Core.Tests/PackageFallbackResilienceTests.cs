using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class PackageFallbackResilienceTests
{
    [Fact]
    public void SourceTypeRetry_UsesTwoSupportedTypesAndRequiresExactNameMatch()
    {
        Assert.Equal(new[] { "Microsoft.PreIndexed.Package", "Microsoft.Rest" }, WingetSourceTypeRetry.Types);
        Assert.True(WingetSourceTypeRetry.IsListed(new[] { "My source  https://example.test" }, "My source"));
        Assert.False(WingetSourceTypeRetry.IsListed(new[] { "My source extra  https://example.test" }, "My source"));
    }
}
