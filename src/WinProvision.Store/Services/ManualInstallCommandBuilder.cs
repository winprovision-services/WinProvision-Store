using WinProvision.Core.Models;
using WinProvision.Core.Services;
using System.Text;

namespace WinProvision.Store.Services;

/// <summary>Gera o comando para revisão manual e pré-preenchimento no terminal.</summary>
public static class ManualInstallCommandBuilder
{
    public static string BuildInstallCommand(AppEntry app, PackageInstallOptions? options, string operation = "install")
    {
        ArgumentNullException.ThrowIfNull(app);
        options ??= new PackageInstallOptions();
        if (app.Source.Trim().Equals("chocolatey", StringComparison.OrdinalIgnoreCase))
            return ChocolateyCommandLineBuilder.BuildPowerShellPreview(operation, app.Id, options);

        string source = app.Source.Trim().ToLowerInvariant() switch
        {
            "winget" => "winget",
            "msstore" => "msstore",
            _ => throw new InvalidOperationException($"A origem '{app.Source}' não é compatível com instalação manual via WinGet.")
        };

        bool interactive = options.Interactive || source == "msstore";
        return WingetCommandLineBuilder.BuildCommand(operation, app.Id, source, options with { Interactive = interactive },
            installedVersion: options.Version);
    }

    public static IReadOnlyList<string> ParseManualChocolateyArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return [];
        if (arguments.Length > 4_096 || arguments.Any(char.IsControl))
            throw new InvalidOperationException("Os argumentos adicionais Chocolatey são inválidos ou excedem 4 KiB.");

        var parsed = new List<string>();
        int index = 0;
        while (index < arguments.Length)
        {
            while (index < arguments.Length && char.IsWhiteSpace(arguments[index])) index++;
            if (index >= arguments.Length) break;

            var value = new StringBuilder();
            bool quoted = false;
            bool started = false;
            while (index < arguments.Length)
            {
                int backslashes = 0;
                while (index < arguments.Length && arguments[index] == '\\')
                {
                    backslashes++;
                    index++;
                }

                if (index < arguments.Length && arguments[index] == '"')
                {
                    value.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1) value.Append('"');
                    else quoted = !quoted;
                    index++;
                    started = true;
                    continue;
                }

                value.Append('\\', backslashes);
                if (index >= arguments.Length || (!quoted && char.IsWhiteSpace(arguments[index]))) break;
                value.Append(arguments[index++]);
                started = true;
            }

            if (quoted)
                throw new InvalidOperationException("As aspas dos argumentos adicionais Chocolatey não estão balanceadas.");
            if (started) parsed.Add(value.ToString());
            while (index < arguments.Length && char.IsWhiteSpace(arguments[index])) index++;
            if (parsed.Count > 64)
                throw new InvalidOperationException("O comando manual aceita até 64 argumentos Chocolatey adicionais.");
        }
        return parsed;
    }

}
