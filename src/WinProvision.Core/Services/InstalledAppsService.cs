using System.Runtime.Versioning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Core.Services;

/// <summary>
/// Fonte única de verdade sobre quais aplicativos clássicos (EXE/MSI) estão instalados.
/// Ignora aplicativos nativos do Windows (UWP/AppX) varrendo diretamente o Registro.
/// </summary>
[SupportedOSPlatform("windows")]
public class InstalledAppsService
{
    private const string UninstallRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private readonly WingetExecutor? _wingetExecutor;

    // Mudança: Deixamos de usar HashSet<string> para guardar a lista detalhada de apps
    private List<InstalledAppDetail> _installedApps = new();
    private HashSet<string> _wingetInstalledIds = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, HashSet<string>> _wingetIdsByDisplayName = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _installedDisplayNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public event Action? Changed;

    public InstalledAppsService(WingetExecutor? wingetExecutor = null)
    {
        _wingetExecutor = wingetExecutor;
    }

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
            return;

        await RefreshAsync(cancellationToken);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken);
        try
        {
            // O inventário do winget permite comparar PackageIdentifier exato. O export
            // pode omitir um pacote que winget list e o Registro ainda reconhecem;
            // nesses casos o nome registrado complementa o inventário.
            Task<List<InstalledAppDetail>> registryTask = Task.Run(GetDesktopAppsFromRegistry, cancellationToken);
            Task<(bool Succeeded, List<string> PackageIds)>? wingetTask = _wingetExecutor is null
                ? null
                : _wingetExecutor.TryGetInstalledPackageIdsAsync(cancellationToken);
            Task<List<UpgradablePackage>>? wingetListTask = _wingetExecutor?.ListInstalledPackagesAsync(cancellationToken);
            _wingetIdsByDisplayName = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            if (wingetTask is not null)
                await Task.WhenAll(registryTask, ObserveWingetSnapshotAsync(wingetTask, cancellationToken),
                    ObserveWingetListAsync(wingetListTask!, cancellationToken));
            var registryApps = await registryTask;
            var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (wingetTask is not null)
            {
                try
                {
                    var snapshot = await wingetTask;
                    if (snapshot.Succeeded) installedIds.UnionWith(snapshot.PackageIds);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { CoreLog.Warning(exception, "ReadInstalledWingetSnapshot"); }
            }
            if (wingetListTask is not null)
            {
                try
                {
                    var listed = await wingetListTask;
                    foreach (var package in listed)
                        if (!string.IsNullOrWhiteSpace(package.Id)) installedIds.Add(package.Id.Trim());
                    _wingetIdsByDisplayName = listed
                        .Where(package => !string.IsNullOrWhiteSpace(package.Name) && !string.IsNullOrWhiteSpace(package.Id))
                        .GroupBy(package => NormalizeDisplayName(package.Name), StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key,
                            group => group.Select(package => package.Id.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase),
                            StringComparer.OrdinalIgnoreCase);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { CoreLog.Warning(exception, "ReadInstalledWingetList"); }
            }
            _wingetInstalledIds = installedIds;
            _installedApps = registryApps;
            _installedDisplayNames = registryApps
                .Select(app => NormalizeDisplayName(app.DisplayName))
                .Where(name => name.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _loaded = true;
        }
        finally
        {
            _loadLock.Release();
        }

        Changed?.Invoke();
    }

    private static async Task ObserveWingetSnapshotAsync(
        Task<(bool Succeeded, List<string> PackageIds)> task, CancellationToken cancellationToken)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { CoreLog.Warning(exception, "ReadInstalledWingetSnapshot"); }
    }

    private static async Task ObserveWingetListAsync(Task<List<UpgradablePackage>> task, CancellationToken cancellationToken)
    {
        try { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) { CoreLog.Warning(exception, "ReadInstalledWingetList"); }
    }

    /// <summary>
    /// Retorna todos os aplicativos (EXE/MSI) detectados para a tela de Desinstalação.
    /// </summary>
    public IReadOnlyList<InstalledAppDetail> GetAllInstalledApps() => _installedApps.AsReadOnly();

    /// <summary>
    /// Compatibilidade para a Store UI: Verifica se um app está instalado pelo Nome.
    /// </summary>
    public bool IsInstalled(string displayName, string? packageId = null)
    {
        return IsInstalledByIdentity(displayName, packageId, _wingetInstalledIds,
            _wingetIdsByDisplayName, _installedDisplayNames);
    }

    internal static bool IsInstalledByIdentity(string displayName, string? packageId,
        IReadOnlySet<string> installedIds, IReadOnlyDictionary<string, HashSet<string>> installedIdsByDisplayName,
        IReadOnlySet<string> registryDisplayNames)
    {
        if (!string.IsNullOrWhiteSpace(packageId)
            && (installedIds.Contains(packageId.Trim()) || HasKnownPackageIdAlias(packageId, installedIds)))
            return true;
        if (string.IsNullOrWhiteSpace(displayName)) return false;
        string name = NormalizeDisplayName(displayName);
        if (!string.IsNullOrWhiteSpace(packageId)
            && installedIdsByDisplayName.TryGetValue(name, out var sameNameIds)
            && sameNameIds.Count > 0
            && !sameNameIds.Contains(packageId.Trim())
            && !sameNameIds.Any(id => AreKnownPackageIdAliases(packageId, id)))
            return false;
        return registryDisplayNames.Contains(name);
    }

    private static bool HasKnownPackageIdAlias(string packageId, IReadOnlySet<string> installedIds) =>
        installedIds.Any(installedId => AreKnownPackageIdAliases(packageId, installedId));

    private static bool AreKnownPackageIdAliases(string left, string right) =>
        (left.Equals("Devolutions.UniGetUI", StringComparison.OrdinalIgnoreCase)
            && right.Equals("XPFFTQ032PTPHF", StringComparison.OrdinalIgnoreCase))
        || (right.Equals("Devolutions.UniGetUI", StringComparison.OrdinalIgnoreCase)
            && left.Equals("XPFFTQ032PTPHF", StringComparison.OrdinalIgnoreCase));

    private static string NormalizeDisplayName(string displayName) =>
        string.Join(' ', displayName.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static (RegistryHive Hive, RegistryView View, string Path)[] GetRegistryLocations() =>
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, UninstallRegistryPath),
        (RegistryHive.LocalMachine, RegistryView.Registry32, UninstallRegistryPath),
        (RegistryHive.CurrentUser, RegistryView.Registry64, UninstallRegistryPath),
        (RegistryHive.CurrentUser, RegistryView.Registry32, UninstallRegistryPath)
    ];

    // Uma operação concluída invalida a varredura em memória. Releia o Registro
    // antes de notificar as telas para que a Visão Geral não reutilize estado antigo.
    public void MarkInstalled() => _ = RefreshAfterChangeAsync();
    public void MarkUninstalled() => _ = RefreshAfterChangeAsync();

    private async Task RefreshAfterChangeAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "RefreshInstalledApps");
            // A operação de instalação/desinstalação já terminou. Uma falha de
            // atualização não deve causar exceção não observada na interface.
            _loaded = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Motor de varredura profunda no Registro para capturar apenas EXE e MSI.
    /// </summary>
    private List<InstalledAppDetail> GetDesktopAppsFromRegistry()
    {
        var apps = new List<InstalledAppDetail>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // As 3 fontes principais de programas clássicos no Windows
        var registryLocations = GetRegistryLocations();

        foreach (var loc in registryLocations)
        {
            using var baseKey = RegistryKey.OpenBaseKey(loc.Hive, loc.View);
            using var uninstallKey = baseKey.OpenSubKey(loc.Path);

            if (uninstallKey == null) continue;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                using var appKey = uninstallKey.OpenSubKey(subKeyName);
                if (appKey == null) continue;

                // 1. Filtro: Se for um componente de sistema do Windows, ignorar
                var systemComponent = appKey.GetValue("SystemComponent") as int?;
                if (systemComponent == 1) continue;

                string? displayName = appKey.GetValue("DisplayName") as string;
                string? uninstallString = appKey.GetValue("UninstallString") as string;
                string? quietUninstallString = appKey.GetValue("QuietUninstallString") as string;
                string? publisher = appKey.GetValue("Publisher") as string;
                string? version = appKey.GetValue("DisplayVersion") as string;

                // 2. Filtro: Só adicionamos se tiver Nome e tiver um executável de Desinstalação (ignora lixo)
                if (!string.IsNullOrWhiteSpace(displayName) && !string.IsNullOrWhiteSpace(uninstallString))
                {
                    // Evita duplicatas se o mesmo app estiver registrado no HKLM e HKCU
                    string normalizedName = NormalizeDisplayName(displayName);
                    if (seenNames.Add(normalizedName))
                    {
                        apps.Add(new InstalledAppDetail
                        {
                            Id = subKeyName,
                            DisplayName = displayName.Trim(),
                            Publisher = publisher?.Trim() ?? "Desconhecido",
                            DisplayVersion = version?.Trim() ?? "",
                            UninstallString = uninstallString.Trim(),
                            QuietUninstallString = quietUninstallString?.Trim() ?? string.Empty,
                            InstallLocation = appKey.GetValue("InstallLocation") as string ?? string.Empty,
                            DisplayIcon = appKey.GetValue("DisplayIcon") as string ?? string.Empty
                        });
                    }
                }
            }
        }

        // Retorna a lista ordenada por nome
        return apps.OrderBy(a => a.DisplayName).ToList();
    }
}

// Lembre-se de criar essa classe no seu projeto (ex: na pasta Models)
public class InstalledAppDetail
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string DisplayVersion { get; set; } = string.Empty;
    public string UninstallString { get; set; } = string.Empty;
    public string QuietUninstallString { get; set; } = string.Empty;
    public string InstallLocation { get; set; } = string.Empty;
    public string DisplayIcon { get; set; } = string.Empty;
}
