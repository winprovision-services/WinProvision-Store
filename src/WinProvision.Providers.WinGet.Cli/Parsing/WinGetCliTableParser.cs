using System.Text.RegularExpressions;
using WinProvision.Providers.WinGet.Cli.Contracts;

namespace WinProvision.Providers.WinGet.Cli.Parsing;

/// <summary>Pure text parsers for localized WinGet tables. Column boundaries come from each output header.</summary>
public static partial class WinGetCliTableParser
{
    private static readonly Regex ManifestPathPattern = new(@"rP:\s*(manifests/[A-Za-z0-9._+\-/]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    public static IReadOnlyList<WinGetCliPackageMatch> ParseSearch(string output, string? requestedSource = null)
    {
        string[] lines = NormalizeLines(output);
        int separatorIndex = Array.FindIndex(lines, IsTableSeparator);
        if (separatorIndex < 1) return [];
        int headerIndex = separatorIndex - 1;
        while (headerIndex >= 0 && string.IsNullOrWhiteSpace(lines[headerIndex])) headerIndex--;
        if (headerIndex < 0) return [];
        int[] columnStarts = [.. new[] { lines[headerIndex].TakeWhile(char.IsWhiteSpace).Count() }
            .Concat(Spaces().Matches(lines[headerIndex]).Select(match => match.Index + match.Length))];
        int minimumColumns = string.IsNullOrWhiteSpace(requestedSource) ? 4 : 3;
        if (columnStarts.Length < minimumColumns) return [];
        var results = new List<WinGetCliPackageMatch>();
        foreach (string line in lines[(separatorIndex + 1)..])
        {
            string[] fields = Regex.Split(line.Trim(), @"\s{2,}").Where(field => !string.IsNullOrWhiteSpace(field)).ToArray();
            if (fields.Length < minimumColumns) continue;
            string id = fields[1];
            if (string.IsNullOrWhiteSpace(id) || id.Contains(' ') || id.Contains('…') || id.Contains("...", StringComparison.Ordinal)) continue;
            string source = string.IsNullOrWhiteSpace(requestedSource) && fields.Length >= 4 ? fields[^1] : requestedSource ?? string.Empty;
            results.Add(new(id, fields[0], fields[2], source));
        }
        return results;
    }

    public static string[] ParseVersions(string output)
    {
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        int separator = Array.FindIndex(lines, line => line.Trim().Length >= 3 && line.Trim().All(c => c == '-'));
        if (separator < 0) return [];
        return lines.Skip(separator + 1).Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.Contains(' ') && !line.Contains('\t'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<string> ParseSourceNames(string output)
    {
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim()).Where(line => line.Length > 0).ToArray();
        var sources = new List<string>(lines.Length);
        for (int index = 0; index < lines.Length; index++)
            if (!IsSourceSeparator(lines[index]) && !(index + 1 < lines.Length && IsSourceSeparator(lines[index + 1])))
                sources.Add(lines[index]);
        return sources;
    }

    public static IReadOnlyList<string> ParseManifestPaths(string log) => ManifestPathPattern.Matches(log)
        .Select(match => match.Groups[1].Value.TrimEnd('.', ',', ';'))
        .Where(path => !path.Contains("..", StringComparison.Ordinal)
            && path.Split('/').All(part => part.Length > 0 && part != "." && part != ".."))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> ParseInstallerUrls(string output)
    {
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = rawLine.IndexOf(':');
            if (separator < 0) continue;
            string label = rawLine[..separator].Trim();
            if (!label.Contains("Installer Url", StringComparison.OrdinalIgnoreCase)
                && !label.Equals("InstallerURL", StringComparison.OrdinalIgnoreCase)
                && !label.Equals("URL do instalador", StringComparison.OrdinalIgnoreCase)) continue;
            string url = rawLine[(separator + 1)..].Trim().Trim('"');
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                && uri.Scheme is "http" or "https") urls.Add(uri.AbsoluteUri);
        }
        return urls.ToArray();
    }

