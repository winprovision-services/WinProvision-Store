using WinProvision.Core.Models;
using WinProvision.PackageManagement;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinGetRouteResolverTests
{
    [Theory]
    [InlineData(WinGetEnginePreference.Automatic, WinGetRoute.ComApi)]
    [InlineData(WinGetEnginePreference.ComApi, WinGetRoute.ComApi)]
    [InlineData(WinGetEnginePreference.WingetCli, WinGetRoute.WingetCli)]
    public void ResolvesThePreferredInitialRoute(WinGetEnginePreference preference, WinGetRoute expected)
    {
        foreach (WinGetRouteOperation operation in Enum.GetValues<WinGetRouteOperation>())
        {
            var decision = WinGetRouteResolver.Resolve(operation, preference);
            Assert.Equal(expected, decision.Route);
            Assert.Equal(operation, decision.Operation);
        }
    }

    [Fact]
    public void AutomaticCanFallBackOnlyForPreDispatchComFailures()
    {
        var initial = WinGetRouteResolver.Resolve(WinGetRouteOperation.Update,
            WinGetEnginePreference.Automatic);

        Assert.Equal(WinGetRoute.WingetCli, WinGetRouteResolver.ResolveFallback(initial,
            WinGetFailurePhase.BeforeDispatch, "timeout")?.Route);
        Assert.Null(WinGetRouteResolver.ResolveFallback(initial,
            WinGetFailurePhase.AfterDispatch, "installer-error"));
        Assert.Null(WinGetRouteResolver.ResolveFallback(initial,
            WinGetFailurePhase.Unknown, "uncertain"));
        Assert.Null(WinGetRouteResolver.ResolveFallback(initial,
            WinGetFailurePhase.BeforeDispatch, "empty-result"));
    }

    [Theory]
    [InlineData(WinGetEnginePreference.ComApi)]
    [InlineData(WinGetEnginePreference.WingetCli)]
    public void ExclusivePreferencesNeverFallback(WinGetEnginePreference preference)
    {
        var initial = WinGetRouteResolver.Resolve(WinGetRouteOperation.Install, preference);
        Assert.Null(WinGetRouteResolver.ResolveFallback(initial,
            WinGetFailurePhase.BeforeDispatch, "unavailable"));
    }

    [Fact]
    public void ValidationOverridesAreExplicitAndExclusive()
    {
        Assert.Equal(WinGetRoute.WingetCli, WinGetRouteResolver.Resolve(
            WinGetRouteOperation.Search, WinGetEnginePreference.Automatic, WinGetMode.CliOnly).Route);
        Assert.Null(WinGetRouteResolver.ResolveFallback(WinGetRouteResolver.Resolve(
            WinGetRouteOperation.Search, WinGetEnginePreference.Automatic, WinGetMode.ComOnly),
            WinGetFailurePhase.BeforeDispatch, "unavailable"));
    }
}
