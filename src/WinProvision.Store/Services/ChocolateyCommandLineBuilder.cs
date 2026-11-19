using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

/// <summary>Fonte única dos argumentos Chocolatey usados na execução e na prévia manual.</summary>
public static class ChocolateyCommandLineBuilder
{
    public static string[] BuildArguments(string operation, string id, PackageInstallOptions options)
    {
        string verb = operation.Trim().ToLowerInvariant() switch
        {
            "install" or "instalar" => "install",
            "upgrade" or "update" or "atualizar" => "upgrade",
            "uninstall" or "desinstalar" => "uninstall",
            _ => throw new ArgumentOutOfRangeException(nameof(operation), "Operação Chocolatey não reconhecida.")
        };
        ValidateId(id);

        var arguments = new List<string> { verb, id.Trim(), "-y", "--no-progress" };
        if (options.Interactive) arguments.Add("--notsilent");
        if ((verb is "install" or "upgrade") && string.Equals(options.Architecture, "x86", StringComparison.OrdinalIgnoreCase)) arguments.Add("--forcex86");
        if ((verb is "install" or "upgrade") && options.IgnoreSecurityHash) arguments.Add("--ignore-checksum");
        if ((verb is "install" or "upgrade") && options.AllowPrerelease) arguments.Add("--pre");
        if (!string.IsNullOrWhiteSpace(options.Version))
        {
            string version = options.Version.Trim();
            ValidateVersion(version);
            arguments.AddRange(new[] { "--version", version, "--allow-downgrade" });
        }

        string? custom = verb switch
        {
            "install" => options.CustomArguments,
            "upgrade" => options.UpdateArguments ?? options.CustomArguments,
            "uninstall" => options.UninstallArguments,
            _ => null
        };
        arguments.AddRange(ManualInstallCommandBuilder.ParseManualChocolateyArguments(custom));
        return [.. arguments];
    }

    public static string BuildPowerShellPreview(string operation, string id, PackageInstallOptions options)
    {
        string[] arguments = BuildArguments(operation, id, options);
        return "choco.exe " + CommandLineDisplayFormatter.FormatPowerShellArguments(arguments);
    }

    private static void ValidateId(string id)
    {
        string value = id.Trim();
        if (value.Length is 0 or > 128 || !char.IsAsciiLetterOrDigit(value[0])
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            throw new ArgumentException("O identificador do pacote Chocolatey é inválido.", nameof(id));
    }

    private static void ValidateVersion(string version)
    {
        if (version.Length > 128 || !char.IsAsciiLetterOrDigit(version[0])
            || version.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-' and not '+'))
            throw new ArgumentException("A versão informada não é válida para o Chocolatey.", nameof(version));
    }

}
