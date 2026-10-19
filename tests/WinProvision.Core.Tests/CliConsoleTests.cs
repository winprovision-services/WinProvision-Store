using Spectre.Console;
using WinProvision.Cli;
using Xunit;

namespace WinProvision.Core.Tests;

public class CliConsoleTests
{
    [Theory]
    [InlineData(48)]
    [InlineData(100)]
    public void SharedSectionsPreserveContentAtDifferentTerminalWidths(int width)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var terminal = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(output), Ansi = AnsiSupport.No, Interactive = InteractionSupport.No
        });
        terminal.Profile.Width = width;
        var console = new CliConsole(output, error, terminal);
        console.Section("Ajuda [teste]", CliApplication.Help("uninstall"));
        console.Help(CliApplication.Help(""));
        console.Heading("Importação");
        console.Note("2 pacote(s) para instalar:");
        console.Summary(1, 1, 0);
        console.Result("App [red] — falhou", false);
        console.Cancelled();
        Assert.Contains("Ajuda [teste]", output.ToString());
        Assert.Contains("Resumo", output.ToString());
        Assert.Contains("--elevation-cache", output.ToString());
        Assert.DoesNotContain("App [red]", output.ToString());
        Assert.Contains("App [red] — falhou", error.ToString());
        Assert.Contains("Operação cancelada.", error.ToString());
        Assert.DoesNotContain('\u001b', output.ToString() + error);
        Assert.DoesNotContain("Confirmar", output.ToString());
    }

    [Fact]
    public async Task CancelledQueryCannotStartOrWriteResults()
    {
        using var output = new StringWriter();
        var console = new CliConsole(output, TextWriter.Null);
        bool started = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => console.QueryAsync("Consultando...", () =>
        {
            started = true;
            return Task.FromResult(0);
        }, new(true)));
        Assert.False(started);
        Assert.Equal("", output.ToString());
    }

    [Fact]
    public void QueriesShareColumnsAndEscapeUntrustedTextWithoutAnsi()
    {
        using var output = new StringWriter();
        var console = new CliConsole(output, TextWriter.Null);
        console.Packages(new List<WinProvision.Core.Models.UpgradablePackage> { new() { Id = "Test.App", Name = "App [red]", CurrentVersion = "1", AvailableVersion = "2", Source = "winget" } }, "update");
        console.Details("Editor: [blue]\nVersão: 2");
        var text = output.ToString();
        Assert.Contains("Atualizações disponíveis", text);
        Assert.Contains("Instalada", text);
        Assert.Contains("Disponível", text);
        Assert.Contains("Test.App", text);
        Assert.Contains("App [red]", text);
        Assert.Contains("Editor: [blue]", text);
        Assert.Contains("Total: 1", text);
        Assert.DoesNotContain('\u001b', text);
    }

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
        // CI capability detection deliberately disables interaction; this test supplies a virtual terminal.
        terminal.Profile.Capabilities.Interactive = true;
        terminal.Profile.Capabilities.Ansi = true;
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
        // CI capability detection deliberately disables interaction; this test supplies a virtual terminal.
        terminal.Profile.Capabilities.Interactive = true;
        terminal.Profile.Capabilities.Ansi = true;
        var console = new CliConsole(output, TextWriter.Null, terminal);
        bool started = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => console.RunAsync("App", () =>
        { started = true; return Task.FromResult(0); }, new(true)));
        Assert.False(started);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => console.RunAsync<int>("App", () => Task.FromCanceled<int>(new(true)), default));
        Assert.Equal(1, await console.RunAsync("Novo app", () => Task.FromResult(1), default));
    }
}
