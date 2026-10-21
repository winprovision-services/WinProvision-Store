using Spectre.Console;
using WinProvision.Core.Models;

namespace WinProvision.Cli;

/// <summary>Owns terminal rendering; package services never write to the live display.</summary>
public sealed class CliConsole
{
    private readonly IAnsiConsole _console;
    private readonly IAnsiConsole _errorConsole;
    private readonly bool _animate;

    public CliConsole(TextWriter output, TextWriter error, IAnsiConsole? console = null)
    {
        _console = console ?? AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output),
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        _errorConsole = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(error),
            Ansi = ReferenceEquals(error, Console.Error) && !Console.IsErrorRedirected && !Console.IsOutputRedirected ? AnsiSupport.Detect : AnsiSupport.No,
            ColorSystem = ReferenceEquals(error, Console.Error) && !Console.IsErrorRedirected && !Console.IsOutputRedirected
                ? ColorSystemSupport.Detect : ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No
        });
        _animate = _console.Profile.Capabilities.Ansi && _console.Profile.Capabilities.Interactive;
    }

    public static CliConsole Standard() => new(Console.Out, Console.Error,
        AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(Console.Out),
            Ansi = Console.IsOutputRedirected ? AnsiSupport.No : AnsiSupport.Detect,
            ColorSystem = Console.IsOutputRedirected ? ColorSystemSupport.NoColors : ColorSystemSupport.Detect,
            Interactive = Console.IsOutputRedirected || Console.IsInputRedirected ? InteractionSupport.No : InteractionSupport.Detect
        }));

    public void Heading(string title) => _console.MarkupLine($"  [skyblue1]{Markup.Escape(CliApplication.Clean(title))}[/]");

    public void Note(string message) => _console.WriteLine($"  {CliApplication.Clean(message)}");

    public void Status(string message) => _errorConsole.MarkupLine($"  [skyblue1]•[/] {Markup.Escape(CliApplication.Clean(message))}");

    public void Section(string title, string content)
    {
        _console.Write(new Panel(new Text(CliApplication.Clean(content)))
            .Header($"[skyblue1]{Markup.Escape(CliApplication.Clean(title))}[/]")
            .Border(BoxBorder.Rounded).BorderStyle(new Style(Color.SkyBlue1)));
    }

    public void Help(string content)
    {
        // The help text is shared with callers; lay out its authored sections as tables
        // so descriptions wrap within their column even in compact terminals.
        foreach (var block in content.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            if (lines[0] is "Consultas:" or "Operações:" or "Opções comuns:")
            {
                Heading(lines[0].TrimEnd(':'));
                var table = new Table().Border(TableBorder.None);
                table.AddColumn(lines[0] == "Opções comuns:" ? "[skyblue1]Opção[/]" : "[skyblue1]Comando[/]");
                table.AddColumn("[skyblue1]Descrição[/]");
                foreach (var line in lines.Skip(1))
                {
                    string row = line.Trim();
                    int separator = row.IndexOf("  ", StringComparison.Ordinal);
                    table.AddRow(Markup.Escape(separator < 0 ? row : row[..separator]),
                        Markup.Escape(separator < 0 ? "" : row[separator..].TrimStart()));
                }
                _console.Write(table);
            }
            else if (lines[0] == "Exemplos:")
                Section("Exemplos", string.Join('\n', lines.Skip(1).Select(line => line.TrimStart())));
            else if (lines[0].StartsWith("WinProvision Store", StringComparison.Ordinal)) Heading(block);
            else Section(lines[0].StartsWith("Ctrl+C", StringComparison.Ordinal) ? "Execução" : "Ajuda", block);
        }
    }

    public void Summary(int succeeded, int failed, int reboot)
    {
        Heading("Resumo");
        _console.MarkupLine($"  [springgreen2]{succeeded} concluído(s)[/], [{(failed == 0 ? "grey" : "red")}]{failed} falha(s)[/], [grey]{reboot} exige(m) reinicialização.[/]");
    }

    public void Cancelled() => _errorConsole.MarkupLine("  [yellow]•[/] Operação cancelada.");

    public async Task<T> RunAsync<T>(string label, Func<Task<T>> operation, CancellationToken token,
        int completed = 0, int total = 1)
    {
        token.ThrowIfCancellationRequested();
        label = CliApplication.Clean(label);
        if (!_animate)
        {
            _console.WriteLine($"  {completed + 1}/{total} — {label}");
            return await operation();
        }

        // The bar measures completed applications, never elapsed time or invented percentages.
        // A single operation has no measurable percentage, so its bar is indeterminate.
        return await _console.Progress().AutoClear(true).HideCompleted(false)
            .Columns(new ProgressBarColumn
            {
                Width = 30,
                CompletedStyle = new Style(Color.SpringGreen2),
                FinishedStyle = new Style(Color.SpringGreen2),
                RemainingStyle = new Style(Color.Grey)
            }, new SpinnerColumn(Spinner.Known.Dots) { Style = new Style(Color.SkyBlue1) },
                new TaskDescriptionColumn())
            .StartAsync(async context =>
            {
                var task = context.AddTask($"[skyblue1]{Markup.Escape(label)}[/] [grey]({completed + 1}/{total})[/]", maxValue: total);
                task.Value = completed;
                task.IsIndeterminate = total == 1;
                // Do not mark a task complete before its real operation finishes.
                var result = await operation();
                token.ThrowIfCancellationRequested();
                task.Value = completed + 1;
                task.StopTask();
                return result;
            });
    }

    public async Task<T> QueryAsync<T>(string label, Func<Task<T>> query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        label = CliApplication.Clean(label);
        if (!_animate)
        {
            Status(label);
            return await query();
        }
        return await _console.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(new Style(Color.SkyBlue1))
            .StartAsync(Markup.Escape(label), async _ => await query());
    }

    public void Packages(IReadOnlyList<UpgradablePackage> packages, string command)
    {
        string title = command switch
        {
            "list" => "Aplicativos instalados",
            "update" => "Atualizações disponíveis",
            _ => "Resultados da pesquisa"
        };
        Heading(title);
        if (packages.Count == 0)
        {
            Note(command == "update" ? "Nenhuma atualização encontrada." : "Nenhum resultado encontrado.");
            Note("Total: 0");
            return;
        }
        var table = new Table().Border(TableBorder.None);
        table.AddColumn("[skyblue1]Aplicativo[/]");
        table.AddColumn("[skyblue1]ID[/]");
        table.AddColumn(command == "update" ? "[skyblue1]Instalada[/]" : "[skyblue1]Versão[/]");
        if (command == "update") table.AddColumn("[skyblue1]Disponível[/]");
        table.AddColumn("[skyblue1]Origem[/]");
        static string Cell(string? value) => Markup.Escape(string.IsNullOrWhiteSpace(value) ? "—" : CliApplication.Clean(value).Replace('\n', ' ').Replace('\t', ' '));
        foreach (var package in packages)
        {
            string[] cells = command == "update"
                ? [Cell(package.Name), Cell(package.Id), Cell(package.CurrentVersion), Cell(package.AvailableVersion), Cell(package.SourceLabel)]
                : [Cell(package.Name), Cell(package.Id), Cell(package.CurrentVersion), Cell(package.SourceLabel)];
            table.AddRow(cells);
        }
        _console.Write(table);
        _console.MarkupLine($"  [springgreen2]Total: {packages.Count}[/]");
    }

    public void Details(string details)
    {
        Section("Detalhes do aplicativo", details);
    }

    public void Result(string label, bool success, bool reboot = false)
    {
        label = CliApplication.Clean(label);
        string suffix = reboot ? " Reinicie o Windows para concluir." : "";
        (success ? _console : _errorConsole).MarkupLine($"  [{(success ? "springgreen2" : "red")}]{(success ? "✔" : "✗")}[/] {Markup.Escape(label + suffix)}");
    }
}
