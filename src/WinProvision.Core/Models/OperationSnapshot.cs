using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Models;

/// <summary>Immutable inputs required to faithfully replay a package operation.</summary>
public sealed record OperationSnapshot(
    int SchemaVersion,
    string Manager,
    string? Source,
    string PackageId,
    string PackageName,
    OperationKind Kind,
    string? VersionFrom,
    string? VersionTo,
    PackageInstallOptions Options,
    string? InstallLocation = null,
    OfficeInstallRequest? OfficeInstall = null,
    OfficeRemoveRequest? OfficeRemove = null,
    bool? OfficeAutoUpdatesEnabled = null,
    string? IconUrl = null);
