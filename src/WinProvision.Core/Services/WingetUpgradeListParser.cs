using WinProvision.Core.Models;
using WinProvision.Providers.WinGet.Cli.Contracts;
using WinProvision.Providers.WinGet.Cli.Parsing;

namespace WinProvision.Core.Services;

/// <summary>Core adapter that converts provider rows to the shared package domain.</summary>
public static class WingetUpgradeListParser
{
    public static List<UpgradablePackage> Parse(string rawOutput)
    {
        if (!TryParse(rawOutput, out List<UpgradablePackage> result))
            throw new FormatException("A saída do WinGet não corresponde a uma tabela de atualizações reconhecida.");
        return result;
    }

    public static bool TryParse(string rawOutput, out List<UpgradablePackage> packages,
        bool requireAvailableVersion = true) => TryParse(rawOutput, out packages, requireAvailableVersion, false);

    public static bool TryParse(string rawOutput, out List<UpgradablePackage> packages,
        bool requireAvailableVersion, bool allowMissingAvailableColumn, string? sourceIfMissing = null)
    {
        bool parsed = WinGetCliUpgradeTableParser.TryParse(rawOutput, out List<WinGetCliUpgradePackage> rows,
            requireAvailableVersion, allowMissingAvailableColumn, sourceIfMissing);
        packages = rows.Select(row => new UpgradablePackage
        {
            Id = row.Id, Name = row.Name, CurrentVersion = row.CurrentVersion,
            AvailableVersion = row.AvailableVersion, Source = row.Source
        }).ToList();
        return parsed;
    }
}
