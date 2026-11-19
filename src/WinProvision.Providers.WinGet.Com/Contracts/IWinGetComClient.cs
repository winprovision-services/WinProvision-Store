namespace WinProvision.Providers.WinGet.Com.Contracts;

using WinProvision.WinGet.Contracts;

public interface IWinGetComClient
{
    IReadOnlyList<WinGetComOperationDescriptor> Operations { get; }
    Task<WinGetComOperationResult> ProbeActivationAsync(CancellationToken token = default);
    Task<WinGetComOperationResult> ProbeAsync(CancellationToken token = default);
    Task<WinGetComQueryResult<WinGetComPackage[]>> ListInstalledAsync(WinGetComQueryOptions options, CancellationToken token = default);
    Task<WinGetComQueryResult<WinGetComPackageMatch[]>> SearchAsync(string query, WinGetComQueryOptions options, CancellationToken token = default);
    Task<WinGetComQueryResult<WinGetComPackageDetails?>> GetDetailsAsync(string packageId, string? source, CancellationToken token = default);
    Task<WinGetComQueryResult<WinGetComPackage[]>> GetUpdatesAsync(WinGetComQueryOptions options, CancellationToken token = default);
    Task<WinGetComQueryResult<WinGetComInstallerMetadata?>> GetInstallerMetadataAsync(string packageId, string? source,
        WinGetVariantSelectionOptions selection, CancellationToken token = default);
    Task<WinGetComOperationResult> InstallAsync(WinGetComInstallRequest request, IProgress<WinGetComInstallProgress>? progress = null, IProgress<WinGetComEvent>? events = null, CancellationToken token = default);
    Task<WinGetComOperationResult> UpgradeAsync(WinGetComUpgradeRequest request, IProgress<WinGetComInstallProgress>? progress = null, IProgress<WinGetComEvent>? events = null, CancellationToken token = default);
    Task<WinGetComOperationResult> UninstallAsync(WinGetComUninstallRequest request, IProgress<WinGetComUninstallProgress>? progress = null, IProgress<WinGetComEvent>? events = null, CancellationToken token = default);
}
