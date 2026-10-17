using Spectre.Console;

namespace WinProvision.Cli;

/// <summary>Owns terminal rendering; package services never write to the live display.</summary>
public sealed class CliConsole
{
    private readonly IAnsiConsole _console;
    private readonly TextWriter _error;
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
        _error = error;
        _animate = _console.Profile.Capabilities.Ansi && _console.Profile.Capabilities.Interactive;
    }

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

    public void Result(string label, bool success, bool reboot = false)
    {
        label = CliApplication.Clean(label);
        string suffix = reboot ? " Reinicie o Windows para concluir." : "";
        if (!success && !_animate)
        {
            _error.WriteLine($"  ✗ {label}{suffix}");
            return;
        }
        _console.MarkupLine($"  [{(success ? "springgreen2" : "red")}]{(success ? "✔" : "✗")}[/] {Markup.Escape(label + suffix)}");
    }
}
