using System.Text.RegularExpressions;
using System.Xml.Linq;
using Windows.Management.Deployment;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

/// <summary>Resolves installed icons without loading image pixels into the inventory.</summary>
public sealed partial class InstalledIconResolver(IconService icons)
{
    public Task<IReadOnlyList<InstalledPackageInfo>> ResolveAsync(IReadOnlyList<InstalledPackageInfo> packages,
        IReadOnlyList<InstalledAppDetail> registry, CancellationToken token) => Task.Run<IReadOnlyList<InstalledPackageInfo>>(() =>
    {
        var shortcuts = ReadShortcuts(token);
        var appx = ReadAppxAssets(token);
        var result = new List<InstalledPackageInfo>(packages.Count);
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(package.IconUrl) && package.IconUrl != IconService.DefaultIconPackUri)
            { result.Add(package); continue; }
            string? icon = null;
            try
            {
                var entry = registry.FirstOrDefault(app => app.Id.Equals(package.Id, StringComparison.OrdinalIgnoreCase)
                    || app.DisplayName.Trim().Equals(package.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (entry is not null && TryParseDisplayIcon(entry.DisplayIcon, out string path, out int index))
                    icon = ResolveFile(path, index);
                if (icon is null && shortcuts.TryGetValue(package.Name, out string? shortcut))
                    icon = ResolveFile(shortcut, 0);
                if (icon is null && !string.IsNullOrWhiteSpace(entry?.InstallLocation) && Directory.Exists(entry.InstallLocation))
                {
                    foreach (var exe in Directory.EnumerateFiles(entry.InstallLocation, "*.exe").Take(32)
                        .Where(exe => !Regex.IsMatch(Path.GetFileName(exe), "unins|uninstall|update|setup", RegexOptions.IgnoreCase)))
                    {
                        token.ThrowIfCancellationRequested();
                        icon = ResolveFile(exe, 0);
                        if (icon is not null) break;
                    }
                }
                if (icon is null)
                {
                    string packageId = package.Id.StartsWith("MSIX\\", StringComparison.OrdinalIgnoreCase) ? package.Id[5..] : package.Id;
                    string identityName = packageId.Split('_')[0];
                    if (appx.TryGetValue(packageId, out var asset) || appx.TryGetValue(identityName, out asset) || appx.TryGetValue(package.Name, out asset)) icon = asset;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { WinProvisionLog.Warning(exception, "InstalledIcon:" + package.Id); }
            result.Add(package with { IconUrl = icon ?? string.Empty });
        }
        return result;
    }, token);

    private string? ResolveFile(string path, int index)
    {
        if (!File.Exists(path)) return null;
        return Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg"
            ? path : icons.ExtractIconResource(path, index);
    }

    internal static bool TryParseDisplayIcon(string value, out string path, out int index)
    {
        index = 0;
        path = Environment.ExpandEnvironmentVariables(value.Trim());
        var match = Regex.Match(path, @",\s*(-?\d+)\s*$");
        if (match.Success)
        {
            if (!int.TryParse(match.Groups[1].Value, out index)) return false;
            path = path[..match.Index].Trim();
        }
        path = path.Trim('"');
        return Path.IsPathFullyQualified(path) && File.Exists(path);
    }

    private static Dictionary<string, string> ReadShortcuts(CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            string root = Environment.GetFolderPath(folder);
            if (!Directory.Exists(root)) continue;
            try
            {
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var link in Directory.EnumerateFiles(root, "*.lnk", options).Take(2000))
                {
                    token.ThrowIfCancellationRequested();
                    try { result.TryAdd(Path.GetFileNameWithoutExtension(link), IconService.ReadShortcutTarget(link)); }
                    catch (Exception exception) { WinProvisionLog.Warning(exception, "InstalledShortcut"); }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { WinProvisionLog.Warning(exception, "InstalledStartMenu"); }
        }
        return result;
    }

    private static Dictionary<string, string> ReadAppxAssets(CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    string root = package.InstalledLocation.Path;
                    var manifest = XDocument.Load(Path.Combine(root, "AppxManifest.xml"));
                    var visual = manifest.Descendants().FirstOrDefault(element => element.Name.LocalName == "VisualElements");
                    string? logo = visual?.Attribute("Square44x44Logo")?.Value ?? visual?.Attribute("Square150x150Logo")?.Value;
                    if (string.IsNullOrWhiteSpace(logo)) continue;
                    string path = Path.GetFullPath(Path.Combine(root, logo.Replace('\\', Path.DirectorySeparatorChar)));
                    if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(path))
                    {
                        string directory = Path.GetDirectoryName(path)!;
                        if (!Directory.Exists(directory)) continue;
                        path = Directory.EnumerateFiles(directory, Path.GetFileNameWithoutExtension(path) + ".*.png")
                            .OrderByDescending(file => file.Contains("targetsize-64", StringComparison.OrdinalIgnoreCase))
                            .ThenBy(file => file.Length).FirstOrDefault() ?? string.Empty;
                    }
                    if (!File.Exists(path)) continue;
                    result.TryAdd(package.Id.FamilyName, path);
                    result.TryAdd(package.Id.Name, path);
                    result.TryAdd(package.DisplayName, path);
                }
                catch (Exception exception) { WinProvisionLog.Warning(exception, "InstalledAppxIcon"); }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { WinProvisionLog.Warning(exception, "InstalledAppxIndex"); }
        return result;
    }
}
