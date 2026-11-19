using WinProvision.Core.Models;

namespace WinProvision.PackageManagement;

public enum WinGetRouteOperation
{
    Search,
    ListInstalled,
    DiscoverUpdates,
    Install,
    Update,
    Uninstall
}

public enum WinGetRoute
{
    ComApi,
    WingetCli
}

public enum WinGetFailurePhase
{
    BeforeDispatch,
    AfterDispatch,
    Unknown
}

public sealed record WinGetRouteDecision(
    WinGetRouteOperation Operation,
    WinGetEnginePreference Preference,
    WinGetRoute Route,
    string Reason,
    bool IsFallback = false);

/// <summary>The only policy point that chooses a WinGet transport route.</summary>
public static class WinGetRouteResolver
{
    public static WinGetRouteDecision Resolve(
        WinGetRouteOperation operation,
        WinGetEnginePreference preference,
        WinGetMode validationOverride = WinGetMode.Auto,
        bool requiresCliOptions = false)
    {
        if (validationOverride == WinGetMode.CliOnly)
            return new(operation, preference, WinGetRoute.WingetCli, "validation-override-cli");
        if (validationOverride == WinGetMode.ComOnly)
            return new(operation, preference, WinGetRoute.ComApi, "validation-override-com");

        if (preference == WinGetEnginePreference.WingetCli)
            return new(operation, preference, WinGetRoute.WingetCli, "preference-winget-cli");
        if (preference == WinGetEnginePreference.ComApi)
            return new(operation, preference, WinGetRoute.ComApi, "preference-com-exclusive");
        if (requiresCliOptions)
            return new(operation, preference, WinGetRoute.WingetCli, "options-require-cli");
        return new(operation, preference, WinGetRoute.ComApi, "automatic-prefers-com");
    }

    public static WinGetRouteDecision? ResolveFallback(
        WinGetRouteDecision current,
        WinGetFailurePhase failurePhase,
        string reason)
    {
        bool isProvenPreDispatchFailure = reason is "unavailable" or "timeout" or "query-error" or "activation-error"
            || reason.StartsWith("COM-status-", StringComparison.Ordinal);
        if (current.Preference != WinGetEnginePreference.Automatic
            || current.Route != WinGetRoute.ComApi
            || current.IsFallback
            || failurePhase != WinGetFailurePhase.BeforeDispatch
            || current.Reason.StartsWith("validation-override", StringComparison.Ordinal)
            || !isProvenPreDispatchFailure)
            return null;

        return current with
        {
            Route = WinGetRoute.WingetCli,
            Reason = $"com-pre-dispatch-{reason}",
            IsFallback = true
        };
    }
}
