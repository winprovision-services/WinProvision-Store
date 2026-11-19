using System.Diagnostics.CodeAnalysis;

namespace WinProvision.Elevation;

/// <summary>Category-aware executable path policy. Phase 4 intentionally runs in log-only mode.</summary>
public sealed class ElevationPathPolicy : IElevationPolicy
{
    private readonly Action<string> _log;
    private readonly ElevationPolicyMode _mode;
    private readonly IExecutablePathResolver _pathResolver;
    private readonly Func<ElevationCategory, IEnumerable<string>> _approvedRoots;

    public ElevationPathPolicy(Action<string> log, ElevationPolicyMode mode = ElevationPolicyMode.LogOnly)
        : this(log, mode, new ExecutablePathResolver(), GetApprovedRoots)
    {
    }

    internal ElevationPathPolicy(Action<string> log, ElevationPolicyMode mode,
        IExecutablePathResolver pathResolver, Func<ElevationCategory, IEnumerable<string>> approvedRoots)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _mode = mode;
        _pathResolver = pathResolver;
        _approvedRoots = approvedRoots;
    }

    public ValueTask<ElevationPolicyResult> EvaluateAsync(ElevatedCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        bool resolved = _pathResolver.TryResolve(command.ExecutablePath, out string? executable, out string resolutionReason);
        bool approved = false;
        string reason = resolved ? "outside-approved-roots" : resolutionReason;
        if (resolved)
        {
            foreach (string root in _approvedRoots(command.Category))
            {
                if (!_pathResolver.TryResolveDirectory(root, out string? canonicalRoot)) continue;
                if (!IsWithinDirectory(executable!, canonicalRoot!)) continue;
                approved = true;
                reason = "within-approved-root";
                break;
            }
        }

        // Do not include command arguments: they can contain credentials, paths or user data.
        string decision = approved ? "allowed" : _mode == ElevationPolicyMode.LogOnly ? "would-reject" : "rejected";
        string integrity = command.Category == ElevationCategory.DownloadedInstaller
            ? command.ExpectedSha256 is null ? "sha256-missing" : "sha256-provided"
            : "sha256-not-required";
        try { _log($"ElevationPolicy category={command.Category} decision={decision} mode={_mode} reason={reason} integrity={integrity} executable=\"{executable ?? command.ExecutablePath}\""); }
        catch { /* Log-only evaluation must not prevent a command from being dispatched. */ }

        bool allowed = _mode == ElevationPolicyMode.LogOnly || approved;
        return ValueTask.FromResult(new ElevationPolicyResult(allowed, reason));
    }

    internal static bool IsWithinDirectory(string path, string directory)
    {
        string canonicalPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string canonicalDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (string.Equals(canonicalPath, canonicalDirectory, StringComparison.OrdinalIgnoreCase)) return true;
        string prefix = canonicalDirectory + Path.DirectorySeparatorChar;
        return canonicalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> GetApprovedRoots(ElevationCategory category)
    {
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string temp = Path.GetTempPath();
        string app = AppContext.BaseDirectory;
        string[] nonEmpty(params string[] values) => values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();

        return category switch
        {
            ElevationCategory.WinGet => nonEmpty(
                Path.Combine(programFiles, "WindowsApps"),
                Path.Combine(local, "Microsoft", "WindowsApps")),
            ElevationCategory.Chocolatey => nonEmpty(
                Path.Combine(common, "chocolatey"),
                Path.Combine(programFiles, "Chocolatey"),
                Path.Combine(local, "Programs", "chocolatey")),
            ElevationCategory.Office => nonEmpty(
                Path.Combine(local, "WinProvision", "OfficeDeploymentTool"),
                Path.Combine(local, "WinProvision", "Office"),
                Path.Combine(programFiles, "OfficeDeploymentTool"),
                Path.Combine(programFilesX86, "OfficeDeploymentTool"),
                programFiles, programFilesX86),
            ElevationCategory.DownloadedInstaller => nonEmpty(
                Path.Combine(local, "WinProvision", "InstallerCache"),
                Path.Combine(local, "WinProvisionStore", "ApiCache"),
                Path.Combine(common, "WinProvisionStore", "lan-run"),
                Path.Combine(temp, "WinProvision")),
            ElevationCategory.Uninstall => nonEmpty(
                programFiles, programFilesX86,
                Path.Combine(local, "Programs"),
                Path.Combine(common, "chocolatey"),
                Path.Combine(programFiles, "WindowsApps"),
                Path.Combine(local, "Microsoft", "WindowsApps"), system),
            ElevationCategory.Network => nonEmpty(system),
            ElevationCategory.AppUpdate => nonEmpty(
                Path.Combine(temp, "WinProvisionUpdate"),
                programFiles, programFilesX86, system),
            ElevationCategory.ComHost or ElevationCategory.LanClient => nonEmpty(
                Path.Combine(app, "cli"), app),
            ElevationCategory.Other => [],
            _ => []
        };
    }
}

internal interface IExecutablePathResolver
{
    bool TryResolve(string executablePath, [NotNullWhen(true)] out string? resolvedPath, out string reason);
    bool TryResolveDirectory(string directoryPath, [NotNullWhen(true)] out string? resolvedPath);
}

internal sealed class ExecutablePathResolver : IExecutablePathResolver
{
    public bool TryResolve(string executablePath, [NotNullWhen(true)] out string? resolvedPath, out string reason)
    {
        resolvedPath = null;
        if (string.IsNullOrWhiteSpace(executablePath)) { reason = "empty-path"; return false; }

        try
        {
            string? candidate = ResolveExecutableCandidate(executablePath);
            if (candidate is null) { reason = "executable-not-found"; return false; }
            if (!TryResolveExistingPath(candidate, out resolvedPath)) { reason = "path-resolution-failed"; return false; }
            reason = "resolved";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                           or ArgumentException or NotSupportedException)
        {
            reason = "path-resolution-failed";
            return false;
        }
    }

    public bool TryResolveDirectory(string directoryPath, [NotNullWhen(true)] out string? resolvedPath)
    {
        resolvedPath = null;
        try
        {
            return Directory.Exists(directoryPath) && TryResolveExistingPath(directoryPath, out resolvedPath, requireFile: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                           or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static string? ResolveExecutableCandidate(string executablePath)
    {
        if (Path.IsPathFullyQualified(executablePath) || executablePath.Contains(Path.DirectorySeparatorChar)
            || executablePath.Contains(Path.AltDirectorySeparatorChar))
        {
            string fullPath = Path.GetFullPath(executablePath);
            return File.Exists(fullPath) ? fullPath : null;
        }

        string[] extensions = Path.HasExtension(executablePath)
            ? [string.Empty]
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> searchDirectories = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.Windows)
        }.Concat((Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (string directory in searchDirectories.Where(Directory.Exists))
        foreach (string extension in extensions)
        {
            string candidate = Path.GetFullPath(Path.Combine(directory, executablePath + extension));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static bool TryResolveExistingPath(string path, [NotNullWhen(true)] out string? resolvedPath,
        bool requireFile = true)
    {
        resolvedPath = null;
        string fullPath = Path.GetFullPath(path);
        if (requireFile ? !File.Exists(fullPath) : !Directory.Exists(fullPath)) return false;

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrEmpty(root)) return false;
        string current = root;
        string relative = Path.GetRelativePath(root, fullPath);
        foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) == 0) continue;

            FileSystemInfo item = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            FileSystemInfo? target = item.ResolveLinkTarget(returnFinalTarget: true);
            if (target is null) return false;
            current = Path.GetFullPath(target.FullName);
        }

        resolvedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
        return true;
    }
}
