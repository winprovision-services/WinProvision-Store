namespace WinProvision.PackageManagement;

/// <summary>Tipos aceitos para fontes gerenciador de pacotes personalizadas, em ordem de tentativa.</summary>
internal static class WingetSourceTypeRetry
{
    internal static IReadOnlyList<string> Types { get; } =
        ["Microsoft.PreIndexed.Package", "Microsoft.Rest"];

    internal static bool IsListed(IReadOnlyList<string> sourceLines, string name)
    {
        foreach (string line in sourceLines)
        {
            string candidate = line.Trim();
            if (candidate.Equals(name, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(name + "  ", StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(name + "\t", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
