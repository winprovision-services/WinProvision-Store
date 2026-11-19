using System.Runtime.InteropServices;

namespace WinProvision.Providers.WinGet.Cli.Discovery;

public sealed class WinGetCliDiscovery
{
    public const string DefaultExecutableName = "winget.exe";
    private const string PackagePrefix = "Microsoft.DesktopAppInstaller_";
    private const string PublisherSuffix = "__8wekyb3d8bbwe";

    public string? FindOnPath() => FindExecutable(DefaultExecutableName);

    public string? FindPackaged()
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        if (!Directory.Exists(root)) return null;
        string suffix = "_" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant() + PublisherSuffix;
        try
        {
            return Directory.EnumerateDirectories(root, PackagePrefix + "*")
                .Where(path => Path.GetFileName(path).EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => ParseVersion(Path.GetFileName(path)))
                .Select(path => Path.Combine(path, DefaultExecutableName))
                .FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public string? Resolve(string? preferredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath) && File.Exists(preferredPath)) return Path.GetFullPath(preferredPath);
        return FindOnPath() ?? FindPackaged();
    }

    private static string? FindExecutable(string name)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static Version ParseVersion(string folder)
    {
        string[] parts = folder.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var version) ? version : new Version(0, 0);
    }
}
