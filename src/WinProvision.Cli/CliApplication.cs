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

public sealed partial class CliApplication
{
    private readonly ICliPackageBackend backend;
    private readonly string version;
    private readonly CliConsole _console;
    private readonly TextWriter _output;

    // Input parameters remain available to callers compiled against the original console adapter.
    public CliApplication(ICliPackageBackend backend, TextReader input, TextWriter output, TextWriter error, string version, bool inputRedirected = false, CliConsole? presentation = null)
    {
        this.backend = backend;
        this.version = version;
        _console = presentation ?? new(output, error);
        _output = TextWriter.Synchronized(output);
    }

    public async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        var request = InteractiveCliInput.ParseRequest(args);
        token.ThrowIfCancellationRequested();
        if (request.Command == "logs") { _console.Section("Diagnósticos", CliDiagnostics.DirectoryPath); return 0; }
        if (request.Command == "help") { _console.Help(Help(request.Query)); return 0; }
        if (request.Command == "version") { _console.Section("Versão", $"WinProvision Store {version}"); return 0; }
        if (request.Command == "office")
        {
            _console.Section("Office", "Microsoft 365 Família — 64 bits, pt-br, canal Current; Access e Publisher excluídos.");
            if (request.InstallOffice)
            {
                return Report(await _console.RunAsync("Instalando Office", () => backend.InstallOfficeAsync(Log, token), token), "Office instalado");
            }
            _console.Note(request.Offline ? "Baixando a mídia completa para uso offline..." : "Baixando setup e XML para instalação online...");
            string folder = await _console.RunAsync("Baixando Office", () => backend.OfficeAsync(request.Offline, Log, token), token);
            _console.Result("Office — download concluído", true);
            _console.Section("Arquivos do Office", $"Destino: {folder}\nPara instalar depois, execute Install-Office.ps1 nessa pasta.");
            return 0;
        }
        if (request.Command == "import") return await ImportAsync(request, token);
        if (request.Command == "export") return await ExportAsync(request, token);
        if (request.Command == "uninstall")
        {
            return Report(await _console.RunAsync($"Desinstalando {request.Id}", () => backend.UninstallAsync(request.Id!, request.Source, Log, token), token), "Aplicativo desinstalado");
        }
        if (request.Command == "show")
        {
            _console.Details(await _console.QueryAsync("Consultando detalhes...",
                () => backend.DetailsAsync(request.Id!, request.Source ?? "winget", token), token));
            return 0;
        }
        // Status stays outside structured stdout; technical diagnostics go to the file sink.
        string status = request.Command switch
        {
            "list" => "Consultando aplicativos instalados...",
            "update" => "Consultando atualizações...",
            _ => $"Pesquisando: {Clean(request.Id ?? request.Query)}"
        };
        Task<List<UpgradablePackage>> Query() => request.Command switch
        {
            "list" => backend.ListAsync(request.Source, token),
            "update" => backend.UpdatesAsync(Log, token),
            _ => backend.SearchAsync(request, token)
        };
        List<UpgradablePackage> packages;
        if (request.Json)
        {
            _console.Status(status);
            packages = await Query();
        }
        else packages = await _console.QueryAsync(status, Query, token);
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
            if (request.Command is "install" or "download")
            {
                _console.Result(CliFailure.Message("O pacote não foi encontrado.", request.Id), false);
                return 1;
            }
            _console.Packages(packages, request.Command);
            return 0;
        }
        if (request.Command == "download")
        {
            if (packages.Count != 1) throw new InvalidOperationException("O ID não resolveu um único pacote.");
            return Report(await _console.RunAsync($"Baixando {packages[0].Name}", () => backend.DownloadAsync(packages[0], request.Output, Log, token), token), "Download concluído");
        }
        if (request.Command is "list" or "search" || request.Command == "update" && request.NoInput)
        {
            _console.Packages(packages, request.Command);
            return 0;
        }
        if (request.Command == "install" && packages.Count != 1) throw new InvalidOperationException("O ID não resolveu um único pacote.");
        int selected = request.Command == "install" || request.Id is not null ? 1 : -1;
        var chosen = selected == -1 ? packages : [packages[selected - 1]];
        if (chosen.Any(package => string.IsNullOrWhiteSpace(package.Id) || package.Id.Contains('…') || package.Id.Contains("...")
            || package.Source is not ("winget" or "msstore")))
            throw new InvalidOperationException("Pacote com ID truncado ou fonte não reconhecida; nenhuma operação foi iniciada.");
        bool install = request.Command is "search" or "install";
        _console.Heading(install ? "Instalação" : "Atualização");
        _console.Note($"{chosen.Count} pacote(s) para {(install ? "instalar" : "atualizar")}:");
        int succeeded = 0, failed = 0, reboot = 0;
        for (int i = 0; i < chosen.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var package = chosen[i];
            WingetExecutionResult result;
            try { result = await _console.RunAsync($"{(install ? "Instalando" : "Atualizando")} {package.Name}",
                () => backend.ExecuteAsync(install, package, Log, token), token, i, chosen.Count); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            { failed++; Log(exception.ToString()); _console.Result(CliFailure.Message(CliFailure.Describe(exception), package.Name), false); continue; }
            if (!result.Success)
            { failed++; Log($"{package.Id}: {result.ExitCode} {result.Output}"); _console.Result(CliFailure.Message(CliFailure.Describe(result), package.Name), false); }
            else
            {
                succeeded++;
                if (result.RebootRequired) reboot++;
                _console.Result($"{package.Name} — {(install ? "instalado" : "atualizado")}", true, result.RebootRequired);
                if (result.RebootInitiated) break;
            }
        }
        _console.Summary(succeeded, failed, reboot);
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

    private void Log(string text) => CliDiagnostics.Write(text);
    public static string Clean(string text) => new(text.Where(character => !char.IsControl(character) || character is '\n' or '\t').ToArray());

    public static string Help(string command) => command switch
    {
        "office" => "Uso: winprovision office [--offline]\n     winprovision install --office\nTambém aceita --office [--offline] e --install --office. office baixa em Downloads, sem instalar; install --office instala diretamente. Padrão: Microsoft 365 Família, 64 bits, pt-br, Current; Access e Publisher excluídos. Com --offline: mídia completa e hashes, apenas download; não combina com install. Pode ocupar vários GB.",
        "search" => "Uso: winprovision search <nome> [--id ID] [--source winget|msstore] [--exact] [--json]\nPesquisa sem instalar. Use install --id ID para instalar um resultado.",
        "list" => "Uso: winprovision list [nome] [--id ID] [--source winget|msstore] [--exact] [--json]\nLista aplicativos instalados. --upgrade-available consulta atualizações.",
        "update" => "Uso: winprovision upgrade [nome] [--id ID] [--source winget|msstore] [--json|--no-input]\n     winprovision upgrade --all\nAtualiza todos os resultados diretamente. --id limita a um aplicativo; --json ou --no-input apenas consulta. Alias: update.",
        "install" => "Uso: winprovision install --id <ID> [--source winget|msstore]\n     winprovision install --office\nExecuta diretamente, sem menus. --office instala Microsoft 365 Família com o XML padrão. Use search para encontrar IDs de pacotes.",
        "show" => "Uso: winprovision show --id <ID> [--source winget|msstore]\nExibe detalhes sem instalar.",
        "download" => "Uso: winprovision download --id <ID> [--source winget|msstore] [--output pasta]\nBaixa o instalador com verificação de hash. Destino padrão: pasta configurada no app ou Downloads do usuário.",
        "uninstall" => "Uso: winprovision uninstall --id <ID> [--source winget|msstore]\nRemove o pacote exato diretamente, sem confirmação da CLI. Use list para encontrar o ID.",
        "import" => "Uso: winprovision import <arquivo.json|URL HTTPS> [--download] [--output pasta]\n     winprovision import --input <arquivo.json|URL HTTPS>\nInstala a lista diretamente, sem menus. --download apenas baixa. -i significa --input. Aceita perfis e backups do app, links de nuvem, export WinGet ou array com id e source. Preserva Office e versões fixadas. Máximo: 16 MiB e 10000 aplicativos.",
        "logs" => "Uso: winprovision logs\nMostra a pasta dos diagnósticos técnicos da CLI.",
        "help" => "Uso: winprovision help [comando]\nExibe a ajuda geral ou específica de um comando.",
        "export" => "Uso: winprovision export [--output arquivo.json]\nExporta os pacotes reconhecidos pelo WinGet em formato compatível com import. Padrão: winprovision-apps.json no diretório atual. Não sobrescreve arquivos.",
        _ => """
            WinProvision Store — CLI

            Uso: winprovision <comando> [opções]

            Consultas:
              search       Pesquisar pacotes, sem instalar
              show         Exibir detalhes por ID
              list         Listar aplicativos instalados
              export       Exportar lista de pacotes para JSON
              logs         Mostrar pasta dos diagnósticos

            Operações:
              install      Instalar por ID, sem menus
              download     Baixar instalador na pasta configurada
              office       Baixar setup e XML do Office; --offline inclui a mídia
              import       Instalar lista JSON; --download apenas baixa
              upgrade      Atualizar todos; --id limita a um aplicativo
              uninstall    Desinstalar por ID, sem menus

            Opções comuns:
              --id ID           Identificador completo
              -s, --source      winget ou msstore
              -q, --query       Consulta por nome
              -e, --exact       Correspondência exata
              --json            JSON em search/list/upgrade; apenas consulta
              --elevation-cache  Compatibilidade; cache automático de 5 minutos
              --no-input        Em upgrade, apenas consulta; demais comandos sem menus
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

            Ctrl+C cancela. Operações executam sem confirmação da CLI.
            logs mostra a pasta dos diagnósticos.
            Códigos de saída: 0 sucesso, 1 falha, 2 uso inválido, 130 cancelamento.
            """
    };
}
public sealed record CliPackageResult(string Id, string Name, string Version, string AvailableVersion, string Source);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CliPackageResult[]))]
internal partial class CliJsonContext : JsonSerializerContext;
