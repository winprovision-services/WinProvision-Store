using WinProvision.Core.Models;

namespace WinProvision.PackageManagement;

/// <summary>Searches only the route selected by the shared WinGet route resolver.</summary>
internal sealed class WinGetSearchService(
    Func<bool, Action<string>?, CancellationToken, Task<bool>> readiness,
    Func<string, bool, CancellationToken, Task<IReadOnlyList<WinGetService.WinGetPackageMatch>>> comSearch,
    Func<string, string?, CancellationToken, Task<IReadOnlyList<WinGetService.WinGetPackageMatch>>> cliSearch,
    Func<WinGetEnginePreference> preferredEngine,
    Func<WinGetMode>? validationOverride = null,
    Action<string>? diagnostic = null)
{
    private readonly Func<WinGetMode> _validationOverride = validationOverride ?? (() => WinGetMode.Auto);
    private readonly Action<string> _diagnostic = diagnostic ?? PackageDiagnostics.Write;

    internal async Task<IReadOnlyList<WinGetService.WinGetPackageMatch>> SearchAsync(
        string query, CancellationToken cancellationToken, bool includeMicrosoftStore = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WinGetRouteDecision decision = WinGetRouteResolver.Resolve(
            WinGetRouteOperation.Search, preferredEngine(), _validationOverride());

        if (decision.Route == WinGetRoute.WingetCli)
        {
            await readiness(false, _diagnostic, cancellationToken).ConfigureAwait(false);
            LogRoute(decision);
            var cliResults = await cliSearch(query, includeMicrosoftStore ? null : "winget", cancellationToken).ConfigureAwait(false);
            LogResult(decision, cliResults.Count);
            return cliResults;
        }

        bool ready;
        try { ready = await readiness(true, _diagnostic, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (WinGetRouteResolver.ResolveFallback(decision, WinGetFailurePhase.BeforeDispatch,
                exception is TimeoutException ? "timeout" : "unavailable") is { } fallback)
            {
                LogRoute(fallback);
                var cliResults = await cliSearch(query, includeMicrosoftStore ? null : "winget", cancellationToken).ConfigureAwait(false);
                LogResult(fallback, cliResults.Count);
                return cliResults;
            }
            LogFailure(decision, exception);
            throw;
        }

        if (!ready)
        {
            if (WinGetRouteResolver.ResolveFallback(decision, WinGetFailurePhase.BeforeDispatch, "unavailable") is { } fallback)
            {
                LogRoute(fallback);
                var cliResults = await cliSearch(query, includeMicrosoftStore ? null : "winget", cancellationToken).ConfigureAwait(false);
                LogResult(fallback, cliResults.Count);
                return cliResults;
            }
            _diagnostic($"WINGET ROUTE RESULT operation=search preference={decision.Preference} route={decision.Route} rawCount=null success=false fallback=false phase=before-dispatch reason=unavailable");
            throw new InvalidOperationException("A API COM do WinGet está indisponível.");
        }

        try
        {
            LogRoute(decision);
            var comResults = await comSearch(query, includeMicrosoftStore, cancellationToken).ConfigureAwait(false);
            LogResult(decision, comResults.Count);
            return comResults;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (exception is TimeoutException)
                WinGetFactoryHelper.ForceDisableComForSession("WinGet COM search timeout");
            else
                WinGetFactoryHelper.DisableComForSession(exception);

            if (WinGetRouteResolver.ResolveFallback(decision, WinGetFailurePhase.BeforeDispatch,
                exception is TimeoutException ? "timeout" : "query-error") is not { } fallback)
            {
                LogFailure(decision, exception);
                throw;
            }

            LogRoute(fallback);
            var cliResults = await cliSearch(query, includeMicrosoftStore ? null : "winget", cancellationToken).ConfigureAwait(false);
            LogResult(fallback, cliResults.Count);
            return cliResults;
        }
    }

    private void LogRoute(WinGetRouteDecision decision) => _diagnostic(
        $"WINGET ROUTE operation=search preference={decision.Preference} route={decision.Route} reason={decision.Reason} fallback={decision.IsFallback}");

    private void LogResult(WinGetRouteDecision decision, int count) => _diagnostic(
        $"WINGET ROUTE RESULT operation=search preference={decision.Preference} route={decision.Route} rawCount={count} success=true fallback={decision.IsFallback}");

    private void LogFailure(WinGetRouteDecision decision, Exception exception) => _diagnostic(
        $"WINGET ROUTE RESULT operation=search preference={decision.Preference} route={decision.Route} rawCount=null success=false fallback=false phase=before-dispatch error={exception.GetType().Name} message={exception.Message}");
}
