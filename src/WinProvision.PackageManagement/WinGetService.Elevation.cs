using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

public sealed partial class WinGetService
{
    private async Task<InstallerPreferences?> ResolveInstallerElevationAsync(string id, string source, CancellationToken token)
    {
        if (!source.Equals("winget", StringComparison.OrdinalIgnoreCase)) return null;
        // Read-only COM preflight also covers explicit CLI/API selections. No installer is dispatched here.
        try
        {
            if (!WinGetFactoryHelper.IsComDisabled)
            {
                var resolved = await Task.Run(async () =>
                {
                    var manager = WinGetFactoryHelper.CreateResilientPackageManager();
                    var reference = manager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog);
                    reference.AcceptSourceAgreements = true;
                    var connection = await reference.ConnectAsync().AsTask(token).ConfigureAwait(false);
                    if (connection.Status != ConnectResultStatus.Ok || connection.PackageCatalog is null) return null;
                    var options = WinGetFactoryHelper.CreateFindPackagesOptions();
                    var filter = WinGetFactoryHelper.CreatePackageMatchFilter();
                    filter.Field = PackageMatchField.Id; filter.Option = PackageFieldMatchOption.Equals; filter.Value = id;
                    options.Filters.Add(filter);
                    var matches = await connection.PackageCatalog.FindPackagesAsync(options).AsTask(token).ConfigureAwait(false);
                    var package = IndexedCollectionReader.Read(matches.Matches, token).FirstOrDefault()?.CatalogPackage;
                    var installOptions = WinGetFactoryHelper.CreateInstallOptions();
                    installOptions.PackageInstallMode = PackageInstallMode.Silent;
                    installOptions.AcceptPackageAgreements = true;
                    var installer = package?.DefaultInstallVersion?.GetApplicableInstaller(installOptions);
                    return installer is null ? null : new InstallerPreferences(installer.Scope switch
                    {
                        PackageInstallerScope.User => "user", PackageInstallerScope.System => "machine", _ => null
                    }, ReadInstallerArchitecture(installer),
                        installer.ElevationRequirement is ElevationRequirement.ElevationRequired or ElevationRequirement.ElevatesSelf,
                        installer.ElevationRequirement == ElevationRequirement.ElevationProhibited);
                }, token).ConfigureAwait(false);
                if (resolved is not null) return resolved;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { PackageDiagnostics.Write($"ELEVATION preflight COM: {ex.GetType().Name} 0x{ex.HResult:X8}"); }
        try
        {
            var manifest = await _apiService.GetPackageAsync(id, token, source).ConfigureAwait(false);
            var catalogInstaller = manifest is null ? null : WinProvisionApiService.PickInstaller(manifest);
            return catalogInstaller is null ? null : new InstallerPreferences(catalogInstaller.Scope, catalogInstaller.Architecture,
                catalogInstaller.ElevationRequirement is "elevationRequired" or "elevatesSelf",
                catalogInstaller.ElevationRequirement == "elevationProhibited");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            PackageDiagnostics.Write($"ELEVATION preflight catálogo: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return null;
        }
    }
}
