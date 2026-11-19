using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Management.Deployment;
using WinProvision.Providers.WinGet.Com.Activation;
using WinProvision.Providers.WinGet.Com.Contracts;
using WinProvision.WinGet.Contracts;

namespace WinProvision.Providers.WinGet.Com;

public sealed partial class WinGetComClient : IWinGetComClient
{
    private static int _inProcEnabled;
    private readonly WinGetComActivation _activation;
    private readonly ILogger? _logger;
    public IReadOnlyList<WinGetComOperationDescriptor> Operations => WinGetComOperationCatalog.All;
    public static bool InProcComEnabled => Volatile.Read(ref _inProcEnabled) != 0;
    public Activation.WinGetComActivationStrategy ActivationStrategy => _activation.Strategy;

    /// <summary>Enables local class activation for a caller that has already established its execution context.</summary>
    public static void EnableInProcCom() => Volatile.Write(ref _inProcEnabled, 1);

    public static void ConfigureExternalActivationStrategy(Activation.WinGetComActivationStrategy? strategy) =>
        Activation.WinGetComActivation.ConfigureExternalStrategy(strategy);

    public WinGetComClient(ILogger? logger = null, bool inProc = false)
    {
        _logger = logger;
        _activation = new WinGetComActivation(logger, inProc || InProcComEnabled);
    }

    public async Task<WinGetComOperationResult> ProbeAsync(CancellationToken token = default)
        => await ProbeAsync(token, includeMicrosoftStore: false).ConfigureAwait(false);

    public async Task<WinGetComOperationResult> ProbeAsync(CancellationToken token, bool includeMicrosoftStore)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var manager = _activation.Create<PackageManager>();
            _ = _activation.Create<FindPackagesOptions>();
            _ = _activation.Create<InstallOptions>();
            _ = _activation.Create<PackageMatchFilter>();
            _ = _activation.Create<CreateCompositePackageCatalogOptions>();
            _ = _activation.Create<UninstallOptions>();
            var catalog = await ConnectAsync(manager, PredefinedPackageCatalog.OpenWindowsCatalog, token).ConfigureAwait(false);
            WinGetComActivation.ConfirmExternalStrategy();
            _ = catalog.Info;
            if (includeMicrosoftStore)
            {
                try { _ = await ConnectAsync(manager, PredefinedPackageCatalog.MicrosoftStore, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger?.LogWarning(ex, "Optional Microsoft Store catalog probe failed"); }
            }
            return new(WinGetComStatus.Success, FurthestPhase: WinGetComPhase.CatalogConnected);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (WinGetComActivation.TryAdvanceExternalStrategy(ex))
        { return await ProbeAsync(token, includeMicrosoftStore).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogWarning(ex, "WinGet COM probe failed"); return OperationFailure(ex, WinGetComPhase.None); }
    }

    public Task<WinGetComOperationResult> ProbeActivationAsync(CancellationToken token = default) => Task.Run(() =>
    {
        try
        {
            token.ThrowIfCancellationRequested();
            _ = _activation.Create<PackageManager>();
            _ = _activation.Create<FindPackagesOptions>();
            _ = _activation.Create<InstallOptions>();
            _ = _activation.Create<PackageMatchFilter>();
            _ = _activation.Create<CreateCompositePackageCatalogOptions>();
            _ = _activation.Create<UninstallOptions>();
            return new WinGetComOperationResult(WinGetComStatus.Success, FurthestPhase: WinGetComPhase.Activated);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger?.LogWarning(ex, "WinGet COM class activation probe failed"); return OperationFailure(ex, WinGetComPhase.None); }
    }, token);

