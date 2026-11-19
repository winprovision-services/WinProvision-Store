using System.Runtime.Versioning;
using Microsoft.Win32;
using WinProvision.Core.Models.Office;

namespace WinProvision.Core.Services.Office;

/// <summary>
/// Lê (somente leitura) o estado de instalação do Office Click-to-Run a partir do
/// registro, para permitir listar "o que já está instalado nesta máquina" na tela de
/// desinstalação — sem precisar reinstalar nada só para descobrir isso.
///
/// Só lê chaves de estado de instalação documentadas publicamente
/// (documentação do instalador/click-to-run-registry-values):
/// ProductReleaseIds, *.ExcludedApps, VersionToReport, Platform e ClientCulture, todas
/// sob HKLM\SOFTWARE\plataforma do sistema\Office\ClickToRun\Configuration. Não lê nem grava nada
/// relacionado a licenciamento/ativação — isso é fora do escopo deste serviço.
/// </summary>
[SupportedOSPlatform("windows")]
public class OfficeInstalledProductsDetector
{
    private const string ConfigurationKeyPath = @"SOFTWARE\Microsoft\Office\ClickToRun\Configuration";
    private static readonly string[] UninstallRoots =
    [
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
        @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
    ];

    /// <summary>Retorna a lista de produtos Click-to-Run instalados, ou vazia se nenhum Office C2R for encontrado.</summary>
    public IReadOnlyList<OfficeInstalledProduct> GetInstalledProducts()
    {
        using var key = Registry.LocalMachine.OpenSubKey(ConfigurationKeyPath);
        if (key == null)
            return [];

        string? productReleaseIds = key.GetValue("ProductReleaseIds") as string;
        if (string.IsNullOrWhiteSpace(productReleaseIds))
            return [];

        string? version = key.GetValue("VersionToReport") as string ?? key.GetValue("ClientVersionToReport") as string;
        string? platform = key.GetValue("Platform") as string;
        string? culture = key.GetValue("ClientCulture") as string ?? key.GetValue("ScenarioCulture") as string;
        string? updateChannel = key.GetValue("UpdateChannel") as string ?? key.GetValue("CDNBaseUrl") as string;
        string? installationPath = key.GetValue("InstallationPath") as string ?? key.GetValue("InstallPath") as string;

        var products = new List<OfficeInstalledProduct>();

        foreach (var rawId in productReleaseIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var excludedRaw = key.GetValue($"{rawId}.ExcludedApps") as string;
            var excludedApps = string.IsNullOrWhiteSpace(excludedRaw)
                ? Array.Empty<string>()
                : excludedRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            products.Add(new OfficeInstalledProduct(
                ProductId: rawId,
                KnownPlan: OfficePlanCatalog.ByProductId(rawId),
                VersionToReport: version,
                Platform: platform,
                ClientCulture: culture,
                ExcludedApps: excludedApps,
                UpdateChannel: updateChannel,
                InstallationPath: installationPath));
        }

        return products;
    }

    /// <summary>Atalho para saber se existe qualquer instalação Click-to-Run na máquina.</summary>
    public bool HasAnyInstallation() => GetInstalledProducts().Count > 0;

    /// <summary>Detecta programas Office por executável real/App Paths, sem inferir presença de *.ExcludedApps.</summary>
    public IReadOnlySet<string> GetInstalledApplicationIds(IReadOnlyList<OfficeInstalledProduct> products) =>
        DetectInstalledApplicationIds(products, File.Exists, GetRegisteredExecutablePath,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            IsRegisteredPackageInstalled);

