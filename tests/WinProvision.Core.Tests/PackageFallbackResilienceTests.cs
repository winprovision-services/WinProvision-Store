using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class PackageFallbackResilienceTests
{
    [Fact]
    public async Task QueryFallback_DoesNotReturnAnEarlierEmptyResultWhenLaterMethodFails()
    {
        int attempts = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => PackageQueryFallback.RunAsync(
            new Func<CancellationToken, Task<List<string>>>[]
            {
                _ => Task.FromResult(new List<string>()),
                _ =>
                {
                    attempts++;
                    return Task.FromException<List<string>>(new IOException("fonte indisponível"));
                }
            },
            result => result.Count > 0,
            _ => { },
            CancellationToken.None));

        Assert.Equal(1, attempts);
        Assert.IsType<IOException>(exception.InnerException);
    }

    [Fact]
    public void SourceTypeRetry_UsesTwoSupportedTypesAndRequiresExactNameMatch()
    {
        Assert.Equal(new[] { "Microsoft.PreIndexed.Package", "Microsoft.Rest" }, WingetSourceTypeRetry.Types);
        Assert.True(WingetSourceTypeRetry.IsListed(new[] { "My source  https://example.test" }, "My source"));
        Assert.False(WingetSourceTypeRetry.IsListed(new[] { "My source extra  https://example.test" }, "My source"));
    }
}
