using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Cli;

public interface ICliPackageBackend
{
    Task<List<UpgradablePackage>> SearchAsync(InteractiveCliRequest request, CancellationToken token);
    Task<List<UpgradablePackage>> ListAsync(string? source, CancellationToken token);
    Task<List<UpgradablePackage>> UpdatesAsync(Action<string> log, CancellationToken token);
    Task<string> DetailsAsync(string id, string source, CancellationToken token);
    Task<WingetExecutionResult> ExecuteAsync(bool install, UpgradablePackage package, Action<string> log, CancellationToken token);
    Task<WingetExecutionResult> ExecuteProfileAsync(ProfileAppRef app, Action<string> log, CancellationToken token)
        => app.OfficeOptions is null && app.PinnedVersion is null ? ExecuteAsync(true, new() { Id = app.Id, Name = app.Name ?? app.Id, Source = app.Source! }, log, token) : throw new NotSupportedException("Este serviço não executa opções de perfil.");
    Task<WingetExecutionResult> DownloadProfileAsync(ProfileAppRef app, string? directory, Action<string> log, CancellationToken token)
        => app.OfficeOptions is null && app.PinnedVersion is null ? DownloadAsync(new() { Id = app.Id, Name = app.Name ?? app.Id, Source = app.Source! }, directory, log, token) : throw new NotSupportedException("Este serviço não baixa opções de perfil.");
    Task<WingetExecutionResult> DownloadAsync(UpgradablePackage package, string? directory, Action<string> log, CancellationToken token)
        => throw new NotSupportedException();
    Task<WingetExecutionResult> ExportAsync(string path, Action<string> log, CancellationToken token)
        => throw new NotSupportedException();
    Task<WingetExecutionResult> UninstallAsync(string id, string? source, Action<string> log, CancellationToken token)
        => throw new NotSupportedException();
    Task<string> OfficeAsync(bool offline, Action<string> log, CancellationToken token)
        => throw new NotSupportedException();
    Task<WingetExecutionResult> InstallOfficeAsync(Action<string> log, CancellationToken token)
        => throw new NotSupportedException();
}

public sealed partial class CliApplication(ICliPackageBackend backend, TextReader input, TextWriter output, TextWriter error, string version, bool inputRedirected = false)
{
    private readonly TextWriter _output = TextWriter.Synchronized(output);
    private readonly TextWriter _error = TextWriter.Synchronized(error);
    private const int PageSize = 15;

