namespace WinProvision.Core.Services;

/// <summary>Maps only COM installer names whose equivalent is listed by the WinGet CLI contract.</summary>
public static class WinGetInstallerTypeArgumentMapper
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    { "burn", "exe", "font", "inno", "msi", "msstore", "nullsoft", "portable", "wix", "zip" };

    public static string? ToCliValue(string? comInstallerType)
    {
        if (string.IsNullOrWhiteSpace(comInstallerType)) return null;
        string normalized = comInstallerType.Trim().ToLowerInvariant();
        return Supported.Contains(normalized) ? normalized : null;
    }
}
