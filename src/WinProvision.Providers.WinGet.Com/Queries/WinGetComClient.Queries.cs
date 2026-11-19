using Microsoft.Management.Deployment;
using Microsoft.Extensions.Logging;
using System.Runtime.InteropServices;
using WinProvision.WinGet.Contracts;
using WinGetComActivation = WinProvision.Providers.WinGet.Com.Activation.WinGetComActivation;
using WinProvision.Providers.WinGet.Com.Contracts;

namespace WinProvision.Providers.WinGet.Com;

public sealed partial class WinGetComClient
{
    public async Task<WinGetComQueryResult<WinGetComPackageMatch[]>> SearchAsync(string query, WinGetComQueryOptions options, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        try
        {
            var manager = _activation.Create<PackageManager>();
            var rows = new List<WinGetComPackageMatch>();
            foreach (var (kind, name) in Catalogs(options))
            {
                token.ThrowIfCancellationRequested();
                var catalog = await ConnectAsync(manager, kind, token).ConfigureAwait(false);
                foreach (var field in new[] { PackageMatchField.Name, PackageMatchField.Id, PackageMatchField.Moniker })
                {
                    var find = _activation.Create<FindPackagesOptions>();
                    var filter = _activation.Create<PackageMatchFilter>();
                    filter.Field = field; filter.Option = PackageFieldMatchOption.ContainsCaseInsensitive; filter.Value = query;
                    find.Filters.Add(filter);
                    var result = await catalog.FindPackagesAsync(find).AsTask(token).ConfigureAwait(false);
                    if (result.Status != FindPackagesResultStatus.Ok) continue;
                    foreach (var match in Read(result.Matches, token))
                    {
                        token.ThrowIfCancellationRequested();
                        var package = match.CatalogPackage; var version = package.DefaultInstallVersion;
                        rows.Add(new(package.Id, package.Name, version?.Version ?? "", name,
                            version?.Publisher));
                    }
                }
            }
            var unique = rows.GroupBy(x => (x.Id, x.Source), StringTupleComparer.Instance).Select(g => g.First()).ToArray();
            return new(WinGetComStatus.Success, options.Limit is > 0 ? unique.Take(options.Limit.Value).ToArray() : unique);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (WinGetComActivation.TryAdvanceExternalStrategy(ex))
        { return await SearchAsync(query, options, token).ConfigureAwait(false); }
        catch (Exception ex) { return QueryFailure<WinGetComPackageMatch[]>(ex); }
    }

    public async Task<WinGetComQueryResult<WinGetComPackage[]>> ListInstalledAsync(WinGetComQueryOptions options, CancellationToken token = default)
    {
        try
        {
            var manager = _activation.Create<PackageManager>();
            PackageCatalog packageCatalog;
            if (options.InstalledInformationOnly)
            {
                var local = manager.GetLocalPackageCatalog(LocalPackageCatalog.InstalledPackages);
                local.InstalledPackageInformationOnly = true;
                var localConnection = await local.ConnectAsync().AsTask(token).ConfigureAwait(false);
                if (localConnection.Status != ConnectResultStatus.Ok || localConnection.PackageCatalog is null)
                    return new(Map(0), null, TechnicalMessage: localConnection.Status.ToString());
                packageCatalog = localConnection.PackageCatalog;
            }
            else
            {
                var composite = _activation.Create<CreateCompositePackageCatalogOptions>();
                composite.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;
                foreach (var (kind, _) in Catalogs(options)) composite.Catalogs.Add(manager.GetPredefinedPackageCatalog(kind));
                var connection = await manager.CreateCompositePackageCatalog(composite).ConnectAsync().AsTask(token).ConfigureAwait(false);
                if (connection.Status != ConnectResultStatus.Ok || connection.PackageCatalog is null)
                    return new(Map(connection.ExtendedErrorCode?.HResult ?? 0), null, connection.ExtendedErrorCode?.HResult ?? 0, connection.Status.ToString());
                packageCatalog = connection.PackageCatalog;
            }
            var result = await packageCatalog.FindPackagesAsync(_activation.Create<FindPackagesOptions>()).AsTask(token).ConfigureAwait(false);
                if (result.Status != FindPackagesResultStatus.Ok) return new(WinGetComStatus.CatalogOrSourceFailed, null, 0, result.Status.ToString());
            var packages = new List<WinGetComPackage>();
            foreach (var match in Read(result.Matches, token))
            {
                var package = match.CatalogPackage; var installed = package.InstalledVersion;
                if (installed is null) continue;
                string source = SourceOf(package.DefaultInstallVersion?.PackageCatalog?.Info?.Name ?? installed.PackageCatalog?.Info?.Name);
                if (options.Source is not null && !source.Equals(options.Source, StringComparison.OrdinalIgnoreCase)) continue;
                string? installedScope = installed.GetMetadata(PackageVersionMetadataField.InstalledScope)?.Trim().ToLowerInvariant() switch
                { "user" => "user", "machine" or "system" => "machine", _ => null };
                string? installerType = installed.GetMetadata(PackageVersionMetadataField.InstallerType)?.Trim().ToLowerInvariant();
                var productCodes = Read(installed.ProductCodes, token).Where(code => !string.IsNullOrWhiteSpace(code)).ToArray();
                packages.Add(new(package.Id, package.Name, installed.Version, source,
                    package.DefaultInstallVersion?.Publisher, InstalledVersion: installed.Version,
                    AvailableVersion: package.DefaultInstallVersion?.Version, InstalledScope: installedScope, InstallerType: installerType,
                    IsUpdateAvailable: package.IsUpdateAvailable, LocalId: installed.Id,
                    ProductCodes: productCodes));
            }
            return new(WinGetComStatus.Success, Limit(packages, options), WasLimitExceeded: result.WasLimitExceeded);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (WinGetComActivation.TryAdvanceExternalStrategy(ex))
        { return await ListInstalledAsync(options, token).ConfigureAwait(false); }
        catch (Exception ex) { return QueryFailure<WinGetComPackage[]>(ex); }
    }

    public async Task<WinGetComQueryResult<WinGetComPackage[]>> GetUpdatesAsync(WinGetComQueryOptions options, CancellationToken token = default)
    {
        var installed = await ListInstalledAsync(options, token).ConfigureAwait(false);
        if (installed.Status != WinGetComStatus.Success || installed.Value is null) return new(installed.Status, null, installed.HResult, installed.TechnicalMessage);
        return new(WinGetComStatus.Success, installed.Value.Where(p => p.IsUpdateAvailable && p.AvailableVersion is not null).ToArray(), WasLimitExceeded: installed.WasLimitExceeded);
    }

    public async Task<WinGetComQueryResult<WinGetComPackageDetails?>> GetDetailsAsync(string packageId, string? source, CancellationToken token = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        try
        {
            var manager = _activation.Create<PackageManager>();
            foreach (var (kind, name) in Catalogs(new(source, true)))
            {
                var catalog = await ConnectAsync(manager, kind, token).ConfigureAwait(false);
                var find = _activation.Create<FindPackagesOptions>(); var filter = _activation.Create<PackageMatchFilter>();
                filter.Field = PackageMatchField.Id; filter.Option = PackageFieldMatchOption.Equals; filter.Value = packageId; find.Filters.Add(filter);
                var result = await catalog.FindPackagesAsync(find).AsTask(token).ConfigureAwait(false);
                if (result.Status != FindPackagesResultStatus.Ok) continue;
                var package = Read(result.Matches, token).Select(m => m.CatalogPackage)
                    .FirstOrDefault(p => p.Id.Equals(packageId, StringComparison.OrdinalIgnoreCase));
                if (package is null) continue;
                var version = package.DefaultInstallVersion;
                var versions = new List<string>();
                if (version is not null) versions.Add(version.Version);
                if (package.InstalledVersion is { } installed && !versions.Contains(installed.Version)) versions.Add(installed.Version);
                return new(WinGetComStatus.Success, new(package.Id, package.Name, version?.Publisher, null,
                    null, null, versions, [name]));
            }
            return new(WinGetComStatus.PackageNotFound, null, TechnicalMessage: packageId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (WinGetComActivation.TryAdvanceExternalStrategy(ex))
        { return await GetDetailsAsync(packageId, source, token).ConfigureAwait(false); }
        catch (Exception ex) { return QueryFailure<WinGetComPackageDetails?>(ex); }
    }

    public async Task<WinGetComQueryResult<WinGetComInstallerMetadata?>> GetInstallerMetadataAsync(string packageId, string? source,
        WinGetVariantSelectionOptions selection, CancellationToken token = default)
    {
        var details = await GetDetailsAsync(packageId, source, token).ConfigureAwait(false);
        if (details.Status != WinGetComStatus.Success) return new(details.Status, null, details.HResult, details.TechnicalMessage);
        try
        {
            var manager = _activation.Create<PackageManager>();
            var catalog = await ConnectAsync(manager, string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase)
                ? PredefinedPackageCatalog.MicrosoftStore : PredefinedPackageCatalog.OpenWindowsCatalog, token).ConfigureAwait(false);
            var find = _activation.Create<FindPackagesOptions>(); var filter = _activation.Create<PackageMatchFilter>();
            filter.Field = PackageMatchField.Id; filter.Option = PackageFieldMatchOption.Equals; filter.Value = packageId; find.Filters.Add(filter);
            var result = await catalog.FindPackagesAsync(find).AsTask(token).ConfigureAwait(false);
            var package = Read(result.Matches, token).Select(m => m.CatalogPackage).FirstOrDefault();
            var options = _activation.Create<InstallOptions>(); options.PackageInstallMode = PackageInstallMode.Silent; options.AcceptPackageAgreements = true;
            options.PackageInstallScope = selection.Scope?.Equals("machine", StringComparison.OrdinalIgnoreCase) == true ? PackageInstallScope.System
                : selection.Scope?.Equals("user", StringComparison.OrdinalIgnoreCase) == true ? PackageInstallScope.User : PackageInstallScope.Any;
            var versionId = package is null ? null : SelectVersionId(package, selection.Version);
            if (versionId is null && package is not null && !string.IsNullOrWhiteSpace(selection.Version))
                return new(WinGetComStatus.NoApplicableInstallers);
            if (versionId is not null) options.PackageVersionId = versionId;
            var version = package is null ? null : versionId is null ? package.DefaultInstallVersion : package.GetPackageVersionInfo(versionId);
            var installer = version?.GetApplicableInstaller(options);
            if (installer is null) return new(WinGetComStatus.PackageNotFound);
            string rawScope = installer.Scope.ToString();
            selection.Scope = rawScope.Equals("System", StringComparison.OrdinalIgnoreCase) ? "machine"
                : rawScope.Equals("User", StringComparison.OrdinalIgnoreCase) ? "user"
                : selection.InstalledScope ?? selection.RequestedScope;
            selection.Architecture = installer.Architecture.ToString().ToLowerInvariant();
            selection.InstallerType = installer.InstallerType.ToString().ToLowerInvariant();
            selection.Version = version?.Version;
            selection.MarkFixed("version");
            if (selection.Scope is null) selection.MarkUnfixed("scope");
            else selection.MarkFixed("scope");
            selection.MarkExamined();
            _logger?.LogInformation("WinGetComInstallerScope packageId={PackageId} version={Version} applicableInstallers=[{Installer}] requestedScope={RequestedScope} effectiveScope={EffectiveScope} scopeUndeclared={ScopeUndeclared}",
                packageId, version?.Version, $"scope={rawScope},architecture={installer.Architecture},type={installer.InstallerType}",
                selection.RequestedScope ?? "none", selection.Scope ?? "none", rawScope.Equals("Unknown", StringComparison.OrdinalIgnoreCase));
            return new(WinGetComStatus.Success, new(installer.Scope.ToString(), installer.Architecture.ToString(),
                installer.InstallerType.ToString(), installer.ElevationRequirement.ToString(),
                installer.ElevationRequirement == ElevationRequirement.ElevationProhibited, version?.Version));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (WinGetComActivation.TryAdvanceExternalStrategy(ex))
        { return await GetInstallerMetadataAsync(packageId, source, selection, token).ConfigureAwait(false); }
        catch (Exception ex) { return QueryFailure<WinGetComInstallerMetadata?>(ex); }
    }

    private PackageVersionId? SelectVersionId(CatalogPackage package, string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return null;
        return WinRtCollectionReader.FirstOrDefault(package.AvailableVersions,
            candidate => candidate.Version.Equals(version, StringComparison.OrdinalIgnoreCase),
            "Package.AvailableVersions", _logger);
    }

    private static async Task<PackageCatalog> ConnectAsync(PackageManager manager, PredefinedPackageCatalog kind, CancellationToken token)
    {
        var reference = manager.GetPredefinedPackageCatalog(kind); reference.AcceptSourceAgreements = true;
        var connected = await reference.ConnectAsync().AsTask(token).ConfigureAwait(false);
        if (connected.Status != ConnectResultStatus.Ok || connected.PackageCatalog is null)
            throw new COMException($"Catalog connection failed: {connected.Status}", connected.ExtendedErrorCode?.HResult ?? unchecked((int)0x8A150008));
        WinGetComActivation.ConfirmExternalStrategy();
        return connected.PackageCatalog;
    }

    private static IEnumerable<(PredefinedPackageCatalog Kind, string Name)> Catalogs(WinGetComQueryOptions options)
    {
        if (options.Source is null || options.Source.Equals("winget", StringComparison.OrdinalIgnoreCase)) yield return (PredefinedPackageCatalog.OpenWindowsCatalog, "winget");
        if (options.IncludeMicrosoftStore && (options.Source is null || options.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase))) yield return (PredefinedPackageCatalog.MicrosoftStore, "msstore");
    }

    private static string SourceOf(string? value) => value?.Contains("store", StringComparison.OrdinalIgnoreCase) == true ? "msstore" : "winget";
    private static T[] Limit<T>(List<T> values, WinGetComQueryOptions options) => options.Limit is > 0 ? values.Take(options.Limit.Value).ToArray() : values.ToArray();
    private IEnumerable<T> Read<T>(IReadOnlyList<T> values, CancellationToken token)
        => WinRtCollectionReader.Enumerate(values, values.GetType().FullName ?? typeof(T).Name,
            _logger, token);
    private sealed class StringTupleComparer : IEqualityComparer<(string Id, string Source)>
    {
        public static readonly StringTupleComparer Instance = new();
        public bool Equals((string Id, string Source) x, (string Id, string Source) y) => x.Id.Equals(y.Id, StringComparison.OrdinalIgnoreCase) && x.Source.Equals(y.Source, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Id, string Source) value) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Id), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Source));
    }
}
