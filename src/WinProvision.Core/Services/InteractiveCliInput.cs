namespace WinProvision.Core.Services;

public sealed record InteractiveCliRequest(
    string Command, string Query = "", string? Id = null, string? Source = null,
    bool Exact = false, bool Json = false, bool NoInput = false, bool All = false,
    string? Input = null, string? Output = null, bool Download = false, bool Offline = false, bool InstallOffice = false, bool Yes = false, bool ElevationCache = false);

/// <summary>Contrato de entrada da CLI, independente do console e da interface.</summary>
public static class InteractiveCliInput
{
    public static (string Command, string Query) Parse(string[] arguments)
    {
        var request = ParseRequest(arguments);
        return (request.Command, request.Query);
    }

    public static InteractiveCliRequest ParseRequest(string[] arguments)
    {
        var args = arguments.AsSpan();
        if (args.Length > 0 && args[0] == "--") args = args[1..];
        if (args.Length == 0) return new("help");
        if (args.Length == 1 && args[0] is "--version" or "-V") return new("version");
        if (args.Length == 1 && args[0] is "--help" or "-h") return new("help");
        string command = CanonicalCommand(args[0]);
        args = args[1..];
        if (command == "help")
        {
            if (args.Length == 0) return new("help");
            if (args.Length == 1) return new("help", CanonicalCommand(args[0]));
            throw Invalid("Use help [comando].");
        }
        if (command == "list" && args.Length > 0 && args[0].Equals("update", StringComparison.OrdinalIgnoreCase))
        { command = "update"; args = args[1..]; }
        foreach (var arg in args)
        {
            if (arg == "--") break;
            if (arg is "--help" or "-h") return new("help", command);
        }

        if (command == "cache")
        {
            string action = args.Length == 0 ? "list" : args.Length == 1 ? args[0].ToLowerInvariant() : "";
            if (action is not ("list" or "prune" or "clear")) throw Invalid("Use cache [list|prune|clear].");
            return new("cache", action);
        }

        string? id = null, source = null, input = null, output = null;
        bool download = false, offline = false, installOffice = false;
        bool exact = false, json = false, noInput = false, all = false, positionalOnly = false, yes = false, elevationCache = false;
        var words = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!positionalOnly && arg == "--") { positionalOnly = true; continue; }
            if (!positionalOnly && arg.StartsWith('-'))
            {
                int equal = arg.IndexOf('=');
                var option = equal < 0 ? arg : arg[..equal];
                option = option switch { "-y" => "--yes", "-q" => "--query", "-s" => "--source", "-e" => "--exact", "-id" => "--id", "-d" when command == "uninstall" => "--id", "-all" => "--all", "-i" => "--input", "-o" => "--output", "-d" => "--output", _ => option };
                if (!seen.Add(option)) throw Invalid($"Opção repetida: {option}.");
                bool takesValue = option is "--query" or "--id" or "--source" or "--input" or "--output";
                string? value = null;
                if (takesValue)
                {
                    if (equal >= 0) value = arg[(equal + 1)..];
                    else if (++i < args.Length && !args[i].StartsWith('-')) value = args[i];
                    if (string.IsNullOrWhiteSpace(value)) throw Invalid($"Informe um valor para {option}.");
                    ValidateText(value);
                }
                else if (equal >= 0) throw Invalid($"{option} não aceita valor.");
                switch (option)
                {
                    case "--query": words.Add(value!); break;
                    case "--id": id = value; break;
                    case "--source": source = value!.ToLowerInvariant(); break;
                    case "--exact": exact = true; break;
                    case "--json": json = true; break;
                    case "--no-input": noInput = true; break;
                    case "--elevation-cache": elevationCache = true; break;
                    case "--yes": yes = true; break;
                    case "--all": all = true; break;
                    case "--input": input = value; break;
                    case "--output": output = value; break;
                    case "--download": download = true; break;
                    case "--offline": offline = true; break;
                    case "--office" when command == "install": command = "office"; installOffice = true; break;
                    case "--upgrade-available" when command == "list": command = "update"; break;
                    default: throw Invalid($"Opção desconhecida: {option}.");
                }
            }
            else { ValidateText(arg); words.Add(arg); }
        }
        var query = string.Join(' ', words).Trim();
        if (offline && command != "office") throw Invalid("--offline só é permitido em office.");
        if (elevationCache && (command is not ("install" or "update" or "uninstall" or "import") && !installOffice || json || noInput && !yes))
            throw Invalid("--elevation-cache exige uma operação de instalação, atualização, remoção ou importação.");
        if (yes && json) throw Invalid("--yes não combina com --json; JSON apenas consulta.");
        if (yes && command is not ("install" or "update" or "uninstall" or "import") && !installOffice) throw Invalid("--yes exige uma operação de instalação, atualização, remoção ou importação.");
        if (yes && command == "update" && !all && id is null) throw Invalid("Com --yes, informe --id ou --all para atualizar sem seleção interativa.");
        if (command == "office" && (seen.Any(option => option != "--offline" && option != "--office" && option != "--yes" && option != "--elevation-cache" && !(option == "--no-input" && yes)) || query.Length > 0))
            throw Invalid("Use office [--offline], sem filtros ou outras opções.");
        if (installOffice && offline) throw Invalid("--offline apenas baixa. Use office --offline ou install --office separadamente.");
        if (command == "import")
        {
            if (input is not null && words.Count > 0) throw Invalid("Use --input ou caminho posicional, sem combinar os dois.");
            input ??= query.Length > 0 ? query : null;
            query = "";
            if (input is null) throw Invalid("Use import --input <arquivo.json ou URL HTTPS>.");
        }
        if (input is not null && command != "import") throw Invalid("--input só é permitido em import.");
        if (output is not null && command is not ("export" or "download" or "import")) throw Invalid("--output não é permitido neste comando.");
        if (download && command != "import") throw Invalid("--download só é permitido em import.");
        if (command is "import" or "export" && (id is not null || source is not null || exact || json || (noInput && !yes) || all || query.Length > 0))
            throw Invalid("Import/export não aceita filtros nem opções de consulta.");
        if (command == "import" && output is not null && !download) throw Invalid("--output em import exige --download.");
        if (query.Length > 512) throw Invalid("A consulta deve ter no máximo 512 caracteres.");
        if (seen.Contains("--query") && words.Count > 1) throw Invalid("Use --query ou texto posicional, sem combinar os dois.");
        if (id is not null && (query.Length > 0 || id.Length > 256 || id.Any(char.IsWhiteSpace) || id.Contains('…') || id.Contains("...")))
            throw Invalid("Informe um ID completo, sem espaços, separado da consulta por nome.");
        if (source is not (null or "winget" or "msstore")) throw Invalid("Fonte inválida. Use winget ou msstore.");
        if (command is "install" or "show" or "download" or "uninstall")
        {
            if (id is null) throw Invalid($"Use {command} --id <ID do pacote> para identificar o pacote com precisão.");
            if (all || json || (command != "show" && noInput && !yes))
                throw Invalid($"{command} não aceita --all, --json ou instalação sem confirmação.");
        }
        if (command == "search" && id is null && query.Length == 0) throw Invalid("Informe o nome ou --id para pesquisar.");
        if (all && (command != "update" || id is not null || query.Length > 0 || json || (noInput && !yes)))
            throw Invalid("--all só pode ser usado em upgrade/update interativo, sem filtro por pacote.");
        return new(command, query, id, source, exact, json, noInput, all, input, output, download, offline, installOffice, yes, elevationCache);
    }

    private static string CanonicalCommand(string command) => (command.StartsWith("--") ? command[2..] : command).ToLowerInvariant() switch
    {
        "search" or "find" => "search",
        "update" or "upgrade" => "update",
        "list" or "ls" => "list",
        "show" or "info" => "show",
        "install" => "install",
        "import" or "export" or "download" or "uninstall" or "office" => command.TrimStart('-').ToLowerInvariant(),
        "help" => "help",
        "cache" => "cache",
        _ => throw Invalid($"Comando desconhecido: {command}.")
    };

    private static void ValidateText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl) || text.Length > 512)
            throw Invalid("O argumento está vazio, é muito longo ou contém caracteres de controle.");
    }
    private static ArgumentException Invalid(string detail) => new($"{detail} Use winprovision --help.");

    public static int? Selection(string? input, int count, bool allowAll)
    {
        if (input is null) return 0;
        input = input.Trim();
        if (allowAll && input.Equals("A", StringComparison.OrdinalIgnoreCase)) return -1;
        return int.TryParse(input, out var value) && value >= 0 && value <= count ? value : null;
    }
}
