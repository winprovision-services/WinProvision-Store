using System.Text.Json.Serialization;

namespace WinProvision.Providers.WinGet.Cli.Contracts;

public enum WinGetCliExecutionMode { CurrentUser, Elevated }
public enum WinGetCliOperation { Probe, Search, List, Show, UpgradeList, Install, Upgrade, Uninstall, Download, Export, Source }
public enum WinGetCliStatus { Success, Unavailable, UacCancelled, ElevationRejected, InvalidCredentials, HelperError, PackageNotFound, NoUpdates, SourceFailed, HashMismatch, InstallerFailed, Cancelled, OutcomeUnknown, ParseError, UnexpectedError }
public enum WinGetCliExitCodeOrigin { None, WinGet, Infrastructure }
public enum WinGetCliPhase { None, Started, Searching, Downloading, Verifying, Installing, Uninstalling, Finished }

public sealed record WinGetCliInvocation(
    WinGetCliOperation Operation,
    IReadOnlyList<string> Arguments,
    WinGetCliExecutionMode ExecutionMode = WinGetCliExecutionMode.CurrentUser,
    bool IsMutation = false,
    string? WorkingDirectory = null);

public sealed record WinGetCliEvent(WinGetCliPhase Phase, double? Percentage, string Line, bool IsError = false);
public sealed record WinGetCliPackageMatch(string Id, string Name, string Version, string Source, string Match = "");
public sealed record WinGetCliPackageDetails(string? Version, string? Publisher, string? Author, string? Description,
    string? Homepage, string? License, string? LicenseUrl, string? ReleaseDate, string? InstallerType,
    string? InstallerUrl, string? Sha256, string? Dependencies, string? ReleaseNotes, string? ReleaseNotesUrl,
    string[] Tags);
public sealed record WinGetCliUpgradePackage
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string CurrentVersion { get; init; } = string.Empty;
    public string AvailableVersion { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}
public sealed record WinGetCliExportPackage([property: JsonPropertyName("PackageIdentifier")] string? PackageIdentifier,
    [property: JsonPropertyName("Version")] string? Version = null);
public sealed record WinGetCliExportSource([property: JsonPropertyName("Packages")] WinGetCliExportPackage[]? Packages);
public sealed record WinGetCliExportFile([property: JsonPropertyName("Sources")] WinGetCliExportSource[]? Sources);
public sealed record WinGetCliResult(
    WinGetCliStatus Status,
    int? ExitCode,
    WinGetCliExitCodeOrigin ExitCodeOrigin,
    int? HResult,
    bool RebootRequired,
    bool ProcessStarted,
    bool InstallerMayHaveStarted,
    WinGetCliPhase FurthestPhase,
    string RawOutputSummary,
    string? Diagnostic,
    string? TechnicalMessage,
    string StandardOutput,
    string StandardError)
{
    public bool Success => Status == WinGetCliStatus.Success;
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(WinGetCliEvent))]
[JsonSerializable(typeof(WinGetCliResult))]
[JsonSerializable(typeof(WinGetCliInvocation))]
[JsonSerializable(typeof(WinGetCliPackageDetails))]
[JsonSerializable(typeof(WinGetCliUpgradePackage))]
[JsonSerializable(typeof(WinGetCliExportFile))]
public partial class WinGetCliJsonContext : JsonSerializerContext { }
