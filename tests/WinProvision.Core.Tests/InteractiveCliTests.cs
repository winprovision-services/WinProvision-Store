using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public class InteractiveCliTests
{
    [Fact]
    public void SearchTableKeepsExactIdentifiersWithMatchColumn()
    {
        var output = $"{ "Name",-30}{ "Id",-35}{ "Version",-16}{ "Match",-16}Source\n" + new string('-', 110) + "\n" +
            $"{ "Google Chrome (EXE)",-30}{ "Google.Chrome.EXE",-35}{ "130.0",-16}{ "Tag: browser",-16}winget\n";
        Assert.True(WingetUpgradeListParser.TryParse(output, out var results, false, true));
        Assert.Single(results);
        Assert.Equal("Google.Chrome.EXE", results[0].Id);
        Assert.Equal("Google Chrome (EXE)", results[0].Name);
    }

    [Fact]
    public void OptionalSeparatorAndMultiwordSearchArePreserved()
    {
        Assert.Equal(("search", "Google Chrome"), InteractiveCliInput.Parse(["--", "search", "Google", "Chrome"]));
        Assert.Equal(("search", "Google Chrome"), InteractiveCliInput.Parse(["search", "Google Chrome"]));
        Assert.Equal(("update", ""), InteractiveCliInput.Parse(["list", "update"]));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4")]
    [InlineData("1; install")]
    [InlineData("")]
    public void InvalidSelectionNeverStartsOperation(string input) => Assert.Null(InteractiveCliInput.Selection(input, 3, false));

    [Fact]
    public void AllOnlyAllowedForUpdatesAndEndOfInputExits()
    {
        Assert.Equal(-1, InteractiveCliInput.Selection(" a ", 3, true));
        Assert.Null(InteractiveCliInput.Selection("A", 3, false));
        Assert.Equal(0, InteractiveCliInput.Selection(null, 3, true));
        Assert.Equal(2, InteractiveCliInput.Selection("2", 3, false));
        Assert.Throws<ArgumentException>(() => InteractiveCliInput.Parse(["/auto"]));
        Assert.Throws<ArgumentException>(() => InteractiveCliInput.Parse(["search", "\n"]));
    }

    [Theory]
    [InlineData("find", "search")]
    [InlineData("upgrade", "update")]
    [InlineData("update", "update")]
    [InlineData("ls", "list")]
    public void AliasesPreserveCanonicalCommands(string command, string expected)
        => Assert.Equal(expected, InteractiveCliInput.ParseRequest([command, "Chrome"]).Command);

    [Fact]
    public void ExactIdAndSourceAreParsedWithoutPassingRawFlags()
    {
        var request = InteractiveCliInput.ParseRequest(["search", "--id=Google.Chrome.EXE", "-s", "WINGET", "-e", "--json"]);
        Assert.Equal("Google.Chrome.EXE", request.Id);
        Assert.Equal("winget", request.Source);
        Assert.True(request.Exact);
        Assert.True(request.Json);
        Assert.Equal("update", InteractiveCliInput.ParseRequest(["list", "--upgrade-available"]).Command);
        Assert.Equal(new InteractiveCliRequest("help", "install"), InteractiveCliInput.ParseRequest(["install", "--help"]));
        Assert.Equal("version", InteractiveCliInput.ParseRequest(["--version"]).Command);
        Assert.Equal("--help", InteractiveCliInput.ParseRequest(["search", "--", "--help"]).Query);
    }

    [Fact]
    public void ShowJsonRequiresExplicitOptInAndDoesNotRelaxOtherValidation()
    {
        Assert.Throws<ArgumentException>(() => InteractiveCliInput.ParseRequest(["show", "--id", "Google.Chrome", "--json"]));
        var request = InteractiveCliInput.ParseRequest(["show", "--id", "Google.Chrome", "--json"], allowShowJson: true);
        Assert.Equal("show", request.Command);
        Assert.True(request.Json);
        Assert.Throws<ArgumentException>(() => InteractiveCliInput.ParseRequest(["install", "--id", "Google.Chrome", "--json"], allowShowJson: true));
        Assert.Throws<ArgumentException>(() => InteractiveCliInput.ParseRequest(["show", "--id", "Google.Chrome", "--all", "--json"], allowShowJson: true));
        Assert.Throws<ArgumentException>(() => InteractiveCliInput.ParseRequest(["show", "--id", "Google.Chrome", "--json", "--json"], allowShowJson: true));
    }

    [Theory]
    [InlineData("install --id Google.Chrome --elevation-cache")]
    [InlineData("upgrade --all --elevation-cache")]
    [InlineData("import apps.json --elevation-cache")]
    [InlineData("install --office --elevation-cache")]
    public void ElevationCacheIsExplicitAndLimitedToOperations(string command)
    {
        var request = InteractiveCliInput.ParseRequest(command.Split(' '));
        Assert.True(request.ElevationCache);
    }

    [Theory]
    [InlineData("install --office --offline")]
    [InlineData("install --office --id Google.Chrome")]
    [InlineData("office --id Google.Chrome")]
    [InlineData("office --output folder")]
    [InlineData("office --download")]
    [InlineData("office install")]
    [InlineData("search Chrome --offline")]
    [InlineData("export --source winget")]
    [InlineData("download --id Google.Chrome --json")]
    [InlineData("list --input apps.json")]
    [InlineData("import apps.json --output installers")]
    [InlineData("install --id Google.Chrome --json")]
    [InlineData("install Google Chrome")]
    [InlineData("upgrade --all --json")]
    [InlineData("upgrade --all --no-input")]
    [InlineData("search Chrome --source arbitrary")]
    [InlineData("search Chrome --yes")]
    [InlineData("install --id Google.Chrome --yes")]
    [InlineData("install --id Google.Chrome -y")]
    [InlineData("uninstall --id Google.Chrome --yes")]
    [InlineData("import apps.json -y")]
    [InlineData("search Chrome --elevation-cache")]
    [InlineData("upgrade --json --elevation-cache")]
    [InlineData("list --elevation-cache")]
    [InlineData("upgrade --id Google.Chrome --json --yes")]
    [InlineData("install --id Google.Chrome --yes -y")]
    [InlineData("search Chrome --souce winget")]
    [InlineData("search Chrome --id Google.Chrome")]
    [InlineData("search Chrome --json=false")]
    [InlineData("list --source winget --source msstore")]
    public void InvalidOrUnattendedRequestsAreRejected(string command)
        => Assert.Throws<ArgumentException>(() => InteractiveCliInput.ParseRequest(command.Split(' ')));
}
