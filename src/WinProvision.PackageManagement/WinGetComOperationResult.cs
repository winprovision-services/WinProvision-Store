using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.WinGet.Contracts;

namespace WinProvision.PackageManagement;

internal enum ComOperationKind { Install, Upgrade, Uninstall }

internal sealed record InstallerPreferences(
    string? Scope,
    string? Architecture,
    bool RequiresElevation,
    bool ElevationProhibited,
    string? InstallerType = null,
    string? ElevationRequirement = null,
    WinGetVariantSelectionOptions? Selection = null);

internal sealed record WinGetComOperationResult(
    WingetExecutionResult Result,
    InstallerPreferences? Preferences = null,
    string? FallbackReason = null);

internal sealed class ComAttemptState
{
    public volatile bool Started;
    public volatile bool InstallerStarted;
    public InstallerPreferences? Preferences;
    public string? FallbackReason;
}


