using Microsoft.Management.Deployment;
using Microsoft.Win32;
using System.Runtime.InteropServices;
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
                if (!InProcessWinGetFactory.Enabled && !string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase))
                {
                    var installer = package.InstalledVersion.GetApplicableInstaller(WinGetFactoryHelper.CreateInstallOptions());
                    if (installer?.Scope == PackageInstallerScope.System)
                    {
                        dispatched = true;
                        return await RunElevatedComWorkerAsync(ComOperationKind.Uninstall, id, null,
                            _diagnostic, null, token, new InstallAttemptState()).ConfigureAwait(false);
                    }
                    // Unknown scope must not let the external server trigger an unmanaged UAC.
                    if (installer?.Scope != PackageInstallerScope.User) return null;
                }
                var uninstallOptions = WinGetFactoryHelper.CreateUninstallOptions();
                uninstallOptions.PackageUninstallMode = PackageUninstallMode.Silent;
                token.ThrowIfCancellationRequested();
                dispatched = true;
                var result = await manager.UninstallPackageAsync(package, uninstallOptions).AsTask(token).ConfigureAwait(false);
                int extendedError = result.ExtendedErrorCode?.HResult ?? 0;
                int effectiveError = GetComUninstallFailureCode(result.UninstallerErrorCode, extendedError);
                bool installerStarted = WasComUninstallerDispatched(result.Status, extendedError);
                return new WingetExecutionResult { Success = result.Status == UninstallResultStatus.Ok,
                    ExitCode = effectiveError, Output = result.Status.ToString(),
                    InstallerStarted = installerStarted,
                    FailureReason = result.Status == UninstallResultStatus.BlockedByPolicy
                        ? WingetFailureReason.BlockedByPolicy
                        : InstallerExitCodes.Classify(effectiveError) };
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

    internal static bool WasComUninstallerDispatched(UninstallResultStatus status, int extendedErrorCode)
    {
        // The official API maps these statuses before the execution stage. InternalError
        // is deliberately conservative: it can also represent a failure after execution.
        if (status is UninstallResultStatus.InvalidOptions or UninstallResultStatus.CatalogError
            or UninstallResultStatus.ManifestError or UninstallResultStatus.BlockedByPolicy)
            return false;

        // GetUninstallInfo can fail before execution yet maps this HRESULT to UninstallError.
        // ExecuteUninstaller failures have a different, explicitly documented HRESULT.
        if (unchecked((uint)extendedErrorCode) == 0x8A15002F)
            return false;

        return true;
    }

    internal static int GetComUninstallFailureCode(uint uninstallerErrorCode, int extendedErrorCode) =>
        uninstallerErrorCode != 0 ? unchecked((int)uninstallerErrorCode) : extendedErrorCode;
    public async Task<IReadOnlyList<WinGetPackageMatch>> SearchComOnlyAsync(string query, CancellationToken token)
    {
        if (!await EnsureWingetProvisionedAsync(null, token).ConfigureAwait(false) || WinGetFactoryHelper.IsComDisabled)
            throw new InvalidOperationException("API COM indisponível.");
        return await Task.Run(() => SearchApiAsync(query, token), token).ConfigureAwait(false);
    }

    public async Task<List<UpgradablePackage>> ListComOnlyAsync(string? source, CancellationToken token,
        bool includeInstallerMetadata = false)
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
                var entry = new UpgradablePackage { Id = package.Id, Name = package.Name, CurrentVersion = installed.Version, Source = origin };
                if (includeInstallerMetadata)
                {
                    try
                    {
                        entry.InstalledScope = installed.GetMetadata(PackageVersionMetadataField.InstalledScope)?.Trim().ToLowerInvariant() switch
                        {
                            "user" => "user", "machine" or "system" => "machine", _ => null
                        };
                        entry.InstalledInstallerType = installed.GetMetadata(PackageVersionMetadataField.InstallerType)?.Trim().ToLowerInvariant();
                        entry.InstalledArchitecture = ReadInstalledArchitecture(installed, entry.InstalledScope, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception exception) { PackageDiagnostics.Warning(exception, "ReadInstalledUpdateMetadata"); }
                }
                packages.Add(entry);
            }
            WinGetFactoryHelper.ReportComSuccess();
            return packages;
        }, token).ConfigureAwait(false);
    }

    private static string? ReadInstalledArchitecture(PackageVersionInfo installed, string? scope, CancellationToken token)
    {
        var architectures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string productCode in IndexedCollectionReader.Read(installed.ProductCodes, token))
        {
            if (string.IsNullOrWhiteSpace(productCode) || productCode.IndexOfAny(['\\', '/']) >= 0) continue;
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                if (scope == "user" && hive != RegistryHive.CurrentUser || scope == "machine" && hive != RegistryHive.LocalMachine)
                    continue;
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    token.ThrowIfCancellationRequested();
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + productCode);
                    if (key is null) continue;
                    // HKCU can be shared between registry views, so it cannot prove bitness.
                    if (hive == RegistryHive.LocalMachine)
                        architectures.Add(view == RegistryView.Registry32 ? "x86" : RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
                }
            }
        }
        return architectures.Count == 1 ? architectures.Single() : null;
    }
}