    public static WinGetCliPackageDetails ParsePackageDetails(string output)
    {
        string? version = null, publisher = null, author = null, description = null, homepage = null, license = null;
        string? licenseUrl = null, releaseDate = null, installerType = null, installerUrl = null, sha256 = null;
        string? dependencies = null, releaseNotes = null, releaseNotesUrl = null;
        string section = string.Empty;
        var tags = new List<string>();
        foreach (string raw in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.EndsWith(':') && !line.Contains("://", StringComparison.OrdinalIgnoreCase))
            { section = line.TrimEnd(':').Trim(); continue; }
            int colon = line.IndexOf(':');
            if (colon > 0 && !line.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                string key = line[..colon].Trim(), value = line[(colon + 1)..].Trim();
                if (key.Equals("Versão", StringComparison.OrdinalIgnoreCase) || key.Equals("Version", StringComparison.OrdinalIgnoreCase)) version = value;
                else if (key.Equals("Fornecedor", StringComparison.OrdinalIgnoreCase) || key.Equals("Publisher", StringComparison.OrdinalIgnoreCase)) publisher = value;
                else if (key.Equals("Autor", StringComparison.OrdinalIgnoreCase) || key.Equals("Author", StringComparison.OrdinalIgnoreCase)) author = value;
                else if (key.Equals("Descrição", StringComparison.OrdinalIgnoreCase) || key.Equals("Description", StringComparison.OrdinalIgnoreCase)) description = value;
                else if (key.Equals("Página inicial", StringComparison.OrdinalIgnoreCase) || key.Equals("Homepage", StringComparison.OrdinalIgnoreCase)) homepage = value;
                else if (key.Equals("Licença", StringComparison.OrdinalIgnoreCase) || key.Equals("License", StringComparison.OrdinalIgnoreCase)) license = value;
                else if (key.Equals("URL da licença", StringComparison.OrdinalIgnoreCase) || key.Equals("License Url", StringComparison.OrdinalIgnoreCase)) licenseUrl = value;
                else if (key.Equals("Tipo de instalador", StringComparison.OrdinalIgnoreCase) || key.Equals("Installer Type", StringComparison.OrdinalIgnoreCase)) installerType = value;
                else if (key.Contains("URL do instalador", StringComparison.OrdinalIgnoreCase) || key.Contains("Installer Url", StringComparison.OrdinalIgnoreCase)) installerUrl = value;
                else if (key.Contains("SHA256", StringComparison.OrdinalIgnoreCase)) sha256 = value;
                else if (key.Contains("Lançamento", StringComparison.OrdinalIgnoreCase) || key.Contains("Release Date", StringComparison.OrdinalIgnoreCase)) releaseDate = value;
                else if (key.Contains("Dependência", StringComparison.OrdinalIgnoreCase) || key.Contains("Dependencies", StringComparison.OrdinalIgnoreCase)) dependencies = value;
                else if (key.Contains("Notas de lançamento", StringComparison.OrdinalIgnoreCase) || key.Contains("Release Notes", StringComparison.OrdinalIgnoreCase)) releaseNotes = value;
                else if (key.Contains("URL das notas", StringComparison.OrdinalIgnoreCase) || key.Contains("Release Notes Url", StringComparison.OrdinalIgnoreCase)) releaseNotesUrl = value;
            }
            else if ((section.Equals("Marcas", StringComparison.OrdinalIgnoreCase) || section.Equals("Tags", StringComparison.OrdinalIgnoreCase)) && line.Length > 0)
                tags.Add(line);
        }
        return new(version, publisher, author, description, homepage, license, licenseUrl, releaseDate, installerType,
            installerUrl, sha256, dependencies, releaseNotes, releaseNotesUrl, tags.ToArray());
    }

    private static string[] NormalizeLines(string output) => output.Replace("\r\n", "\n", StringComparison.Ordinal)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line[(line.LastIndexOf('\r') + 1)..].TrimEnd('\r'))
        .Where(line => !string.IsNullOrWhiteSpace(line)).ToArray();

    private static bool IsTableSeparator(string line) => Separator().IsMatch(line);
    private static bool IsSourceSeparator(string line)
    {
        bool found = false;
        foreach (char character in line)
        {
            if (character is '-' or '—' or '─' or '═' or '=') found = true;
            else if (!char.IsWhiteSpace(character)) return false;
        }
        return found;
    }
    [GeneratedRegex(@"\s{2,}")] private static partial Regex Spaces();
    [GeneratedRegex(@"^\s*-{3,}\s*$")] private static partial Regex Separator();
}
