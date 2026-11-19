using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.WinGet.Contracts;

namespace WinProvision.PackageManagement;

/// <summary>CLI-only execution helpers for operations selected by WinGetRouteResolver.</summary>
internal sealed class WinGetFallbackService
{
    private readonly WingetExecutor _wingetExecutor;
    private readonly WinGetReadinessService _readiness;
    private readonly WinGetUpdateDiscoveryService _discovery;

    internal WinGetFallbackService(WingetExecutor wingetExecutor, WinGetReadinessService readiness,
        WinGetUpdateDiscoveryService discovery)
    {
        _wingetExecutor = wingetExecutor;
        _readiness = readiness;
        _discovery = discovery;
    }

    internal async Task<(WinGetVariantSelectionOptions Selection, InstallerPreferences? Preferences)> PrepareCliVariantSelectionAsync(
        string id, string source, PackageInstallOptions options, CancellationToken token,
        InstallerPreferences? existing = null, string? installedScope = null, string? installedInstallerType = null,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        token.ThrowIfCancellationRequested();
        var selection = existing?.Selection ?? WinGetVariantSelectionBuilder.Build(options, installedScope, installedInstallerType);
        if (installedScope is not null && options.Scope is not null
            && !installedScope.Equals(options.Scope, StringComparison.OrdinalIgnoreCase))
            log?.Invoke($"A atualização preservará o escopo instalado ({installedScope}); o escopo solicitado ({options.Scope}) não será aplicado.");

        selection.Scope ??= installedScope ?? options.Scope;
        selection.Architecture = options.Architecture ?? selection.Architecture;
        selection.InstallerType = options.InstallerType ?? installedInstallerType ?? selection.InstallerType;
        selection.Version = options.Version ?? selection.Version;
        if (selection.Scope is not null) selection.MarkFixed("scope");
        if (selection.Architecture is not null) selection.MarkFixed("architecture");
        if (selection.Version is not null) selection.MarkFixed("version");
        if (selection.InstallerType is not null) selection.MarkFixed("installerType");
        selection.MarkUnfixed("installerId", "hash");

        // In CLI-only mode the route does not query COM for installer metadata.
        _ = id;
        _ = source;
        return (selection, existing);
    }

    internal static WingetExecutionResult AlreadyUpdated(string installedVersion, string targetVersion, Action<string>? log)
    {
        string message = $"O aplicativo já está atualizado (instalado: {installedVersion}; alvo: {targetVersion}). Nenhum instalador foi executado.";
        log?.Invoke(message);
        return new WingetExecutionResult { Success = true, UpdateAlreadySatisfied = true, Output = message };
    }

    internal async Task<WingetExecutionResult> RunCliUpdateFallbackAsync(
        string packageId,
        Action<string>? onLogReceived,
        Action<InstallProgressUpdate>? onProgress,
        CancellationToken cancellationToken,
        string source,
        InstallerPreferences? effectivePreferences,
        PackageInstallOptions? options = null,
        UpgradablePackage? installedPackage = null,
        bool isFallback = false)
    {
        options ??= new PackageInstallOptions();
        onProgress?.Invoke(new InstallProgressUpdate(InstallProgressPhase.Preparing, Method: WingetMethod.WingetExe, IsFallback: isFallback));
        await _readiness.EnsureWingetProvisionedAsync(onLogReceived, cancellationToken, probeCom: false).ConfigureAwait(false);

        string? targetVersion = options.Version ?? _discovery.GetOffer(source, packageId)?.AvailableVersion;
        string? installedScope = installedPackage?.InstalledScope ?? effectivePreferences?.Scope;
        string? installedInstallerType = installedPackage?.InstalledInstallerType ?? effectivePreferences?.InstallerType;
        if (installedScope is not null && options.Scope is not null
            && !installedScope.Equals(options.Scope, StringComparison.OrdinalIgnoreCase))
            onLogReceived?.Invoke($"A atualização preservará o escopo instalado ({installedScope}); o escopo solicitado ({options.Scope}) não será aplicado.");
        options = options with
        {
            Scope = installedScope ?? options.Scope,
            Architecture = options.Architecture ?? installedPackage?.InstalledArchitecture ?? effectivePreferences?.Architecture,
            InstallerType = installedInstallerType ?? options.InstallerType
        };
        var variant = await PrepareCliVariantSelectionAsync(packageId, source,
            options with { Version = targetVersion }, cancellationToken, effectivePreferences,
            installedScope, installedInstallerType, onLogReceived).ConfigureAwait(false);
        effectivePreferences = variant.Preferences;
        var cliResult = await _wingetExecutor.UpdateAppAsync(
            packageId,
            onLogReceived,
            cancellationToken,
            source,
            variant.Selection.Scope,
            options.Architecture ?? effectivePreferences?.Architecture,
            effectivePreferences?.RequiresElevation ?? false,
            effectivePreferences?.ElevationProhibited ?? false,
            interactive: options.Interactive,
            ignoreSecurityHash: options.IgnoreSecurityHash,
            version: targetVersion,
            customArguments: options.UpdateArguments,
            selection: variant.Selection).ConfigureAwait(false);
        if (_discovery.GetLocalIdentifier(source, packageId) is { } localId
            && CanRetryLocalIdentifier(cliResult, packageId, localId, cancellationToken))
        {
            onLogReceived?.Invoke($"O ID do catálogo não localizou a instalação; tentando o identificador local {localId}.");
            return await _wingetExecutor.UpdateAppAsync(localId, onLogReceived, cancellationToken,
                source: string.Empty, scope: variant.Selection.Scope,
                architecture: options.Architecture ?? effectivePreferences?.Architecture,
                requiresElevation: effectivePreferences?.RequiresElevation ?? false,
                elevationProhibited: effectivePreferences?.ElevationProhibited ?? false,
                interactive: options.Interactive,
                ignoreSecurityHash: options.IgnoreSecurityHash,
                version: targetVersion,
                customArguments: options.UpdateArguments,
                selection: variant.Selection).ConfigureAwait(false);
        }
        return cliResult;
    }

    internal static bool CanRetryLocalIdentifier(WingetExecutionResult result, string catalogId,
        string? localId, CancellationToken token) =>
        !result.Success && !result.InstallerStarted && !result.WasElevated && !token.IsCancellationRequested
        && result.FailureReason is (WingetFailureReason.NoPackageFound or WingetFailureReason.PackageNotInCatalog)
        && WinGetUpdateDiscoveryService.IsUsableLocalIdentifier(catalogId, localId);

    internal static InstallerPreferences? ResolveExecutionScope(InstallerPreferences? preferences)
    {
        if (!WindowsIdentityContext.IsRunningAsSystem()) return preferences;
        if (preferences is null)
            return new InstallerPreferences("machine", null, RequiresElevation: false, ElevationProhibited: false);
        var resolved = InstallerElevationResolver.Resolve(preferences.InstallerType, "machine", preferences.ElevationRequirement);
        return preferences with { Scope = "machine", RequiresElevation = preferences.RequiresElevation || resolved.RequiresElevation,
            ElevationProhibited = resolved.Prohibited };
    }
}
