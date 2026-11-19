namespace WinProvision.Elevation;

/// <summary>Constrói uma única linha de argumentos para o protocolo textual do fork gsudo fixado.</summary>
public static class GsudoCommandLineBuilder
{
    /// <summary>
    /// Devolve o executável separado (primeiro token do protocolo) e a linha de argumentos do filho.
    /// Os argumentos são citados segundo as regras de argv usadas pelo runtime C do Windows.
    /// </summary>
    public static GsudoChildCommandLine Build(ElevatedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return new GsudoChildCommandLine(command.ExecutablePath, BuildArguments(command.Arguments));
    }

    /// <summary>
    /// Builds the raw helper command line. The pinned fork parses GetCommandLine() itself and
    /// preserves quote characters; passing the child argument text as one ArgumentList item
    /// adds another quoting layer that the fork's splitter cannot undo.
    /// </summary>
    public static string BuildHelperCommandLine(ElevatedCommand command)
    {
        GsudoChildCommandLine child = Build(command);
        var helperCommand = new System.Text.StringBuilder("--direct --loglevel Error ");
        helperCommand.Append(QuoteArgument(child.ExecutablePath));
        if (child.Arguments.Length > 0)
            helperCommand.Append(' ').Append(child.Arguments);
        return helperCommand.ToString();
    }

    private static string BuildArguments(IReadOnlyList<string> arguments)
        => string.Join(' ', arguments.Select(QuoteArgument));

    private static string QuoteArgument(string argument)
    {
        var result = new System.Text.StringBuilder(argument.Length + 2);
        result.Append('"');
        int backslashes = 0;

        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', checked(backslashes * 2 + 1));
                result.Append('"');
            }
            else
            {
                result.Append('\\', backslashes);
                result.Append(character);
            }

            backslashes = 0;
        }

        result.Append('\\', checked(backslashes * 2));
        result.Append('"');
        return result.ToString();
    }
}

public sealed record GsudoChildCommandLine(string ExecutablePath, string Arguments);
