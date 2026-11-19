using System.Text.RegularExpressions;

namespace WinProvision.Core.Services;

/// <summary>Normaliza nomes de pacotes somente para apresentação na fila de operações.</summary>
public static partial class PackageNameNormalizer
{
    private static readonly Regex ArchitectureParenthesis = ArchitectureParenthesisRegex();
    private static readonly Regex ArchitectureSuffix = ArchitectureSuffixRegex();
    private static readonly Regex DuplicateWhitespace = DuplicateWhitespaceRegex();

    public static string ToDisplayName(string? name, string? installedVersion = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? string.Empty;

        var original = name;
        var displayName = name.Trim();
        if (!string.IsNullOrWhiteSpace(installedVersion))
        {
            var version = Regex.Escape(installedVersion.Trim().TrimStart('v', 'V'));
            var versionSuffix = new Regex($@"(?:\s+-\s+|\s+){version}(?=\s*(?:\((?:x64|x86|arm64|64-bit|32-bit|x64 edition)\)|x64|x86|64-bit|32-bit)?\s*$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            displayName = versionSuffix.Replace(displayName, string.Empty, 1);
        }

        displayName = ArchitectureParenthesis.Replace(displayName, string.Empty);
        displayName = ArchitectureSuffix.Replace(displayName, string.Empty);
        displayName = DuplicateWhitespace.Replace(displayName, " ").Trim();
        return displayName.Length < 2 ? original : displayName;
    }

    [GeneratedRegex(@"\s*\((?:x64|x86|arm64|64-bit|32-bit|x64\s+edition)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectureParenthesisRegex();

    [GeneratedRegex(@"\s+(?:x64|x86|64-bit|32-bit)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchitectureSuffixRegex();

    [GeneratedRegex(@"\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicateWhitespaceRegex();
}
