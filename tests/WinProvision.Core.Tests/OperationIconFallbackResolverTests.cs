using WinProvision.Store.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OperationIconFallbackResolverTests
{
    [Theory]
    [InlineData(OperationIconLoadState.Loaded, false)]
    [InlineData(OperationIconLoadState.Loading, true)]
    [InlineData(OperationIconLoadState.Failed, true)]
    [InlineData(OperationIconLoadState.Missing, true)]
    public void FallbackVisibilityTracksPackageImageLoad(OperationIconLoadState state, bool expected) =>
        Assert.Equal(expected, OperationIconFallbackResolver.ShouldShowFallback(state));
}
