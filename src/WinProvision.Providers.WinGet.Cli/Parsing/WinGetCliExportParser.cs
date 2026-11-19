using System.Text.Json;
using WinProvision.Providers.WinGet.Cli.Contracts;

namespace WinProvision.Providers.WinGet.Cli.Parsing;

public static class WinGetCliExportParser
{
    public static IReadOnlyList<string> ParseInstalledPackageIds(string json)
    {
        if (!TryParseInstalledPackageIds(json, out IReadOnlyList<string> ids))
            throw new JsonException("O arquivo export do WinGet não contém Sources.");
        return ids;
    }

    public static bool TryParseInstalledPackageIds(string json, out IReadOnlyList<string> packageIds)
    {
        WinGetCliExportFile? document = JsonSerializer.Deserialize(json, WinGetCliJsonContext.Default.WinGetCliExportFile);
        if (document?.Sources is null) { packageIds = []; return false; }
        packageIds = document.Sources.Where(source => source.Packages is not null)
            .SelectMany(source => source.Packages!)
            .Select(package => package.PackageIdentifier)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();
        return true;
    }
}
