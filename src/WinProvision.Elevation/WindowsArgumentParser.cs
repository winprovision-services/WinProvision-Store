namespace WinProvision.Elevation;

/// <summary>Converts an existing Windows command line into argv using CommandLineToArgvW-compatible quoting rules.</summary>
public static class WindowsArgumentParser
{
    public static IReadOnlyList<string> Parse(string commandLine)
    {
        ArgumentNullException.ThrowIfNull(commandLine);
        var values = new List<string>();
        int index = 0;
        while (index < commandLine.Length)
        {
            while (index < commandLine.Length && char.IsWhiteSpace(commandLine[index])) index++;
            if (index == commandLine.Length) break;
            var value = new System.Text.StringBuilder();
            bool quoted = false;
            while (index < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[index])))
            {
                int slashes = 0;
                while (index < commandLine.Length && commandLine[index] == '\\') { slashes++; index++; }
                if (index < commandLine.Length && commandLine[index] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) { value.Append('"'); index++; }
                    else
                    {
                        index++;
                        if (quoted && index < commandLine.Length && commandLine[index] == '"') { value.Append('"'); index++; }
                        else quoted = !quoted;
                    }
                }
                else
                {
                    value.Append('\\', slashes);
                    if (index < commandLine.Length && (quoted || !char.IsWhiteSpace(commandLine[index]))) value.Append(commandLine[index++]);
                }
            }
            values.Add(value.ToString());
        }
        return values.AsReadOnly();
    }
}
