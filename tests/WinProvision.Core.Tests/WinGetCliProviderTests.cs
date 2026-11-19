using System.Text.Json;
using WinProvision.Providers.WinGet.Cli.Client;
using WinProvision.Providers.WinGet.Cli.Commands;
using WinProvision.Providers.WinGet.Cli.Contracts;
using WinProvision.Providers.WinGet.Cli.Parsing;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WinGetCliProviderTests
{
    [Fact]
    public void InstallArgumentsPreserveCurrentSourceAndSilentFlagsAsTokens()
    {
        IReadOnlyList<string> actual = WinGetCliCommandBuilder.BuildMutation(WinGetCliOperation.Install,
            "Vendor.App", "winget", new(Interactive: false, Scope: "machine", InstallLocation: "C:\\Apps With Space"));
        Assert.Equal(new[] { "install", "--id", "Vendor.App", "--exact", "--source", "winget",
            "--accept-source-agreements", "--disable-interactivity", "--silent", "--scope", "machine",
            "--location", "C:\\Apps With Space", "--accept-package-agreements" }, actual);
    }

    [Fact]
    public void StoreInstallRetainsInteractiveException()
    {
        IReadOnlyList<string> actual = WinGetCliCommandBuilder.BuildMutation(WinGetCliOperation.Install,
            "9NBLGGH4NNS1", "msstore", new(Interactive: true));
        Assert.Contains("--interactive", actual);
        Assert.DoesNotContain("--disable-interactivity", actual);
        Assert.DoesNotContain("--silent", actual);
    }

    [Fact]
    public void UninstallCanOmitSourceAndUsesInstalledVersion()
    {
        IReadOnlyList<string> actual = WinGetCliCommandBuilder.BuildMutation(WinGetCliOperation.Uninstall,
            "Vendor.App", "winget", new(), installedVersion: "2.0", includeSource: false);
        Assert.Equal(new[] { "uninstall", "--id", "Vendor.App", "--exact", "--accept-source-agreements",
            "--disable-interactivity", "--version", "2.0", "--silent" }, actual);
    }

    [Fact]
    public void UpgradeFlagsAndArgumentValuesRemainSeparate()
    {
        IReadOnlyList<string> actual = WinGetCliCommandBuilder.BuildMutation(WinGetCliOperation.Upgrade,
            "Vendor.App", null, new(Version: "3.1", UpdateArguments: "/quiet /norestart", UninstallPrevious: true), force: true);
        Assert.Contains("--include-unknown", actual);
        Assert.Contains("--force", actual);
        Assert.Equal("/quiet /norestart", actual[Array.IndexOf(actual.ToArray(), "--custom") + 1]);
        Assert.Contains("--uninstall-previous", actual);
    }

    [Theory]
    [InlineData("Buscando 50%", WinGetCliPhase.Searching, 50d)]
    [InlineData("Downloading 7,5%", WinGetCliPhase.Downloading, 7.5d)]
    [InlineData("Verificando SHA256…", WinGetCliPhase.Verifying, null)]
    [InlineData("Instalando…", WinGetCliPhase.Installing, null)]
    public void ProgressParsingRemovesControlCharactersAndRecognizesPhase(string line, WinGetCliPhase phase, double? percentage)
    {
        WinGetCliEvent parsed = WinGetCliParsing.ParseProgressLine("\u001b[32m" + line + "\r");
        Assert.Equal(phase, parsed.Phase);
        Assert.Equal(percentage, parsed.Percentage);
        Assert.DoesNotContain('\u001b', parsed.Line);
        Assert.DoesNotContain('\r', parsed.Line);
    }

    [Fact]
    public void OutputSummaryKeepsBothEndsUnderTheBound()
    {
        string raw = string.Concat(new string('a', WinGetCliParsing.RawOutputLimit), new string('尾', WinGetCliParsing.RawOutputLimit));
        string summary = WinGetCliParsing.BoundSummary(raw);
        Assert.True(summary.Length <= WinGetCliParsing.RawOutputLimit);
        Assert.StartsWith("aaaa", summary);
        Assert.EndsWith("尾尾尾尾", summary);
        Assert.Contains("saída truncada", summary);
    }

    [Fact]
    public void ResultContractRoundTripsWithSourceGeneratedJson()
    {
        var result = new WinGetCliResult(WinGetCliStatus.UacCancelled, null, WinGetCliExitCodeOrigin.None,
            null, false, false, false, WinGetCliPhase.None, "", null, "cancelled", "", "");
        string json = JsonSerializer.Serialize(result, WinGetCliJsonContext.Default.WinGetCliResult);
        WinGetCliResult? restored = JsonSerializer.Deserialize(json, WinGetCliJsonContext.Default.WinGetCliResult);
        Assert.Equal(result, restored);
    }

    [Theory]
    [InlineData("Nome           Id              Versão  Disponível   Origem\n------------------------------------------------------------\nAplicativo     Vendor.App      1.0     2.0          winget\n", "Vendor.App", "2.0")]
    [InlineData("Name           Id              Version Available   Source\n------------------------------------------------------------\n東京工具       Vendor.CJK      1.0     2.0          winget\n", "Vendor.CJK", "2.0")]
    public void UpgradeTableParserUsesRowsAndKeepsUnicodeNames(string output, string id, string available)
    {
        Assert.True(WinGetCliUpgradeTableParser.TryParse(output, out var rows));
        var row = Assert.Single(rows);
        Assert.Equal(id, row.Id);
        Assert.Equal(available, row.AvailableVersion);
    }

    [Fact]
    public void ExportJsonParserUsesSourceGeneratedContract()
    {
        const string json = "{\"$schema\":\"https://aka.ms/winget-packages.schema.2.0.json\",\"Sources\":[{\"Packages\":[{\"PackageIdentifier\":\"Vendor.App\",\"Version\":\"2.1\"}]}]}";
        Assert.Equal(new[] { "Vendor.App" }, WinGetCliExportParser.ParseInstalledPackageIds(json));
        Assert.False(WinGetCliExportParser.TryParseInstalledPackageIds("{}", out _));
    }

    [Fact]
    public void ProviderIsIsolatedAndOnlyElevatesThroughTheApprovedContract()
    {
        string root = FindRepositoryRoot();
        string provider = Path.Combine(root, "src", "WinProvision.Providers.WinGet.Cli");
        string source = string.Join("\n", Directory.EnumerateFiles(provider, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText));
        string project = File.ReadAllText(Path.Combine(provider, "WinProvision.Providers.WinGet.Cli.csproj"));
        Assert.DoesNotContain("Microsoft.Management.Deployment", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WinProvision.Core", source, StringComparison.Ordinal);
        Assert.DoesNotContain("gsudo", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Verb = \"runas\"", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WinProvision.Core.csproj", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WinProvision.Elevation.csproj", project, StringComparison.OrdinalIgnoreCase);

        string[] exposed = typeof(WinGetCliClient).Assembly.GetExportedTypes()
            .SelectMany(type => type.GetMembers().SelectMany(member => member switch
            {
                System.Reflection.MethodInfo method => new[] { method.ReturnType }.Concat(method.GetParameters().Select(parameter => parameter.ParameterType)),
                System.Reflection.PropertyInfo property => (Type[])[property.PropertyType],
                _ => Array.Empty<Type>()
            }))
            .Where(type => type.FullName?.StartsWith("System.Diagnostics.", StringComparison.Ordinal) == true)
            .Select(type => type.FullName!).ToArray();
        Assert.Empty(exposed);
    }

    [Fact]
    public void InProcessWingetLaunchesOutsideProviderAreGone()
    {
        string root = FindRepositoryRoot();
        string source = Path.Combine(root, "src");
        var violations = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(source, path).StartsWith("WinProvision.Providers.WinGet.Cli" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("FileName = WingetLocator.ExecutablePath", StringComparison.Ordinal)
                || File.ReadAllText(path).Contains("RunElevatedAsync(WingetLocator.ExecutablePath", StringComparison.Ordinal))
            .Select(path => Path.GetRelativePath(root, path)).ToArray();
        Assert.Empty(violations);
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "WinProvision.Store.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate WinProvision.Store.slnx from the test output directory.");
    }
}
