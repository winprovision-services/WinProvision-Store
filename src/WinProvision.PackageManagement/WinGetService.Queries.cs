using Microsoft.Management.Deployment;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

public sealed partial class WinGetService
{
    public async Task<WingetExecutionResult?> UninstallComOnlyAsync(string id, string? source, CancellationToken token)
    {
        if (!await EnsureWingetProvisionedAsync(null, token).ConfigureAwait(false) || WinGetFactoryHelper.IsComDisabled)
            return null;
        return await Task.Run(async () =>
        {
            bool dispatched = false;
            try
            {
                var manager = WinGetFactoryHelper.CreateResilientPackageManager();
                var reference = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages);
                var connection = await reference.ConnectAsync().AsTask(token).ConfigureAwait(false);
                if (connection.Status != ConnectResultStatus.Ok || connection.PackageCatalog is null) return null;
                var options = WinGetFactoryHelper.CreateFindPackagesOptions();
                var filter = WinGetFactoryHelper.CreatePackageMatchFilter();
                filter.Field = PackageMatchField.Id;
                filter.Option = PackageFieldMatchOption.EqualsCaseInsensitive;
                filter.Value = id;
                options.Filters.Add(filter);
                var found = await connection.PackageCatalog.FindPackagesAsync(options).AsTask(token).ConfigureAwait(false);
                if (found.Status != FindPackagesResultStatus.Ok) return null;
                var matches = IndexedCollectionReader.Read(found.Matches, token)
                    .Select(m => m.CatalogPackage).Where(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase) && p.InstalledVersion is not null).ToList();
                if (matches.Count != 1) return null;
                var package = matches[0];
                string origin = package.InstalledVersion.PackageCatalog?.Info?.Name?.ToLowerInvariant() ?? "";
                if (source is not null && !origin.Equals(source, StringComparison.OrdinalIgnoreCase)) return null;
                var uninstallOptions = WinGetFactoryHelper.CreateUninstallOptions();
                uninstallOptions.PackageUninstallMode = PackageUninstallMode.Silent;
                token.ThrowIfCancellationRequested();
                dispatched = true;
                var result = await manager.UninstallPackageAsync(package, uninstallOptions).AsTask(token).ConfigureAwait(false);
                return new WingetExecutionResult { Success = result.Status == UninstallResultStatus.Ok,
                    ExitCode = unchecked((int)result.UninstallerErrorCode), Output = result.Status.ToString(), InstallerStarted = true };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                if (!dispatched) return null;
                return new WingetExecutionResult { Success = false, ExitCode = exception.HResult,
                    Output = exception.Message, InstallerStarted = true };
            }
        }, token).ConfigureAwait(false);
    }
    public async Task<IReadOnlyList<WinGetPackageMatch>> SearchComOnlyAsync(string query, CancellationToken token)
    {
        if (!await EnsureWingetProvisionedAsync(null, token).ConfigureAwait(false) || WinGetFactoryHelper.IsComDisabled)
            throw new InvalidOperationException("API COM indisponível.");
        return await Task.Run(() => SearchApiAsync(query, token), token).ConfigureAwait(false);
    }

    public async Task<List<UpgradablePackage>> ListComOnlyAsync(string? source, CancellationToken token)
    {
        if (!await EnsureWingetProvisionedAsync(null, token).ConfigureAwait(false) || WinGetFactoryHelper.IsComDisabled)
            throw new InvalidOperationException("API COM indisponível.");
        return await Task.Run(async () =>
        {
            var manager = WinGetFactoryHelper.CreateResilientPackageManager();
            var composite = WinGetFactoryHelper.CreateCompositePackageCatalogOptions();
            composite.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;
            foreach (var kind in new[] { PredefinedPackageCatalog.OpenWindowsCatalog, PredefinedPackageCatalog.MicrosoftStore })
            {
                var remote = manager.GetPredefinedPackageCatalog(kind);
                remote.AcceptSourceAgreements = true;
                composite.Catalogs.Add(remote);
            }
            var reference = manager.CreateCompositePackageCatalog(composite);
            var connection = await reference.ConnectAsync().AsTask(token).ConfigureAwait(false);
            if (connection.Status != ConnectResultStatus.Ok || connection.PackageCatalog is null)
                throw new InvalidOperationException($"Catálogo instalado indisponível: {connection.Status}.");
            var result = await connection.PackageCatalog.FindPackagesAsync(WinGetFactoryHelper.CreateFindPackagesOptions()).AsTask(token).ConfigureAwait(false);
            if (result.Status != FindPackagesResultStatus.Ok)
                throw new InvalidOperationException($"Consulta COM falhou: {result.Status}.");
            var packages = new List<UpgradablePackage>();
            foreach (var match in IndexedCollectionReader.Read(result.Matches, token))
            {
                var package = match.CatalogPackage;
                if (package.InstalledVersion is not { } installed) continue;
                string origin = (package.DefaultInstallVersion?.PackageCatalog?.Info?.Name
                    ?? installed.PackageCatalog?.Info?.Name)?.ToLowerInvariant() ?? "";
                if (origin.Contains("store", StringComparison.Ordinal)) origin = "msstore";
                else if (origin.Contains("winget", StringComparison.Ordinal)) origin = "winget";
                // Keep uncorrelated local entries out of installable/exported package IDs.
                if (origin is not ("winget" or "msstore") || (source is not null && origin != source)) continue;
                packages.Add(new UpgradablePackage { Id = package.Id, Name = package.Name, CurrentVersion = installed.Version, Source = origin });
            }
            WinGetFactoryHelper.ReportComSuccess();
            return packages;
        }, token).ConfigureAwait(false);
    }
}
