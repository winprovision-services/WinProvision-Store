using System.Text.Json.Serialization;
using WinProvision.WinGet.Contracts;

namespace WinProvision.Providers.WinGet.Com.Contracts;

public enum WinGetComOperationKind { Read, Mutation }
public enum WinGetComAction { Probe, ActivationProbe, ListInstalled, Search, GetDetails, GetUpdates, GetInstallerMetadata, Install, Upgrade, Uninstall }

public sealed record WinGetComOperationDescriptor(WinGetComAction Action, WinGetComOperationKind Kind);

public enum WinGetComStatus
{
    Success, ComUnavailable, ClassNotRegistered, AccessDenied, ApartmentError,
    ActivationFailed, PackageNotFound, CatalogOrSourceFailed, InstallerFailed,
    BlockedByPolicy, InvalidOptions, NoApplicableInstallers, NoApplicableUpgrade,
    DownloadFailed, ManifestFailed, AgreementsNotAccepted,
    Canceled, OutcomeUnknown, UnexpectedError
}

public enum WinGetComPhase
{
    None, Activated, CatalogConnected, PackageResolved, OptionsPrepared,
    Dispatched, Downloading, Installing, PostInstall, Uninstalling,
    PostUninstall, Finished
}

public enum WinGetComInstallState { Queued, Downloading, Installing, PostInstall, Finished }
public enum WinGetComUninstallState { Queued, Uninstalling, PostUninstall, Finished }

public sealed record WinGetComOperationResult(
    WinGetComStatus Status,
    int HResult = 0,
    uint? InstallerExitCode = null,
    bool? RebootRequired = null,
    string? TechnicalMessage = null,
    WinGetComPhase FurthestPhase = WinGetComPhase.None,
    int? ExtendedHResult = null);

public sealed record WinGetComQueryResult<T>(
    WinGetComStatus Status,
    T? Value = default,
    int HResult = 0,
    string? TechnicalMessage = null,
    bool WasLimitExceeded = false);

public sealed record WinGetComEvent(string Code, WinGetComPhase Phase, int? HResult = null, uint? InstallerCode = null);

public sealed record WinGetComInstallProgress(
    WinGetComInstallState State,
    ulong? BytesDownloaded,
    ulong? BytesRequired,
    double? DownloadProgress,
    double? InstallationProgress);

public sealed record WinGetComUninstallProgress(
    WinGetComUninstallState State,
    double? UninstallationProgress);

public sealed record WinGetComQueryOptions(string? Source = null, bool IncludeMicrosoftStore = true, int? Limit = null,
    bool InstalledInformationOnly = false);

public sealed record WinGetComPackage(
    string Id, string Name, string? Version, string Source,
    string? Publisher = null, string? Description = null, string? InstalledVersion = null,
    string? AvailableVersion = null, string? InstalledScope = null, string? InstallerType = null,
    string? Architecture = null, bool IsUpdateAvailable = false, string? LocalId = null,
    IReadOnlyList<string>? ProductCodes = null);

public sealed record WinGetComPackageMatch(string Id, string Name, string Version, string Source, string? Publisher = null, string? Description = null);

public sealed record WinGetComPackageDetails(
    string Id, string Name, string? Publisher, string? Description, string? Homepage,
    string? License, IReadOnlyList<string> Versions, IReadOnlyList<string> Sources);

public sealed record WinGetComInstallerMetadata(
    string? Scope, string? Architecture, string? InstallerType,
    string? ElevationRequirement, bool ElevationProhibited, string? Version = null);

public sealed record WinGetComInstallRequest(
    string PackageId, string? Source = null, string? InstallLocation = null,
    string? Scope = null, string? Architecture = null, bool Interactive = false,
    WinGetVariantSelectionOptions? Selection = null);

public sealed record WinGetComUpgradeRequest(
    string PackageId, string? Source = null, string? InstalledPackageId = null,
    WinGetVariantSelectionOptions? Selection = null);

public sealed record WinGetComUninstallRequest(string PackageId, string? Source = null, bool Silent = true);

public static class WinGetComOperationCatalog
{
    public static IReadOnlyList<WinGetComOperationDescriptor> All { get; } =
    [
        new(WinGetComAction.Probe, WinGetComOperationKind.Read),
        new(WinGetComAction.ActivationProbe, WinGetComOperationKind.Read),
        new(WinGetComAction.ListInstalled, WinGetComOperationKind.Read),
        new(WinGetComAction.Search, WinGetComOperationKind.Read),
        new(WinGetComAction.GetDetails, WinGetComOperationKind.Read),
        new(WinGetComAction.GetUpdates, WinGetComOperationKind.Read),
        new(WinGetComAction.GetInstallerMetadata, WinGetComOperationKind.Read),
        new(WinGetComAction.Install, WinGetComOperationKind.Mutation),
        new(WinGetComAction.Upgrade, WinGetComOperationKind.Mutation),
        new(WinGetComAction.Uninstall, WinGetComOperationKind.Mutation)
    ];
}

[JsonSerializable(typeof(WinGetComOperationResult))]
[JsonSerializable(typeof(WinGetComEvent))]
[JsonSerializable(typeof(WinGetComInstallProgress))]
[JsonSerializable(typeof(WinGetComUninstallProgress))]
[JsonSerializable(typeof(WinGetComQueryResult<WinGetComPackage[]>))]
[JsonSerializable(typeof(WinGetComQueryResult<WinGetComPackageMatch[]>))]
[JsonSerializable(typeof(WinGetComQueryResult<WinGetComPackageDetails>))]
[JsonSerializable(typeof(WinGetComQueryResult<WinGetComInstallerMetadata>))]
[JsonSerializable(typeof(WinGetComQueryOptions))]
[JsonSerializable(typeof(WinGetComOperationDescriptor))]
[JsonSerializable(typeof(WinGetComInstallerMetadata))]
[JsonSerializable(typeof(WinGetComInstallRequest))]
[JsonSerializable(typeof(WinGetComUpgradeRequest))]
[JsonSerializable(typeof(WinGetComUninstallRequest))]
public partial class WinGetComJsonContext : JsonSerializerContext;
