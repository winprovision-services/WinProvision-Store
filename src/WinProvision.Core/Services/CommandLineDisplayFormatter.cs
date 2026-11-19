namespace WinProvision.Core.Services;

/// <summary>Formata tokens como argumentos literais copiáveis no PowerShell.</summary>
public static class CommandLineDisplayFormatter
{
    public static string FormatPowerShellArguments(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(QuotePowerShellArgument));

    public static string QuotePowerShellArgument(string value)
    {
        if (value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character)
            || character is '_' or '-' or '.' or '/' or '\\' or ':' or '=' or '+' or ',' or '@'))
            return value;
        return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
    }
}
