using System.Runtime.Versioning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Reflection.PortableExecutable;
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
            Task<List<InstalledAppDetail>> registryTask = Task.Run(() => GetDesktopAppsFromRegistry(cancellationToken), cancellationToken);
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
    /// Executa em milissegundos sem depender de processos externos ou do WinGet.
    /// </summary>
    public static List<InstalledAppDetail> GetDesktopAppsFromRegistry(CancellationToken cancellationToken = default)
        => ReadRegistryApps(includeSystemComponents: false, cancellationToken);

    private static List<InstalledAppDetail> ReadRegistryApps(bool includeSystemComponents, CancellationToken cancellationToken)
    {
        var apps = new List<InstalledAppDetail>();
        var seenRegistrations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // As fontes principais de programas clássicos no Windows (HKLM e HKCU, 64-bit e 32-bit)
        var registryLocations = GetRegistryLocations();

        foreach (var loc in registryLocations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var baseKey = RegistryKey.OpenBaseKey(loc.Hive, loc.View);
            using var uninstallKey = baseKey.OpenSubKey(loc.Path);

            if (uninstallKey == null) continue;

            foreach (var subKeyName in uninstallKey.GetSubKeyNames())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var appKey = uninstallKey.OpenSubKey(subKeyName);
                if (appKey == null) continue;

                // 1. Filtro: Se for componente de sistema ou atualização do Windows, ignorar
                var systemComponent = appKey.GetValue("SystemComponent") as int?;
                if (systemComponent == 1 && !includeSystemComponents) continue;

                var parentKey = appKey.GetValue("ParentKeyName") as string;
                if (!string.IsNullOrWhiteSpace(parentKey)) continue;

                var releaseType = appKey.GetValue("ReleaseType") as string;
                if (!string.IsNullOrWhiteSpace(releaseType) &&
                    (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase) ||
                     releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)))
                    continue;

                string? displayName = appKey.GetValue("DisplayName") as string;
                string? uninstallString = appKey.GetValue("UninstallString") as string;
                string? quietUninstallString = appKey.GetValue("QuietUninstallString") as string;
                string? publisher = appKey.GetValue("Publisher") as string;
                string version = DetermineRegistryVersion(name => appKey.GetValue(name));

                bool hasUninstall = !string.IsNullOrWhiteSpace(uninstallString)
                    || !string.IsNullOrWhiteSpace(quietUninstallString)
                    || (appKey.GetValue("WindowsInstaller") is int wi && wi == 1);

                // 2. Filtro: Só adicionamos se tiver Nome e tiver meio de Desinstalação (ignora lixo)
                if (!string.IsNullOrWhiteSpace(displayName) && hasUninstall)
                {
                    // HKCU can be shared across views; distinct machine/user registrations must remain distinct.
                    string registryIdentity = $"{loc.Hive}:{(loc.Hive == RegistryHive.CurrentUser ? "shared" : loc.View.ToString())}:{subKeyName}";
                    if (seenRegistrations.Add(registryIdentity))
                    {
                        bool isMsi = appKey.GetValue("WindowsInstaller") is int installerFlag && installerFlag == 1;
                        bool isBundle = appKey.GetValue("BundleUpgradeCode") is not null;
                        apps.Add(new InstalledAppDetail
                        {
                            Id = subKeyName,
                            RegistryIdentity = registryIdentity,
                            DisplayName = displayName.Trim(),
                            Publisher = publisher?.Trim() ?? string.Empty,
                            DisplayVersion = version?.Trim() ?? "",
                            UninstallString = uninstallString?.Trim() ?? string.Empty,
                            QuietUninstallString = quietUninstallString?.Trim() ?? string.Empty,
                            InstallLocation = appKey.GetValue("InstallLocation") as string ?? string.Empty,
                            DisplayIcon = appKey.GetValue("DisplayIcon") as string ?? string.Empty,
                            Scope = loc.Hive == RegistryHive.CurrentUser ? "user" : "machine",
                            IsWindowsInstaller = isMsi,
                            IsBundle = isBundle,
                            IsSystemComponent = systemComponent == 1,
                            // MSI platform is authoritative. A bundle EXE may be x86
                            // while installing x64 payloads, so its icon cannot prove bitness.
                            Architecture = isMsi || isBundle || systemComponent == 1 ? null
                                : ReadExecutableArchitecture(appKey.GetValue("DisplayIcon") as string)
                        });
                    }
                }
            }
        }

        // Retorna a lista ordenada por nome
        return apps.OrderBy(a => a.DisplayName).ToList();
    }

    /// <summary>
    /// Converte o inventário de programas clássicos do Registro para itens utilizáveis na verificação de atualizações.
    /// </summary>
    public static List<UpgradablePackage> GetRegistryInventory(CancellationToken cancellationToken = default)
        => BuildRegistryInventory(ReadRegistryApps(includeSystemComponents: true, cancellationToken));

    internal static List<UpgradablePackage> BuildRegistryInventory(IReadOnlyList<InstalledAppDetail> apps)
    {
        static bool SameRelease(InstalledAppDetail left, InstalledAppDetail right) =>
            left.Scope == right.Scope && left.Publisher.Equals(right.Publisher, StringComparison.OrdinalIgnoreCase)
            && WinProvisionApiService.NormalizeInstalledName(left.DisplayName, left.DisplayVersion)
                == WinProvisionApiService.NormalizeInstalledName(right.DisplayName, right.DisplayVersion)
            && WinProvisionApiService.NumericVersionsEqual(left.DisplayVersion, right.DisplayVersion);
        // Hidden MSI children are inventory evidence only when a visible bundle
        // identifies the same release. Unrelated system components stay excluded.
        var visible = apps.Where(app => !app.IsSystemComponent || app.IsWindowsInstaller
            && apps.Any(bundle => bundle.IsBundle && !bundle.IsSystemComponent && SameRelease(app, bundle))).ToList();
        // Burn bundles also register their child MSI. Keep the MSI registration when
        // exactly one child describes the same installed release and scope.
        return visible.Where(app => !app.IsBundle || visible.Count(other => other.IsWindowsInstaller && SameRelease(other, app)) != 1)
            .Select(ToUpdateInventoryEntry).ToList();
    }

    // Same precedence as WinGet ARPHelper::DetermineVersion; keep unknown versions unknown.
    internal static string DetermineRegistryVersion(Func<string, object?> read)
    {
        if (read("DisplayVersion") is string display && !string.IsNullOrWhiteSpace(display)) return display.Trim();
        foreach (var (majorName, minorName) in new[] { ("VersionMajor", "VersionMinor"), ("MajorVersion", "MinorVersion") })
        {
            uint major = read(majorName) is int majorValue ? unchecked((uint)majorValue) : 0;
            uint minor = read(minorName) is int minorValue ? unchecked((uint)minorValue) : 0;
            if (major != 0 || minor != 0) return $"{major}.{minor}";
        }
        if (read("Version") is int packed && packed != 0)
        {
            uint value = unchecked((uint)packed);
            return $"{value >> 24}.{(value >> 16) & 0xff}.{value & 0xffff}";
        }
        return string.Empty;
    }

    internal static UpgradablePackage ToUpdateInventoryEntry(InstalledAppDetail app)
        => new()
        {
            Id = app.Id,
            Name = app.DisplayName,
            CurrentVersion = app.DisplayVersion,
            Publisher = app.Publisher,
            Source = "winget",
            InstalledScope = app.Scope,
            InstalledArchitecture = app.Architecture ?? (app.IsWindowsInstaller ? WindowsInstallerInventory.ReadArchitecture(app.Id) : null),
            InstalledRegistryId = string.IsNullOrWhiteSpace(app.RegistryIdentity) ? app.Id : app.RegistryIdentity,
            InstalledPublisher = app.Publisher,
            // UninstallString describes removal, not the technology allowed for an
            // upgrade. An EXE uninstaller does not rule out a vendor MSI migration.
            InstalledInstallerType = app.IsWindowsInstaller
                || app.UninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ? "msi" : null
        };

    internal static string? ReadExecutableArchitecture(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        string path = Environment.ExpandEnvironmentVariables(displayIcon.Trim());
        if (path.StartsWith('"'))
        {
            int end = path.IndexOf('"', 1);
            if (end < 0) return null;
            path = path[1..end];
        }
        else
        {
            int comma = path.LastIndexOf(',');
            if (comma >= 0 && int.TryParse(path[(comma + 1)..], out _)) path = path[..comma].Trim();
        }
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var pe = new PEReader(stream);
            return pe.PEHeaders.CoffHeader.Machine switch
            {
                Machine.Amd64 => "x64", Machine.I386 => "x86", Machine.Arm64 => "arm64",
                Machine.ArmThumb2 => "arm", _ => null
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
        {
            CoreLog.Warning(exception, "ReadInstalledExecutableArchitecture");
            return null;
        }
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
    public string Scope { get; set; } = "machine";
    public string? Architecture { get; set; }
    public bool IsWindowsInstaller { get; set; }
    public bool IsBundle { get; set; }
    public bool IsSystemComponent { get; set; }
    public string RegistryIdentity { get; set; } = string.Empty;
}
