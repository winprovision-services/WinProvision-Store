using WinProvision.Core.Models;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WingetUpgradeListParserTests
{
    [Theory]
    [InlineData("steam", "PerfectWorld.SteamChina", "蒸汽平台", "2.10.91.91")]
    [InlineData("chrome", "115.115Chrome", "115浏览器", "36.0.1")]
    public void RealSearchTablesPreserveIdentifiersAfterWideNames(string query, string id, string name, string version)
    {
        using var stream = typeof(WingetUpgradeListParserTests).Assembly.GetManifestResourceStream(
            $"WinProvision.Core.Tests.Fixtures.winget-search-{query}.txt")!;
        using var reader = new StreamReader(stream);
        Assert.True(WingetUpgradeListParser.TryParse(reader.ReadToEnd(), out var packages, false, true));
        var package = Assert.Single(packages, package => package.Id == id);
        Assert.Equal(name, package.Name);
        Assert.Equal(version, package.CurrentVersion);
        Assert.All(packages, item => Assert.Empty(item.AvailableVersion));
        Assert.Contains(packages, item => item.Id == (query == "steam" ? "Valve.Steam" : "Google.Chrome"));
    }

    [Theory]
    [InlineData("中文", 4)]
    [InlineData("한국어", 6)]
    [InlineData("ＡＢ", 4)]
    [InlineData("工具🚀", 6)]
    [InlineData("Cafe\u0301", 4)]
    [InlineData("👨‍👩‍👧‍👦", 2)]
    public void UnicodeNamesRemainAlignedInUpgradeAndInventory(string name, int width)
    {
        string header = $"{"Name",-30}{"Id",-32}{"Version",-12}{"Available",-12}Source";
        string row = name + new string(' ', 30 - width) + $"{"Vendor.App",-32}{"1.0",-12}{"2.0",-12}winget";
        var package = Assert.Single(WingetUpgradeListParser.Parse(header + "\n" + new string('-', 92) + "\n" + row));
        Assert.Equal(name, package.Name);
        Assert.Equal("Vendor.App", package.Id);
        Assert.Equal("1.0", package.CurrentVersion);
        Assert.Equal("2.0", package.AvailableVersion);
        Assert.Equal("winget", package.Source);
    }

    [Theory]
    [InlineData("Name", "Id", "Version", "Available", "Source")]
    [InlineData("Nome", "Identificação", "Versão", "Disponível", "Origem")]
    public void Parse_RecognizesEnglishAndPortugueseUpgradeTables(
        string nameHeader,
        string idHeader,
        string versionHeader,
        string availableHeader,
        string sourceHeader)
    {
        string output = CreateTable(
            nameHeader,
            idHeader,
            versionHeader,
            availableHeader,
            sourceHeader,
            ("Microsoft PowerToys", "Microsoft.PowerToys", "0.80.0", "0.81.0", "winget"));

        var packages = WingetUpgradeListParser.Parse(output);

        UpgradablePackage package = Assert.Single(packages);
        Assert.Equal("Microsoft.PowerToys", package.Id);
        Assert.Equal("Microsoft PowerToys", package.Name);
        Assert.Equal("0.80.0", package.CurrentVersion);
        Assert.Equal("0.81.0", package.AvailableVersion);
        Assert.Equal("winget", package.Source);
    }

    [Theory]
    [InlineData("No applicable upgrade found.")]
    [InlineData("No upgrades available.")]
    [InlineData("Nenhuma atualização aplicável encontrada.")]
    [InlineData("Nenhuma atualização disponível.")]
    [InlineData("Não há atualizações disponíveis.")]
    public void TryParse_RecognizesLocalizedNoUpdateResponses(string output)
    {
        bool parsed = WingetUpgradeListParser.TryParse(output, out var packages);

        Assert.True(parsed);
        Assert.Empty(packages);
    }

    [Theory]
    [InlineData("")]
    [InlineData("WinGet failed to connect to the source.")]
    [InlineData("Name Id Version Available Source\n-----\nMalformed row")]
    [InlineData("Random output\n----------------\nNo package rows")]
    public void TryParse_RejectsEmptyMalformedOrUnrecognizedOutput(string output)
    {
        bool parsed = WingetUpgradeListParser.TryParse(output, out var packages);

        Assert.False(parsed);
        Assert.Empty(packages);
    }

    [Fact]
    public void Parse_ThrowsForUnrecognizedOutputInsteadOfReturningNoUpdates()
    {
        Assert.Throws<FormatException>(() => WingetUpgradeListParser.Parse("Unexpected WinGet output"));
    }

    [Fact]
    public void TryParse_AllowsMissingAvailableVersionForInstalledPackageListings()
    {
        string output = CreateTable(
            "Name",
            "Id",
            "Version",
            "Available",
            "Source",
            ("Visual Studio Code", "Microsoft.VisualStudioCode", "1.90.0", "", "winget"));

        bool parsed = WingetUpgradeListParser.TryParse(
            output,
            out var packages,
            requireAvailableVersion: false);

        Assert.True(parsed);
        UpgradablePackage package = Assert.Single(packages);
        Assert.Equal("Microsoft.VisualStudioCode", package.Id);
        Assert.Equal("1.90.0", package.CurrentVersion);
        Assert.Empty(package.AvailableVersion);
    }

    private static string CreateTable(
        string nameHeader,
        string idHeader,
        string versionHeader,
        string availableHeader,
        string sourceHeader,
        params (string Name, string Id, string Version, string Available, string Source)[] rows)
    {
        string header = $"{nameHeader,-30}{idHeader,-32}{versionHeader,-12}{availableHeader,-12}{sourceHeader}";
        string separator = new('-', header.Length);
        string[] data = rows.Select(row =>
            $"{row.Name,-30}{row.Id,-32}{row.Version,-12}{row.Available,-12}{row.Source}").ToArray();

        return string.Join(Environment.NewLine, new[] { header, separator }.Concat(data));
    }
}
