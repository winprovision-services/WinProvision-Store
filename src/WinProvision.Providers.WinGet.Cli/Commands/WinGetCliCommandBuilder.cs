using WinProvision.Providers.WinGet.Cli.Contracts;

namespace WinProvision.Providers.WinGet.Cli.Commands;

public static class WinGetCliCommandBuilder
{
    public static IReadOnlyList<string> BuildMutation(WinGetCliOperation operation, string packageId, string? source,
        WinGetCliMutationOptions? options = null, string? installedVersion = null, bool includeSource = true, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        if (operation is not (WinGetCliOperation.Install or WinGetCliOperation.Upgrade or WinGetCliOperation.Uninstall))
            throw new ArgumentOutOfRangeException(nameof(operation), "A operação não é uma mutação de pacote.");
        options ??= new();
        var args = new List<string> { operation switch
        {
            WinGetCliOperation.Install => "install",
            WinGetCliOperation.Upgrade => "upgrade",
            _ => "uninstall"
        }, "--id", packageId, "--exact" };
        if (includeSource && !string.IsNullOrWhiteSpace(source)) args.AddRange(["--source", source.Trim()]);
        args.Add("--accept-source-agreements");
        if (!options.Interactive) args.Add("--disable-interactivity");
        string? version = operation == WinGetCliOperation.Uninstall ? installedVersion ?? options.Version : options.Version;
        if (!string.IsNullOrWhiteSpace(version)) args.AddRange(["--version", version.Trim()]);
        args.Add(options.Interactive ? "--interactive" : "--silent");
        if (operation != WinGetCliOperation.Uninstall)
        {
            if (operation == WinGetCliOperation.Upgrade) args.Add("--include-unknown");
            if (!string.IsNullOrWhiteSpace(options.Scope)) args.AddRange(["--scope", options.Scope]);
            if (!string.IsNullOrWhiteSpace(options.Architecture)) args.AddRange(["--architecture", options.Architecture]);
            if (!string.IsNullOrWhiteSpace(options.InstallerType)) args.AddRange(["--installer-type", options.InstallerType]);
            if (!string.IsNullOrWhiteSpace(options.Locale)) args.AddRange(["--locale", options.Locale]);
            if (!string.IsNullOrWhiteSpace(options.InstallLocation)) args.AddRange(["--location", options.InstallLocation]);
            args.Add("--accept-package-agreements");
            if (force || !string.IsNullOrWhiteSpace(version)) args.Add("--force");
            if (options.IgnoreSecurityHash) args.Add("--ignore-security-hash");
        }
        string? custom = operation switch
        {
            WinGetCliOperation.Upgrade => options.UpdateArguments ?? options.CustomArguments,
            WinGetCliOperation.Uninstall => options.UninstallArguments,
            _ => options.CustomArguments
        };
        if (!string.IsNullOrWhiteSpace(custom)) args.AddRange(["--custom", custom.Trim()]);
        if (operation != WinGetCliOperation.Uninstall && !string.IsNullOrWhiteSpace(options.OverrideArguments))
            args.AddRange(["--override", options.OverrideArguments.Trim()]);
        if (operation == WinGetCliOperation.Upgrade && options.UninstallPrevious) args.Add("--uninstall-previous");
        return args.AsReadOnly();
    }

    public static IReadOnlyList<string> Build(WinGetCliOperation operation, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Argumentos WinGet vazios não são aceitos.", nameof(values));
        string verb = operation switch
        {
            WinGetCliOperation.Probe => "--version",
            WinGetCliOperation.Search => "search",
            WinGetCliOperation.List => "list",
            WinGetCliOperation.Show => "show",
            WinGetCliOperation.UpgradeList => "upgrade",
            WinGetCliOperation.Install => "install",
            WinGetCliOperation.Upgrade => "upgrade",
            WinGetCliOperation.Uninstall => "uninstall",
            WinGetCliOperation.Download => "download",
            WinGetCliOperation.Export => "export",
            WinGetCliOperation.Source => "source",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var result = new List<string> { verb };
        result.AddRange(values);
        return result.AsReadOnly();
    }
}

public sealed record WinGetCliMutationOptions(bool Interactive = false, string? Scope = null, string? Architecture = null,
    string? InstallLocation = null, bool IgnoreSecurityHash = false, string? Version = null,
    string? CustomArguments = null, string? OverrideArguments = null, string? UpdateArguments = null,
    string? UninstallArguments = null, bool UninstallPrevious = false, string? InstallerType = null, string? Locale = null);
