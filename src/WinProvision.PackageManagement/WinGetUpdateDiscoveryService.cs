using System.Collections.Concurrent;
using System.Diagnostics;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

internal sealed class WinGetUpdateDiscoveryService
{
    private static readonly TimeSpan UpdateQueryTimeout = TimeSpan.FromSeconds(90);
    private readonly WingetExecutor _wingetExecutor;
    private readonly WinGetReadinessService _readiness;
    private readonly AppLogTaskExecutor? _taskExecutor;
    private readonly Func<CancellationToken, Task<List<UpgradablePackage>>> _installedInventory;
    private readonly StuckUpgradeTracker _stuckUpgrades;
    private readonly ConcurrentDictionary<string, UpgradablePackage> _offeredUpgrades = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _localIdentifiers = new(StringComparer.OrdinalIgnoreCase);

    internal WinGetUpdateDiscoveryService(WingetExecutor wingetExecutor, WinGetReadinessService readiness,
        Func<CancellationToken, Task<List<UpgradablePackage>>>? apiInventory, string? stuckUpgradeStatePath,
        AppLogTaskExecutor? taskExecutor = null)
    {
        _wingetExecutor = wingetExecutor;
        _readiness = readiness;
        _taskExecutor = taskExecutor;
        _installedInventory = apiInventory ?? ReadRegistryInventoryAsync;
        _stuckUpgrades = new StuckUpgradeTracker(stuckUpgradeStatePath);
    }

    internal UpgradablePackage? GetOffer(string source, string id) =>
        _offeredUpgrades.GetValueOrDefault(UpgradeKey(source, id));

    internal void RecordOffer(string source, string id, UpgradablePackage offer) =>
        _offeredUpgrades[UpgradeKey(source, id)] = offer;

    internal string? GetLocalIdentifier(string source, string id) =>
        _localIdentifiers.GetValueOrDefault(UpgradeKey(source, id));

    private void RecordLocalIdentifier(string source, string catalogId, string localId)
    {
        if (IsUsableLocalIdentifier(catalogId, localId))
            _localIdentifiers[UpgradeKey(source, catalogId)] = localId;
    }

    internal static bool IsUsableLocalIdentifier(string catalogId, string? localId) =>
        !string.IsNullOrWhiteSpace(localId) && localId.Length <= 256
        && !localId.Equals(catalogId, StringComparison.OrdinalIgnoreCase)
        && !localId.Any(char.IsControl);

    internal void RecordSuccessfulUpgrade(UpgradablePackage package, WingetExecutionResult result)
    {
        if (result.Success) _stuckUpgrades.RecordSuccess(package);
    }

    private List<UpgradablePackage> FilterUpgrades(IEnumerable<UpgradablePackage> packages)
    {
        var unique = DeduplicateUpdates(packages);
        var visible = _stuckUpgrades.FilterOffered(unique);
        foreach (var package in visible)
            _offeredUpgrades[UpgradeKey(package.Source, package.Id)] = package;
        return visible;
    }

    private static string UpgradeKey(string source, string id) => NormalizeUpdateSource(source) + "\u001f" + id;