    public async Task<WinGetComOperationResult> InstallAsync(WinGetComInstallRequest request,
        IProgress<WinGetComInstallProgress>? progress = null, IProgress<WinGetComEvent>? events = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PackageId);
        return await InstallOrUpgradeAsync(request.PackageId, request.Source, request.InstallLocation, request.Scope,
            request.Architecture, request.Interactive, upgrade: false, request.Selection, progress, events, token).ConfigureAwait(false);
    }

    public async Task<WinGetComOperationResult> UpgradeAsync(WinGetComUpgradeRequest request,
        IProgress<WinGetComInstallProgress>? progress = null, IProgress<WinGetComEvent>? events = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PackageId);
        return await InstallOrUpgradeAsync(request.PackageId, request.Source, null, null, null, false,
            upgrade: true, request.Selection, progress, events, token).ConfigureAwait(false);
    }

    private async Task<WinGetComOperationResult> InstallOrUpgradeAsync(string id, string? source, string? location,
        string? scope, string? architecture, bool interactive, bool upgrade, WinGetVariantSelectionOptions? selection,
        IProgress<WinGetComInstallProgress>? progress, IProgress<WinGetComEvent>? events, CancellationToken token)
    {
        if (!upgrade && architecture is not null)
            return new(WinGetComStatus.InvalidOptions, unchecked((int)0x8A150002), TechnicalMessage: "The COM API does not accept an explicit architecture selection.");
        bool dispatched = false;
        WinGetComPhase phase = WinGetComPhase.None;
        try
        {
            token.ThrowIfCancellationRequested();
            var manager = _activation.Create<PackageManager>(); phase = WinGetComPhase.Activated;
            var kind = source?.Equals("msstore", StringComparison.OrdinalIgnoreCase) == true
                ? PredefinedPackageCatalog.MicrosoftStore : PredefinedPackageCatalog.OpenWindowsCatalog;
            PackageCatalog catalog;
            CatalogPackage? package;
            if (upgrade)
            {
                var composite = _activation.Create<CreateCompositePackageCatalogOptions>();
                composite.CompositeSearchBehavior = CompositeSearchBehavior.RemotePackagesFromAllCatalogs;
                foreach (var catalogKind in new[] { PredefinedPackageCatalog.OpenWindowsCatalog, PredefinedPackageCatalog.MicrosoftStore })
                { var reference = manager.GetPredefinedPackageCatalog(catalogKind); reference.AcceptSourceAgreements = true; composite.Catalogs.Add(reference); }
                var connection = await manager.CreateCompositePackageCatalog(composite).ConnectAsync().AsTask(token).ConfigureAwait(false);
                if (connection.Status != ConnectResultStatus.Ok || connection.PackageCatalog is null)
                    throw new COMException($"Upgrade catalog connection failed: {connection.Status}", unchecked((int)0x8A150008));
                catalog = connection.PackageCatalog; phase = WinGetComPhase.CatalogConnected;
                package = await FindPackageAsync(catalog, id, token, installed: true).ConfigureAwait(false);
            }
            else
            {
                catalog = await ConnectAsync(manager, kind, token).ConfigureAwait(false); phase = WinGetComPhase.CatalogConnected;
                package = await FindPackageAsync(catalog, id, token).ConfigureAwait(false);
            }
            WinGetComActivation.ConfirmExternalStrategy();
            if (package is null) return new(WinGetComStatus.PackageNotFound, TechnicalMessage: id, FurthestPhase: phase);
            phase = WinGetComPhase.PackageResolved;
            var options = _activation.Create<InstallOptions>();
            options.PackageInstallMode = interactive || kind == PredefinedPackageCatalog.MicrosoftStore ? PackageInstallMode.Interactive : PackageInstallMode.Silent;
            options.AcceptPackageAgreements = true; options.PreferredInstallLocation = location;
            scope = selection?.Scope ?? scope;
            options.PackageInstallScope = ResolvePackageInstallScope(selection, scope);
            if (selection is not null && !string.IsNullOrWhiteSpace(selection.Version))
            {
                var versionId = WinRtCollectionReader.FirstOrDefault(package.AvailableVersions,
                    candidate => candidate.Version.Equals(selection.Version, StringComparison.OrdinalIgnoreCase),
                    "Package.AvailableVersions", _logger);
                if (versionId is null)
                    return new(WinGetComStatus.NoApplicableInstallers, TechnicalMessage: "A versão solicitada não está disponível para o pacote COM.");
                options.PackageVersionId = versionId;
            }
            phase = WinGetComPhase.OptionsPrepared;
            events?.Report(new("operation-dispatched", WinGetComPhase.Dispatched));
            dispatched = true; phase = WinGetComPhase.Dispatched;
            var installProgress = new Progress<InstallProgress>(value =>
            {
                WinGetComPhase current = value.State switch
                {
                    PackageInstallProgressState.Downloading => WinGetComPhase.Downloading,
                    PackageInstallProgressState.Installing => WinGetComPhase.Installing,
                    PackageInstallProgressState.PostInstall => WinGetComPhase.PostInstall,
                    PackageInstallProgressState.Finished => WinGetComPhase.Finished,
                    _ => WinGetComPhase.Dispatched
                };
                progress?.Report(new((WinGetComInstallState)value.State, value.BytesDownloaded, value.BytesRequired,
                    FiniteOrNull(value.DownloadProgress), FiniteOrNull(value.InstallationProgress)));
                events?.Report(new("operation-progress", current));
            });
            Func<Task<InstallResult>> convertOperation;
            try
            {
                var operation = upgrade ? manager.UpgradePackageAsync(package, options) : manager.InstallPackageAsync(package, options);
                convertOperation = () => operation.AsTask(token, installProgress);
            }
            catch (Exception ex)
            {
                LogInstallException(ex, upgrade ? "UpgradePackageAsync" : "InstallPackageAsync");
                throw;
            }

            Task<InstallResult> installTask;
            try { installTask = convertOperation(); }
            catch (Exception ex)
            {
                LogInstallException(ex, "AsTaskConversion");
                throw;
            }

            InstallResult result;
            try { result = await installTask.ConfigureAwait(false); }
            catch (Exception ex)
            {
                LogInstallException(ex, "AwaitOperation");
                throw;
            }
            phase = WinGetComPhase.Finished;
            uint? installerCode = result.Status == InstallResultStatus.InstallError ? result.InstallerErrorCode : null;
            int extended = result.ExtendedErrorCode?.HResult ?? 0;
            var status = result.Status switch
            {
                InstallResultStatus.Ok => WinGetComStatus.Success,
                InstallResultStatus.BlockedByPolicy => WinGetComStatus.BlockedByPolicy,
                InstallResultStatus.InvalidOptions => WinGetComStatus.InvalidOptions,
                InstallResultStatus.NoApplicableInstallers => WinGetComStatus.NoApplicableInstallers,
                InstallResultStatus.NoApplicableUpgrade => WinGetComStatus.NoApplicableUpgrade,
                InstallResultStatus.DownloadError => WinGetComStatus.DownloadFailed,
                InstallResultStatus.CatalogError => WinGetComStatus.CatalogOrSourceFailed,
                InstallResultStatus.ManifestError => WinGetComStatus.ManifestFailed,
                InstallResultStatus.PackageAgreementsNotAccepted => WinGetComStatus.AgreementsNotAccepted,
                _ => WinGetComStatus.InstallerFailed
            };
            return new(status, extended, installerCode, result.RebootRequired, result.Status.ToString(), phase, extended);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (!dispatched && WinGetComActivation.TryAdvanceExternalStrategy(ex))
        { return await InstallOrUpgradeAsync(id, source, location, scope, architecture, interactive, upgrade, selection, progress, events, token).ConfigureAwait(false); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 740 && !dispatched)
        { throw; }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "WinGet COM {Operation} failed at {Phase}", upgrade ? "upgrade" : "install", phase);
            return OperationFailure(ex, phase, dispatched);
        }
    }

    internal static PackageInstallScope ResolvePackageInstallScope(WinGetVariantSelectionOptions? selection, string? scope)
    {
        string? effective = selection?.Scope ?? scope;
        return effective?.Equals("machine", StringComparison.OrdinalIgnoreCase) == true ? PackageInstallScope.System
            : effective?.Equals("user", StringComparison.OrdinalIgnoreCase) == true ? PackageInstallScope.User : PackageInstallScope.Any;
    }

    public async Task<WinGetComOperationResult> UninstallAsync(WinGetComUninstallRequest request,
        IProgress<WinGetComUninstallProgress>? progress = null, IProgress<WinGetComEvent>? events = null, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.PackageId);
        bool dispatched = false; var phase = WinGetComPhase.None;
        try
        {
            token.ThrowIfCancellationRequested();
            var manager = _activation.Create<PackageManager>(); phase = WinGetComPhase.Activated;
            WinGetComUninstallLookupPlan lookup = WinGetComUninstallLookupPlan.Create(request.Source);
            var composite = _activation.Create<CreateCompositePackageCatalogOptions>();
            composite.CompositeSearchBehavior = lookup.SearchBehavior;
            foreach (var (kind, _) in Catalogs(new(lookup.Source, IncludeMicrosoftStore: true)))
                composite.Catalogs.Add(manager.GetPredefinedPackageCatalog(kind));
            var connected = await manager.CreateCompositePackageCatalog(composite).ConnectAsync().AsTask(token).ConfigureAwait(false);
            if (connected.Status != ConnectResultStatus.Ok || connected.PackageCatalog is null)
            {
                _logger?.LogWarning("WinGet COM uninstall lookup failed; packageId={PackageId}; catalog=CompositeLocalCatalogs; status={Status}; hresult=0x{HResult:X8}; phase={Phase}; dispatched={Dispatched}",
                    request.PackageId, connected.Status, connected.ExtendedErrorCode?.HResult ?? 0, phase, dispatched);
                return new(WinGetComStatus.CatalogOrSourceFailed, connected.ExtendedErrorCode?.HResult ?? 0,
                    TechnicalMessage: connected.Status.ToString(), FurthestPhase: phase,
                    ExtendedHResult: connected.ExtendedErrorCode?.HResult ?? 0);
            }
            phase = WinGetComPhase.CatalogConnected;
            WinGetComActivation.ConfirmExternalStrategy();
            _logger?.LogInformation("WinGet COM uninstall lookup; packageId={PackageId}; catalog=CompositeLocalCatalogs; behavior={SearchBehavior}; field={Field}; match={MatchOption}; requireInstalledVersion={RequireInstalledVersion}; source={Source}; phase={Phase}",
                request.PackageId, lookup.SearchBehavior, lookup.Field, lookup.MatchOption, lookup.RequireInstalledVersion,
                request.Source ?? "all", phase);
            var package = await FindPackageAsync(connected.PackageCatalog, request.PackageId, token,
                installed: lookup.RequireInstalledVersion, field: lookup.Field, matchOption: lookup.MatchOption).ConfigureAwait(false);
            if (package is null)
            {
                _logger?.LogWarning("WinGet COM uninstall lookup returned no match; packageId={PackageId}; catalog=CompositeLocalCatalogs; behavior={SearchBehavior}; field={Field}; match={MatchOption}; status={Status}; hresult=0x00000000; message={Message}; phase={Phase}; dispatched={Dispatched}",
                    request.PackageId, lookup.SearchBehavior, lookup.Field, lookup.MatchOption,
                    WinGetComStatus.PackageNotFound, request.PackageId, phase, dispatched);
                return new(WinGetComStatus.PackageNotFound, TechnicalMessage: request.PackageId, FurthestPhase: phase);
            }
            string installedSource = package.InstalledVersion?.PackageCatalog?.Info?.Name ?? string.Empty;
            if (request.Source is not null && !SourceOf(installedSource).Equals(request.Source, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("WinGet COM uninstall source mismatch; packageId={PackageId}; requestedSource={RequestedSource}; installedSource={InstalledSource}; phase={Phase}; dispatched={Dispatched}",
                    request.PackageId, request.Source, installedSource, phase, dispatched);
                return new(WinGetComStatus.PackageNotFound, TechnicalMessage: $"{request.PackageId} is not installed from {request.Source}.", FurthestPhase: phase);
            }
            string? rawInstalledScope = package.InstalledVersion?.GetMetadata(PackageVersionMetadataField.InstalledScope);
            _logger?.LogInformation("WinGet COM uninstall package resolved; packageId={PackageId}; installedVersion={Version}; installedScopeRaw={InstalledScopeRaw}; catalog={Catalog}; phase={Phase}",
                package.Id, package.InstalledVersion?.Version, rawInstalledScope, installedSource, WinGetComPhase.PackageResolved);
            phase = WinGetComPhase.PackageResolved;
            var options = _activation.Create<UninstallOptions>();
            options.PackageUninstallMode = request.Silent ? PackageUninstallMode.Silent : PackageUninstallMode.Interactive;
            dispatched = true; phase = WinGetComPhase.Dispatched;
            events?.Report(new("operation-dispatched", phase));
            var result = await manager.UninstallPackageAsync(package, options).AsTask(token,
                new Progress<UninstallProgress>(value =>
                {
                    var current = value.State switch
                    {
                        PackageUninstallProgressState.Uninstalling => WinGetComPhase.Uninstalling,
                        PackageUninstallProgressState.PostUninstall => WinGetComPhase.PostUninstall,
                        PackageUninstallProgressState.Finished => WinGetComPhase.Finished,
                        _ => WinGetComPhase.Dispatched
                    };
                    progress?.Report(new((WinGetComUninstallState)value.State, FiniteOrNull(value.UninstallationProgress)));
                    events?.Report(new("operation-progress", current));
                })).ConfigureAwait(false);
            phase = WinGetComPhase.Finished;
            int extended = result.ExtendedErrorCode?.HResult ?? 0;
            var status = result.Status switch
            {
                UninstallResultStatus.Ok => WinGetComStatus.Success,
                UninstallResultStatus.BlockedByPolicy => WinGetComStatus.BlockedByPolicy,
                UninstallResultStatus.InvalidOptions => WinGetComStatus.InvalidOptions,
                UninstallResultStatus.CatalogError => WinGetComStatus.CatalogOrSourceFailed,
                UninstallResultStatus.ManifestError => WinGetComStatus.ManifestFailed,
                _ => WinGetComStatus.InstallerFailed
            };
            _logger?.LogInformation("WinGet COM uninstall result; packageId={PackageId}; status={Status}; hresult=0x{HResult:X8}; extended=0x{ExtendedHResult:X8}; uninstallerExit={UninstallerExitCode}; message={Message}; phase={Phase}; dispatched={Dispatched}; reboot={RebootRequired}",
                request.PackageId, status, result.ExtendedErrorCode?.HResult ?? 0, extended,
                result.Status == UninstallResultStatus.UninstallError ? result.UninstallerErrorCode : null,
                result.Status.ToString(), phase, dispatched, result.RebootRequired);
            return new(status,
                extended, result.Status == UninstallResultStatus.UninstallError ? result.UninstallerErrorCode : null,
                result.RebootRequired, result.Status.ToString(), phase, extended);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (!dispatched && WinGetComActivation.TryAdvanceExternalStrategy(ex))
        { return await UninstallAsync(request, progress, events, token).ConfigureAwait(false); }
        catch (Exception ex) { _logger?.LogError(ex, "WinGet COM uninstall failed at {Phase}", phase); return OperationFailure(ex, phase, dispatched); }
    }

    private async Task<CatalogPackage?> FindPackageAsync(PackageCatalog catalog, string id, CancellationToken token,
        bool installed = false, PackageMatchField field = PackageMatchField.Id,
        PackageFieldMatchOption matchOption = PackageFieldMatchOption.EqualsCaseInsensitive)
    {
        var options = _activation.Create<FindPackagesOptions>(); var filter = _activation.Create<PackageMatchFilter>();
        filter.Field = field; filter.Option = matchOption; filter.Value = id; options.Filters.Add(filter);
        var found = await catalog.FindPackagesAsync(options).AsTask(token).ConfigureAwait(false);
        if (found.Status != FindPackagesResultStatus.Ok) throw new COMException($"Package lookup failed: {found.Status}", unchecked((int)0x8A150008));
        return Read(found.Matches, token).Select(m => m.CatalogPackage)
            .FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && (!installed || p.InstalledVersion is not null));
    }

    internal static WinGetComOperationResult OperationFailure(Exception ex, WinGetComPhase phase, bool dispatched = false)
    {
        var status = ex.HResult switch
        {
            unchecked((int)0x80040154) => WinGetComStatus.ClassNotRegistered,
            unchecked((int)0x80070005) => WinGetComStatus.AccessDenied,
            unchecked((int)0x800401F0) => WinGetComStatus.ApartmentError,
            unchecked((int)0x800706BA) or unchecked((int)0x80073D54) => WinGetComStatus.ComUnavailable,
            _ => dispatched ? WinGetComStatus.OutcomeUnknown : WinGetComStatus.UnexpectedError
        };
        return new(status, ex.HResult, TechnicalMessage: ex.Message, FurthestPhase: phase, ExtendedHResult: ex.HResult);
    }

    internal static WinGetComStatus Map(int hr) => hr switch
    {
        unchecked((int)0x80040154) => WinGetComStatus.ClassNotRegistered,
        unchecked((int)0x80070005) => WinGetComStatus.AccessDenied,
        unchecked((int)0x800401F0) => WinGetComStatus.ApartmentError,
        unchecked((int)0x800706BA) or unchecked((int)0x80073D54) => WinGetComStatus.ComUnavailable,
        _ => WinGetComStatus.CatalogOrSourceFailed
    };

    private static WinGetComQueryResult<T> QueryFailure<T>(Exception ex)
    {
        if (ex is OperationCanceledException) throw ex;
        WinGetComStatus status = ex.HResult switch
        {
            unchecked((int)0x80040154) => WinGetComStatus.ClassNotRegistered,
            unchecked((int)0x80070005) => WinGetComStatus.AccessDenied,
            unchecked((int)0x800401F0) => WinGetComStatus.ApartmentError,
            unchecked((int)0x800706BA) or unchecked((int)0x80073D54) => WinGetComStatus.ComUnavailable,
            _ when ex is System.Runtime.InteropServices.COMException => WinGetComStatus.CatalogOrSourceFailed,
            _ => WinGetComStatus.UnexpectedError
        };
        return new(status, default, ex.HResult, ex.Message);
    }

    private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;

    private void LogInstallException(Exception exception, string stage) =>
        _logger?.LogError(exception,
            "WinGet COM install failure at {Stage}; exceptionType={ExceptionType}; hresult=0x{HResult:X8}",
            stage, exception.GetType().FullName, exception.HResult);
}