    public async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        var request = InteractiveCliInput.ParseRequest(args);
        token.ThrowIfCancellationRequested();
        if (request.Command == "help") { _output.WriteLine(Help(request.Query)); return 0; }
        if (request.Command == "version") { _output.WriteLine($"WinProvision Store {version}"); return 0; }
        if (inputRedirected && !request.Yes && (request.Command is "install" or "uninstall" or "import"
            || request.InstallOffice || (request.Command == "update" && !request.Json && !request.NoInput)))
            throw new ArgumentException("Entrada redirecionada: use --yes para executar sem confirmação; em upgrade informe --id ou --all. Use --no-input ou --json para apenas consultar.");
        if (request.ElevationCache) GsudoElevationService.RequestCache(5);
        if (request.Command == "office")
        {
            Write("Microsoft 365 Família — 64 bits, pt-br, canal Current; Access e Publisher excluídos.");
            if (request.InstallOffice)
            {
                if (!await ConfirmAsync(request, "Instalar o Office com o XML padrão?", token)) return 0;
                return Report(await backend.InstallOfficeAsync(Log, token));
            }
            Write(request.Offline ? "Baixando a mídia completa para uso offline..." : "Baixando setup e XML para instalação online...");
            string folder = await backend.OfficeAsync(request.Offline, Log, token);
            Write($"Arquivos salvos: {folder}\nPara instalar depois, execute Install-Office.ps1 nessa pasta.");
            return 0;
        }
        if (request.Command == "import") return await ImportAsync(request, token);
        if (request.Command == "export") return await ExportAsync(request, token);
        if (request.Command == "uninstall")
        {
            if (!await ConfirmAsync(request, $"Desinstalar {request.Id}?", token)) return 0;
            return Report(await backend.UninstallAsync(request.Id!, request.Source, Log, token));
        }
        if (request.Command == "show")
        {
            Write(await backend.DetailsAsync(request.Id!, request.Source ?? "winget", token));
            return 0;
        }
        // Diagnostics always use stderr; structured stdout remains valid JSON, including an empty result.
        _error.WriteLine(request.Command switch
        {
            "list" => "Consultando aplicativos instalados...",
            "update" => "Consultando atualizações...",
            _ => $"Pesquisando: {Clean(request.Id ?? request.Query)}"
        });
        var packages = request.Command switch
        {
            "list" => await backend.ListAsync(request.Source, token),
            "update" => await backend.UpdatesAsync(Log, token),
            _ => await backend.SearchAsync(request, token)
        };
        packages = Filter(packages, request);
        if (request.Json)
        {
            var records = packages.Select(package => new CliPackageResult(package.Id, package.Name,
                package.CurrentVersion, package.AvailableVersion, package.Source)).ToArray();
            _output.WriteLine(JsonSerializer.Serialize(records, CliJsonContext.Default.CliPackageResultArray));
            return 0;
        }
        if (packages.Count == 0)
        {
            Write(request.Command == "update" ? "Nenhuma atualização encontrada." : "Nenhum resultado encontrado.");
            return request.Command is "install" or "download" ? 1 : 0;
        }
        if (request.Command == "download")
        {
            if (packages.Count != 1) throw new InvalidOperationException("O ID não resolveu um único pacote.");
            return Report(await backend.DownloadAsync(packages[0], request.Output, Log, token));
        }
        if (request.Command is "list" or "search" || (request.NoInput && !request.Yes))
        {
            foreach (var package in packages) Write(Describe(package, request.Command));
            Write($"\nTotal: {packages.Count}");
            return 0;
        }
        if (request.Command == "install" && packages.Count != 1) throw new InvalidOperationException("O ID não resolveu um único pacote.");
        int selected = request.Command == "install" || (request.Command == "update" && request.Id is not null)
            ? 1 : request.All ? -1 : await ReadPackageSelection(packages, request.Command, token);
        if (selected == 0) return 0;
        var chosen = selected == -1 ? packages : [packages[selected - 1]];
        if (chosen.Any(package => string.IsNullOrWhiteSpace(package.Id) || package.Id.Contains('…') || package.Id.Contains("...")
            || package.Source is not ("winget" or "msstore")))
            throw new InvalidOperationException("Pacote com ID truncado ou fonte não reconhecida; nenhuma operação foi iniciada.");
        bool install = request.Command is "search" or "install";
        Write($"\n{chosen.Count} pacote(s) para {(install ? "instalar" : "atualizar")}:");
        foreach (var package in chosen.Take(10)) Write(Describe(package, request.Command));
        if (chosen.Count > 10) Write($"... e mais {chosen.Count - 10} pacote(s).");
        if (!await ConfirmAsync(request, install ? "Instalar os pacotes?" : "Atualizar os pacotes?", token)) return 0;
        int succeeded = 0, failed = 0, reboot = 0;
        for (int i = 0; i < chosen.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var package = chosen[i];
            Write($"\n{i + 1}/{chosen.Count} — {(install ? "Instalando" : "Atualizando")} {package.Name}");
            WingetExecutionResult result;
            try { result = await backend.ExecuteAsync(install, package, Log, token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            { failed++; _error.WriteLine($"Falha em {Clean(package.Name)}: {Clean(exception.Message)}"); continue; }
            if (!result.Success)
            { failed++; _error.WriteLine($"Falha em {Clean(package.Name)} (código {result.ExitCode}). {Clean(result.Output)}"); }
            else
            {
                succeeded++;
                if (result.RebootRequired) reboot++;
                Write(result.RebootRequired ? "Concluído. Reinicialização necessária." : "Concluído.");
                if (result.RebootInitiated) break;
            }
        }
        Write($"\nResumo: {succeeded} concluído(s), {failed} falha(s), {reboot} exige(m) reinicialização.");
        return failed == 0 ? 0 : 1;
    }

    private static List<UpgradablePackage> Filter(List<UpgradablePackage> packages, InteractiveCliRequest request)
        => packages.Where(package =>
            (request.Source is null || package.Source.Equals(request.Source, StringComparison.OrdinalIgnoreCase)) &&
            (request.Id is null || package.Id.Equals(request.Id, StringComparison.OrdinalIgnoreCase)) &&
            (request.Command is "search" or "install" || request.Query.Length == 0 ||
                (request.Exact ? package.Name.Equals(request.Query, StringComparison.OrdinalIgnoreCase)
                    || package.Id.Equals(request.Query, StringComparison.OrdinalIgnoreCase)
                    : package.Name.Contains(request.Query, StringComparison.OrdinalIgnoreCase)
                        || package.Id.Contains(request.Query, StringComparison.OrdinalIgnoreCase))))
            .DistinctBy(package => (package.Source.ToLowerInvariant(), package.Id.ToLowerInvariant())).ToList();

    private async Task<int> ReadPackageSelection(List<UpgradablePackage> packages, string command, CancellationToken token)
    {
        int page = 0;
        while (true)
        {
            Write(command == "search" ? "\nSelecione a opção desejada:" : "\nSelecione a opção que deseja atualizar:");
            int start = page * PageSize, end = Math.Min(start + PageSize, packages.Count);
            for (int i = start; i < end; i++) Write($"[{i + 1}] {Describe(packages[i], command)}");
            Write($"Resultados {start + 1}–{end} de {packages.Count}");
            if (end < packages.Count) Write("[N] Próxima página");
            if (page > 0) Write("[P] Página anterior");
            if (command == "update") Write("[A] Todos os resultados");
            Write("[0] Sair");
            _output.Write("Opção: ");
            var line = await input.ReadLineAsync(token);
            if (line?.Trim().Equals("N", StringComparison.OrdinalIgnoreCase) == true && end < packages.Count) { page++; continue; }
            if (line?.Trim().Equals("P", StringComparison.OrdinalIgnoreCase) == true && page > 0) { page--; continue; }
            var selected = InteractiveCliInput.Selection(line, packages.Count, command == "update");
            if (selected is int value) return value;
            Write("Opção inválida. Use um número ou uma opção exibida no menu.");
        }
    }

    private async Task<bool> ConfirmAsync(InteractiveCliRequest request, string message, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (request.Yes) return true;
        Write(message + "\n[1] Confirmar\n[0] Sair");
        return await ReadConfirmation(token) != 0;
    }

    private async Task<int> ReadConfirmation(CancellationToken token)
    {
        while (true)
        {
            _output.Write("Opção: ");
            var selected = InteractiveCliInput.Selection(await input.ReadLineAsync(token), 1, false);
            if (selected is int value) return value;
            Write("Opção inválida. Informe 1 para confirmar ou 0 para sair.");
        }
    }

    private static string Describe(UpgradablePackage package, string command)
        => $"{package.Name} — {package.Id} [{package.SourceLabel}]" +
            (command == "update" ? $" | {package.CurrentVersion} → {package.AvailableVersion}"
                : $" | {package.CurrentVersion}");
    private void Write(string text) => _output.WriteLine(Clean(text));
    private void Log(string text) => _error.WriteLine(Clean(text));
    public static string Clean(string text) => new(text.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray());

    public static string Help(string command) => command switch
    {
        "office" => "Uso: winprovision office [--offline]\n     winprovision install --office\nTambém aceita --office [--offline] e --install --office. office baixa em Downloads, sem instalar; install --office instala; --yes (-y) dispensa a confirmação. Padrão: Microsoft 365 Família, 64 bits, pt-br, Current; Access e Publisher excluídos. Com --offline: mídia completa e hashes, apenas download; não combina com install. Pode ocupar vários GB.",
        "search" => "Uso: winprovision search <nome> [--id ID] [--source winget|msstore] [--exact] [--json]\nPesquisa sem instalar. Use install --id ID para instalar um resultado.",
        "list" => "Uso: winprovision list [nome] [--id ID] [--source winget|msstore] [--exact] [--json]\nLista aplicativos instalados. --upgrade-available consulta atualizações.",
        "update" => "Uso: winprovision upgrade [nome] [--id ID] [--source winget|msstore] [--json|--no-input]\n     winprovision upgrade --all\nSem opções: menu com índices. --all seleciona todas; use --yes (-y) com --all ou --id para executar sem confirmação. Alias: update.",
        "install" => "Uso: winprovision install --id <ID> [--source winget|msstore]\n     winprovision install --office\nUse --yes (-y) para executar sem confirmação. --office instala Microsoft 365 Família com o XML padrão. Use search para encontrar IDs de pacotes.",
        "show" => "Uso: winprovision show --id <ID> [--source winget|msstore]\nExibe detalhes sem instalar.",
        "download" => "Uso: winprovision download --id <ID> [--source winget|msstore] [--output pasta]\nBaixa o instalador com verificação de hash. Destino padrão: Downloads do usuário.",
        "uninstall" => "Uso: winprovision uninstall --id <ID> [--source winget|msstore]\nRemove o pacote exato; use --yes (-y) para executar sem confirmação. Use list para encontrar o ID.",
        "import" => "Uso: winprovision import <arquivo.json|URL HTTPS> [--download] [--output pasta]\n     winprovision import --input <arquivo.json|URL HTTPS>\nInstala a lista; use --yes (-y) para executar sem confirmação. --download apenas baixa. -i significa --input. Aceita perfis e backups do app, links de nuvem, export WinGet ou array com id e source. Preserva Office e versões fixadas. Máximo: 16 MiB e 10000 aplicativos.",
        "export" => "Uso: winprovision export [--output arquivo.json]\nExporta os pacotes reconhecidos pelo WinGet em formato compatível com import. Padrão: winprovision-apps.json no diretório atual. Não sobrescreve arquivos.",
        _ => """
            WinProvision Store — CLI

            Uso: winprovision <comando> [opções]

            Consultas:
              search       Pesquisar pacotes, sem instalar
              show         Exibir detalhes por ID
              list         Listar aplicativos instalados
              export       Exportar lista de pacotes para JSON

            Operações:
              install      Instalar por ID, com confirmação
              download     Baixar instalador na pasta Downloads
              office       Baixar setup e XML do Office; --offline inclui a mídia
              import       Instalar lista JSON; --download apenas baixa
              upgrade      Escolher atualizações; --all seleciona todas
              uninstall    Desinstalar por ID, com confirmação

            Opções comuns:
              --id ID           Identificador completo
              -s, --source      winget ou msstore
              -q, --query       Consulta por nome
              -e, --exact       Correspondência exata
              --json            JSON em search/list/upgrade; apenas consulta
              --elevation-cache Reutilizar autorização do helper neste processo (5 min de inatividade)
              --yes, -y         Executar operação sem confirmação (upgrade exige --id ou --all)
              --no-input        Consultar sem menus; com --yes executa sem interação
              -i, --input       Arquivo JSON ou URL HTTPS para import
              -o, --output      Arquivo de exportação ou pasta de download
              -h, --help        Ajuda geral ou específica
              -V, --version     Versão do WinProvision Store

            Exemplos:
              winprovision search Google Chrome
              winprovision install --id Google.Chrome.EXE
              winprovision install --office
              winprovision office --offline
              winprovision download --id Google.Chrome.EXE
              winprovision export --output apps.json
              winprovision import apps.json
              winprovision import apps.json --download
              winprovision upgrade --all
              winprovision uninstall --id Google.Chrome.EXE
              winprovision help import

            Ctrl+C cancela. Menus: [N]/[P] mudam a página; [0] sai.
            Códigos de saída: 0 sucesso, 1 falha, 2 uso inválido, 130 cancelamento.
            """
    };
}
public sealed record CliPackageResult(string Id, string Name, string Version, string AvailableVersion, string Source);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CliPackageResult[]))]
internal partial class CliJsonContext : JsonSerializerContext;
