using Microsoft.Win32;
using System.Runtime.InteropServices;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.WinGet.Contracts;
using WinProvision.Providers.WinGet.Com;
using WinProvision.Providers.WinGet.Com.Contracts;

namespace WinProvision.PackageManagement;

/// <summary>Core-side adapter from provider DTOs to the package-management models.</summary>
internal static class WinGetComQueryService
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan CatalogQueryTimeout = TimeSpan.FromSeconds(20);
    private static readonly SemaphoreSlim NativeQueryGate = new(1, 1);
    private static readonly WinGetComClient Client = new();

    internal static Task<IReadOnlyList<WinGetService.WinGetPackageMatch>> SearchAsync(
        string query, CancellationToken cancellationToken, bool includeMicrosoftStore = true) =>
        RunBoundedAsync(async token =>
        {
            var result = await Client.SearchAsync(query, new(null, includeMicrosoftStore), token).ConfigureAwait(false);
            EnsureSuccess(result.Status, result.HResult, result.TechnicalMessage);
            return (IReadOnlyList<WinGetService.WinGetPackageMatch>)(result.Value ?? [])
                .Select(item => new WinGetService.WinGetPackageMatch(item.Id, item.Name, item.Version, item.Publisher ?? "", item.Source)).ToArray();
        }, SearchTimeout, cancellationToken);

    internal static Task<List<UpgradablePackage>> ListInstalledAsync(
        string? source, CancellationToken token, bool includeInstallerMetadata = false) =>
        RunBoundedAsync(async ct =>
        {
            var result = await Client.ListInstalledAsync(new(source, true), ct).ConfigureAwait(false);
            EnsureSuccess(result.Status, result.HResult, result.TechnicalMessage);
            return (result.Value ?? []).Select(item => ToPackage(item, includeInstallerMetadata)).ToList();
        }, CatalogQueryTimeout, token);

    internal static Task<List<UpgradablePackage>> GetUpgradablePackagesAsync(
        Action<string, string, string> recordLocalIdentifier, CancellationToken token, bool includeMicrosoftStore = true) =>
        RunBoundedAsync(async ct =>
        {
            var result = await Client.GetUpdatesAsync(new(null, includeMicrosoftStore), ct).ConfigureAwait(false);
            EnsureSuccess(result.Status, result.HResult, result.TechnicalMessage);
            if (result.WasLimitExceeded) throw new InvalidOperationException("O catálogo COM limitou a lista de atualizações; a consulta será repetida pela CLI.");
            var packages = new List<UpgradablePackage>();
            foreach (var item in result.Value ?? [])
            {
                ct.ThrowIfCancellationRequested();
                if (item.LocalId is { Length: > 0 } localId) recordLocalIdentifier(item.Source, item.Id, localId);
                packages.Add(ToPackage(item, includeInstallerMetadata: true));
            }
            return packages;
        }, CatalogQueryTimeout, token);

    internal static async Task<InstallerPreferences?> FindInstallerPreferencesAsync(string id,
        WinGetVariantSelectionOptions selection, CancellationToken token)
    {
        var result = await Client.GetInstallerMetadataAsync(id, "winget", selection, token).ConfigureAwait(false);
        if (result.Status == WinGetComStatus.PackageNotFound) return null;
        EnsureSuccess(result.Status, result.HResult, result.TechnicalMessage);
        if (result.Value is not { } metadata) return null;
        string? scope = metadata.Scope?.Equals("System", StringComparison.OrdinalIgnoreCase) == true ? "machine"
            : metadata.Scope?.Equals("User", StringComparison.OrdinalIgnoreCase) == true ? "user" : null;
        var elevation = InstallerElevationResolver.Resolve(metadata.InstallerType, scope, metadata.ElevationRequirement);
        return new(scope, ReadInstallerArchitecture(metadata), elevation.RequiresElevation, elevation.Prohibited,
            metadata.InstallerType, metadata.ElevationRequirement, selection);
    }

    internal static string? ReadInstallerArchitecture(WinGetComInstallerMetadata installerInfo) =>
        installerInfo.Architecture?.Trim().ToLowerInvariant() switch
        { "x86" => "x86", "x64" => "x64", "arm64" => "arm64", "arm" => "arm", _ => null };

    private static UpgradablePackage ToPackage(WinGetComPackage item, bool includeInstallerMetadata)
    {
        var result = new UpgradablePackage
        {
            Id = item.Id, Name = item.Name,
            CurrentVersion = item.InstalledVersion ?? item.Version ?? "",
            AvailableVersion = item.AvailableVersion ?? "", Source = item.Source,
            Publisher = item.Publisher ?? "", Description = item.Description ?? ""
        };
        if (includeInstallerMetadata)
        {
            result.InstalledScope = item.InstalledScope;
            result.InstalledInstallerType = item.InstallerType;
            result.InstalledArchitecture = ReadInstalledArchitecture(item.ProductCodes, item.InstalledScope);
        }
        return result;
    }

    private static string? ReadInstalledArchitecture(IReadOnlyList<string>? productCodes, string? scope)
    {
        if (productCodes is null) return null;
        var architectures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string productCode in productCodes)
        {
            if (string.IsNullOrWhiteSpace(productCode) || productCode.IndexOfAny(['\\', '/']) >= 0) continue;
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                if (scope == "user" && hive != RegistryHive.CurrentUser || scope == "machine" && hive != RegistryHive.LocalMachine) continue;
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    using var root = RegistryKey.OpenBaseKey(hive, view);
                    using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + productCode);
                    if (key is null) continue;
                    if (hive == RegistryHive.LocalMachine)
                        architectures.Add(view == RegistryView.Registry32 ? "x86" : RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant());
                }
            }
        }
        return architectures.Count == 1 ? architectures.Single() : null;
    }

    private static void EnsureSuccess(WinGetComStatus status, int hresult, string? message)
    {
        if (status == WinGetComStatus.Success) return;
        throw new COMException(message ?? $"WinGet COM query failed: {status}", hresult);
    }

    internal static async Task<T> RunBoundedAsync<T>(Func<CancellationToken, Task<T>> query,
        TimeSpan deadline, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(deadline);
        CancellationToken queryToken = timeout.Token;
        bool gateAcquired = false;
        bool workerOwnsGate = false;
        Task<T>? worker = null;
        try
        {
            await NativeQueryGate.WaitAsync(timeout.Token).ConfigureAwait(false);
            gateAcquired = true;
            worker = Task.Run(() => query(queryToken), CancellationToken.None);
            return await worker.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            if (worker is not null)
            {
                workerOwnsGate = true;
                _ = ObserveAndReleaseAsync(worker);
            }
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(worker is null
                ? $"Outra consulta COM ainda está ativa; a fila excedeu {deadline.TotalSeconds:0} segundos."
                : $"A consulta COM não respondeu em {deadline.TotalSeconds:0} segundos.");
        }
        finally
        {
            if (gateAcquired && !workerOwnsGate) NativeQueryGate.Release();
        }
    }

    private static async Task ObserveAndReleaseAsync(Task worker)
    {
        try { await worker.ConfigureAwait(false); }
        catch (OperationCanceledException) { PackageDiagnostics.Write("WinGet COM query worker canceled after timeout."); }
        catch (Exception exception) { PackageDiagnostics.Error(exception, "WinGetComQuery.LateCompletion"); }
        finally { NativeQueryGate.Release(); }
    }
}
