using System.Text.Json.Serialization;
using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Models;

public sealed record OfflineCollectionRequest(string Id, string Name, string Source,
    PackageInstallOptions? Options = null, bool IsOffice = false, OfficeInstallOptions? Office = null);
public sealed record OfflineCollectionSkipped(string Id, string Name, string Reason);
public sealed record OfflineCollectionProgress(string Message, double? Percentage = null);
public sealed record OfflineCollectionFile(string Path, string Sha256);
public sealed record OfflineCollectionPackage(string Id, string Name, string Version, string Architecture,
    string Scope, string InstallerType, string InstallerPath, string SilentArguments, string SilentSource,
    string OriginalSha256, bool RequiresElevation, IReadOnlyList<int> SuccessCodes,
    IReadOnlyList<OfflineCollectionFile> Files);
public sealed record OfflineCollectionManifest(int Schema, string Name, DateTimeOffset CreatedAt,
    IReadOnlyList<OfflineCollectionPackage> Packages, IReadOnlyList<OfflineCollectionSkipped> Skipped,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] OfflineOfficeModule? Office = null);
public sealed record OfflineCollectionResult(string FilePath, IReadOnlyList<OfflineCollectionPackage> Packages,
    IReadOnlyList<OfflineCollectionSkipped> Skipped, OfflineOfficeModule? Office = null);

public sealed record OfflineOfficeModule(int Schema, IReadOnlyList<string> ProductIds,
    IReadOnlyList<OfflineCollectionFile> Files, bool IncludesPayload = false);