    private static string NormalizeForMatching(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string normalized = System.Text.RegularExpressions.Regex.Replace(text.Trim(),
            @"\s*\((?:64-bit|32-bit|x64|x86|arm64|arm|x64\s+pt-BR|x86\s+pt-BR|pt-BR|en-US)[^)]*\)", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized,
            @"(?:\s+|-)(?:64-bit|32-bit|x64|x86|arm64|arm)$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized,
            @"\s+(?:version|v)?\s*\d+(?:\.\d+)+.*$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var chars = normalized.Where(character => char.IsLetterOrDigit(character)
            || char.IsWhiteSpace(character) || character is '+' or '#' or '-').ToArray();
        return string.Join(" ", new string(chars).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static bool IsStrictlyNewerNumericVersion(string? current, string? candidate) =>
        Version.TryParse(current, out var installed) && Version.TryParse(candidate, out var available)
        && available > installed;

    /// <summary>Consulta ofertas em exatamente uma rota, com fallback permitido somente pelo resolvedor.</summary>
    internal async Task<List<UpgradablePackage>> GetUpgradablePackagesAsync(
        WinGetEnginePreference preference, Action<string>? onLogReceived = null,
        CancellationToken cancellationToken = default, bool includeMicrosoftStore = true)
    {
        var timer = Stopwatch.StartNew();
        WinGetRouteDecision decision = WinGetRouteResolver.Resolve(
            WinGetRouteOperation.DiscoverUpdates, preference, WinGetFactoryHelper.Mode);
        LogRoute(decision);

        if (decision.Route == WinGetRoute.WingetCli)
        {
            await _readiness.EnsureWingetProvisionedAsync(onLogReceived, cancellationToken, probeCom: false).ConfigureAwait(false);
            return await QueryCliAsync(decision, onLogReceived, cancellationToken, includeMicrosoftStore, timer).ConfigureAwait(false);
        }

        try
        {
            bool ready = await _readiness.EnsureWingetProvisionedAsync(onLogReceived, cancellationToken, probeCom: true).ConfigureAwait(false);
            if (!ready) throw new InvalidOperationException("A API COM do WinGet está indisponível antes da consulta.");
            onLogReceived?.Invoke("Consultando atualizações dos aplicativos instalados pela API COM do WinGet...");
            List<UpgradablePackage> comPackages = _taskExecutor is null
                ? await WinGetComQueryService.GetUpgradablePackagesAsync(RecordLocalIdentifier, cancellationToken, includeMicrosoftStore).ConfigureAwait(false)
                : await _taskExecutor.RunNativeAsync(new AppLogTaskDescriptor(PackageManagerLogCategories.Winget,
                    "WinGet.COM", "ListUpdates", "WinGet"),
                    innerToken => WinGetComQueryService.GetUpgradablePackagesAsync(RecordLocalIdentifier, innerToken, includeMicrosoftStore),
                    cancellationToken).ConfigureAwait(false);
            PackageDiagnostics.Write($"UPDATE DISCOVERY COM success count={comPackages.Count} elapsed={timer.Elapsed}");
            return FilterUpgrades(comPackages);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            if (exception is TimeoutException)
                WinGetFactoryHelper.ForceDisableComForSession(exception.Message);
            else if (WinGetFactoryHelper.IsActivationFailure(exception))
                WinGetFactoryHelper.DisableComForSession(exception);
            PackageDiagnostics.Error(exception, "WinGet.DiscoverUpdatesCOM");

            if (WinGetRouteResolver.ResolveFallback(decision, WinGetFailurePhase.BeforeDispatch,
                exception is TimeoutException ? "timeout" : "query-error") is not { } fallback)
            {
                PackageDiagnostics.Write($"WINGET ROUTE RESULT operation=discover-updates preference={decision.Preference} route={decision.Route} rawCount=null success=false fallback=false phase=before-dispatch error={exception.GetType().Name}");
                throw;
            }
            LogRoute(fallback);
            return await QueryCliAsync(fallback, onLogReceived, cancellationToken, includeMicrosoftStore, timer).ConfigureAwait(false);
        }
    }

    private async Task<List<UpgradablePackage>> QueryCliAsync(WinGetRouteDecision decision,
        Action<string>? onLogReceived, CancellationToken token, bool includeMicrosoftStore, Stopwatch timer)
    {
        onLogReceived?.Invoke("Conferindo atualizações pelo WinGet CLI...");
        try
        {
            var packages = await RunUpdateQueryAsync(
                innerToken => _wingetExecutor.GetUpgradablePackagesAsync(null, innerToken, includeMicrosoftStore),
                UpdateQueryTimeout, token).ConfigureAwait(false);
            PackageDiagnostics.Write($"UPDATE DISCOVERY CLI success count={packages.Count} elapsed={timer.Elapsed} fallback={decision.IsFallback}");
            return FilterUpgrades(packages.Where(package => includeMicrosoftStore || !IsMicrosoftStore(package.Source)));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            PackageDiagnostics.Write($"WINGET ROUTE RESULT operation=discover-updates preference={decision.Preference} route={decision.Route} rawCount=null success=false fallback={decision.IsFallback} error={exception.GetType().Name} message={exception.Message}");
            throw;
        }
    }

    private static void LogRoute(WinGetRouteDecision decision) => PackageDiagnostics.Write(
        $"WINGET ROUTE operation=discover-updates preference={decision.Preference} route={decision.Route} reason={decision.Reason} fallback={decision.IsFallback}");

    private static bool IsMicrosoftStore(string? source) =>
        source?.Contains("store", StringComparison.OrdinalIgnoreCase) == true;

    internal static async Task<List<UpgradablePackage>> RunUpdateQueryAsync(
        Func<CancellationToken, Task<List<UpgradablePackage>>> query,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        CancellationToken queryToken = timeout.Token;
        // Native activation and property getters are synchronous RPCs. Keep them off the
        // dispatcher and bound the wait even if native code ignores cancellation.
        Task<List<UpgradablePackage>> worker = Task.Run(() => query(queryToken), CancellationToken.None);
        try { return await worker.WaitAsync(queryToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            _ = ObserveUpdateQueryAsync(worker);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException($"A fonte de atualizações não respondeu em {deadline.TotalSeconds:0} segundos.");
        }
    }

    private static async Task ObserveUpdateQueryAsync(Task query)
    {
        try { await query.ConfigureAwait(false); }
        catch (OperationCanceledException) { PackageDiagnostics.Write("UPDATE DISCOVERY worker cancelado."); }
        catch (Exception exception) { PackageDiagnostics.Error(exception, "WinGet.DiscoverUpdatesLateCompletion"); }
    }

    internal Task<List<UpgradablePackage>> GetUpdateInventoryAsync(CancellationToken token) => _installedInventory(token);

    private static Task<List<UpgradablePackage>> ReadRegistryInventoryAsync(CancellationToken token) =>
        Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            var inventory = InstalledAppsService.GetRegistryInventory(token);
            token.ThrowIfCancellationRequested();
            return inventory;
        }, token);

    internal static List<UpgradablePackage> MergeUpdateInventory(
        IReadOnlyCollection<UpgradablePackage> identified, IReadOnlyCollection<UpgradablePackage> registry)
    {
        var result = identified.ToList();
        foreach (var app in registry)
        {
            var matches = identified.Where(item => NormalizeUpdateSource(item.Source) == "winget"
                && (item.Id.Equals(app.Id, StringComparison.OrdinalIgnoreCase)
                    || (NormalizeForMatching(item.Name) == NormalizeForMatching(app.Name)
                        && item.CurrentVersion == app.CurrentVersion))).ToArray();
            int localMatches = registry.Count(item =>
                NormalizeForMatching(item.Name) == NormalizeForMatching(app.Name)
                && item.CurrentVersion == app.CurrentVersion);
            if (matches.Length != 1 || localMatches != 1)
            {
                result.Add(app);
                continue;
            }
            var match = matches[0];
            match.InstalledScope ??= app.InstalledScope;
            match.InstalledArchitecture ??= app.InstalledArchitecture;
            match.InstalledInstallerType ??= app.InstalledInstallerType;
        }
        return result;
    }

    private static List<UpgradablePackage> DeduplicateUpdates(IEnumerable<UpgradablePackage> packages)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return packages
            .Where(package => seen.Add($"{NormalizeUpdateSource(package.Source)}\u001f{package.Id}"))
            .ToList();
    }

