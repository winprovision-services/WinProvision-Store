using Spectre.Console;
using WinProvision.Cli;
using Xunit;

namespace WinProvision.Core.Tests;

public class CliConsoleTests
{
    [Fact]
    public async Task RedirectedOutputHasNoAnimationAndEscapesPackageMarkup()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var console = new CliConsole(output, error);
        var completion = new TaskCompletionSource<int>();
        var operation = console.RunAsync("Instalando App [red]", () => completion.Task, default, 1, 3);
        Assert.False(operation.IsCompleted);
        Assert.DoesNotContain("✔", output.ToString());
        completion.SetResult(42);
        Assert.Equal(42, await operation);
        console.Result("App [red] — instalado", true);
        console.Result("App [blue] — falhou", false);
        Assert.Contains("2/3", output.ToString());
        Assert.Contains("App [red] — instalado", output.ToString());
        Assert.Contains("App [blue] — falhou", error.ToString());
        Assert.DoesNotContain('\u001b', output.ToString() + error);
    }

    [Fact]
    public async Task LiveDisplayCleansUpAfterFailureAndCanStartAgain()
    {
        using var output = new StringWriter();
        var terminal = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes
        });
        var console = new CliConsole(output, TextWriter.Null, terminal);
        await Assert.ThrowsAsync<IOException>(() => console.RunAsync<int>("App [red]", () => throw new IOException("teste"), default));
        Assert.Equal(7, await console.RunAsync("Outro app", () => Task.FromResult(7), default));
        Assert.Contains('\u001b', output.ToString());
        Assert.DoesNotContain("✔", output.ToString());
    }

    [Fact]
    public async Task CancellationDoesNotStartOperationOrLeaveLiveDisplayLocked()
    {
        using var output = new StringWriter();
        var terminal = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.Yes, Interactive = InteractionSupport.Yes
        });
        var console = new CliConsole(output, TextWriter.Null, terminal);
        bool started = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => console.RunAsync("App", () =>
        { started = true; return Task.FromResult(0); }, new(true)));
        Assert.False(started);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => console.RunAsync<int>("App", () => Task.FromCanceled<int>(new(true)), default));
        Assert.Equal(1, await console.RunAsync("Novo app", () => Task.FromResult(1), default));
    }
}
