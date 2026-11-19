using System.Text;
using WinProvision.Core.Models;
using WinProvision.Providers.WinGet.Cli.Commands;
using WinProvision.Providers.WinGet.Cli.Contracts;

namespace WinProvision.Core.Services;

/// <summary>Única fonte dos argumentos WinGet usados pela prévia e pela execução.</summary>
public static class WingetCommandLineBuilder
{
    public static string BuildArguments(string operation, string id, string? source, PackageInstallOptions? options = null,
        string? installedVersion = null, bool includeSource = true, bool force = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        options ??= new PackageInstallOptions();
        WinGetCliOperation operationId = NormalizeOperation(operation) switch
        {
            "install" => WinGetCliOperation.Install,
            "upgrade" => WinGetCliOperation.Upgrade,
            _ => WinGetCliOperation.Uninstall
        };
        var cliOptions = new WinGetCliMutationOptions(options.Interactive, options.Scope, options.Architecture,
            options.InstallLocation, options.IgnoreSecurityHash, options.Version, options.CustomArguments,
            options.OverrideArguments, options.UpdateArguments, options.UninstallArguments, options.UninstallPrevious,
            options.InstallerType, options.Locale);
        IReadOnlyList<string> arguments = WinGetCliCommandBuilder.BuildMutation(operationId, id, source, cliOptions,
            installedVersion, includeSource, force);
        var display = new StringBuilder();
        bool quoteNext = false;
        foreach (string argument in arguments)
        {
            if (display.Length > 0) display.Append(' ');
            if (quoteNext) display.Append(Quote(argument));
            else display.Append(argument);
            quoteNext = argument is "--id" or "--source" or "--version" or "--location" or "--custom" or "--override" or "--installer-type" or "--locale";
        }
        return display.ToString();
    }

    public static string BuildCommand(string operation, string id, string? source, PackageInstallOptions? options = null,
        string? installedVersion = null, bool includeSource = true, bool force = false) =>
        "winget " + BuildArguments(operation, id, source, options, installedVersion, includeSource, force);

    private static string NormalizeOperation(string operation) => operation.Trim().ToLowerInvariant() switch
    {
        "install" or "instalar" => "install",
        "upgrade" or "update" or "atualizar" => "upgrade",
        "uninstall" or "desinstalar" => "uninstall",
        _ => throw new ArgumentOutOfRangeException(nameof(operation), "Operação WinGet não reconhecida.")
    };

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