    internal static List<UpgradablePackage> MergeUpdates(
        IEnumerable<UpgradablePackage> comPackages,
        IEnumerable<UpgradablePackage> cliPackages)
    {
        var combined = DeduplicateUpdates(comPackages).ToDictionary(
            package => UpgradeKey(package.Source, package.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var cli in cliPackages)
        {
            string key = UpgradeKey(cli.Source, cli.Id);
            if (!combined.TryGetValue(key, out var existing))
            {
                combined.Add(key, cli);
                continue;
            }

            // COM may retain an older offer than CLI. Compare only versions of the
            // same installed release; conflicting inventories cannot settle a target.
            if (!WinGetOperationVerificationService.VersionsMatch(existing.CurrentVersion, cli.CurrentVersion)
                || !IsStrictlyNewerNumericVersion(existing.AvailableVersion, cli.AvailableVersion))
                continue;

            if (string.IsNullOrWhiteSpace(cli.Publisher)) cli.Publisher = existing.Publisher;
            if (string.IsNullOrWhiteSpace(cli.Description)) cli.Description = existing.Description;
            if (string.IsNullOrWhiteSpace(cli.IconUrl)) cli.IconUrl = existing.IconUrl;
            cli.InstalledScope ??= existing.InstalledScope;
            cli.InstalledArchitecture ??= existing.InstalledArchitecture;
            cli.InstalledInstallerType ??= existing.InstalledInstallerType;
            cli.InstalledRegistryId ??= existing.InstalledRegistryId;
            combined[key] = cli;
        }
        return combined.Values.ToList();
    }

    internal static string NormalizeUpdateSource(string source)
    {
        if (source.Contains("store", StringComparison.OrdinalIgnoreCase)
            || source.Equals("msstore", StringComparison.OrdinalIgnoreCase))
            return "msstore";
        return string.IsNullOrWhiteSpace(source) ? "winget" : source;
    }
}