    /// <summary>Pure entry point for simulated executable and App Paths inventories.</summary>
    public static IReadOnlySet<string> DetectInstalledApplicationIds(
        IReadOnlyList<OfficeInstalledProduct> products,
        Func<string, bool> fileExists,
        Func<string, string?> appPathLookup,
        string? localAppData = null,
        string? programFiles = null,
        string? programFilesX86 = null,
        Func<string, bool>? packageInstalled = null)
    {
        var officeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? root in products.Select(product => product.InstallationPath).Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            string expanded = Environment.ExpandEnvironmentVariables(root!);
            officeDirectories.Add(expanded);
            officeDirectories.Add(Path.Combine(expanded, "root", "Office16"));
            officeDirectories.Add(Path.Combine(expanded, "Office16"));
        }
        foreach (string? programRoot in new[] { programFiles, programFilesX86 }.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            officeDirectories.Add(Path.Combine(programRoot!, "Microsoft Office", "root", "Office16"));
            officeDirectories.Add(Path.Combine(programRoot!, "Microsoft Office", "Office16"));
            officeDirectories.Add(Path.Combine(programRoot!, "Microsoft Office", "Office15"));
        }

        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string appId, IReadOnlyList<string> executableNames) in OfficeAppCatalog.DetectionExecutables)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string executableName in executableNames)
            {
                if (appPathLookup(executableName) is { Length: > 0 } registeredPath)
                    candidates.Add(NormalizeRegisteredPath(registeredPath));
                foreach (string directory in officeDirectories)
                    candidates.Add(Path.Combine(directory, executableName));
            }

            if (OfficeAppCatalog.DetectionPaths.TryGetValue(appId, out IReadOnlyList<string>? detectionPaths))
                foreach (string template in detectionPaths)
                {
                    var replacements = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["{localAppData}"] = localAppData,
                        ["{programFiles}"] = programFiles,
                        ["{programFilesX86}"] = programFilesX86
                    };
                    bool missingRoot = replacements.Any(pair => template.Contains(pair.Key, StringComparison.OrdinalIgnoreCase)
                        && string.IsNullOrWhiteSpace(pair.Value));
                    if (missingRoot) continue;
                    string expanded = replacements.Aggregate(template, (value, pair) =>
                        value.Replace(pair.Key, pair.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase));
                    if (!expanded.Contains('{') && Path.IsPathFullyQualified(expanded)) candidates.Add(Path.GetFullPath(expanded));
                }

            bool packageFound = packageInstalled is not null
                && OfficeAppCatalog.DetectionPackages.TryGetValue(appId, out IReadOnlyList<string>? packageNames)
                && packageNames.Any(packageInstalled);
            if (candidates.Any(fileExists) || packageFound) installed.Add(appId);
        }
        return installed;
    }

    private static bool IsRegisteredPackageInstalled(string packageName)
    {
        const string packagesKey = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(packagesKey);
            return key?.GetSubKeyNames().Any(name => name.StartsWith(packageName + "_", StringComparison.OrdinalIgnoreCase)) == true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            CoreLog.Warning(exception, "Office.PackageDetection");
            return false;
        }
    }

    private static string? GetRegisteredExecutablePath(string executableName)
    {
        string[] appPaths =
        [
            $@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\{executableName}",
            $@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\{executableName}"
        ];
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (string path in appPaths)
        {
            try
            {
                using RegistryKey? key = hive.OpenSubKey(path);
                if (key?.GetValue(null) is string registeredPath && !string.IsNullOrWhiteSpace(registeredPath))
                    return NormalizeRegisteredPath(registeredPath);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
            {
                CoreLog.Warning(exception, "Office.AppPathDetection");
            }
        }
        return null;
    }

    private static string NormalizeRegisteredPath(string path)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        if (expanded.StartsWith('"'))
        {
            int closingQuote = expanded.IndexOf('"', 1);
            if (closingQuote > 1) return expanded[1..closingQuote];
        }
        return expanded;
    }

    /// <summary>Finds legacy MSI Office entries that the page cannot configure with the Click-to-Run ODT flow.</summary>
    public IReadOnlyList<string> GetUnsupportedInstallations()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (string root in UninstallRoots)
        {
            using RegistryKey? key = hive.OpenSubKey(root);
            if (key is null) continue;
            foreach (string childName in key.GetSubKeyNames())
            {
                using RegistryKey? child = key.OpenSubKey(childName);
                string? displayName = child?.GetValue("DisplayName") as string;
                string? uninstall = child?.GetValue("UninstallString") as string;
                int windowsInstaller = child?.GetValue("WindowsInstaller") is int installer ? installer : 0;
                if (IsUnsupportedMsiInstallation(displayName, uninstall, windowsInstaller))
                    result.Add(displayName!.Trim());
            }
        }
        return result.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public IReadOnlyList<string> GetStoreOfficeInstallations()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using RegistryKey? root = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
            if (root is null) return [];
            return root.GetSubKeyNames().Where(name => name.StartsWith("Microsoft.Office.Desktop_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException)
        {
            CoreLog.Warning(exception, "Office.StoreDetection");
            return [];
        }
    }

    internal static bool IsUnsupportedMsiInstallation(string? displayName, string? uninstallString, int windowsInstaller)
    {
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(uninstallString)
            || uninstallString.Contains("OfficeClickToRun.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        bool officeProduct = new[] { "Microsoft Office", "Microsoft 365", "Office LTSC", "Microsoft Visio", "Microsoft Project" }
            .Any(term => displayName.Contains(term, StringComparison.OrdinalIgnoreCase));
        bool msi = windowsInstaller == 1 || uninstallString.Contains("msiexec", StringComparison.OrdinalIgnoreCase);
        return officeProduct && msi;
    }
}
