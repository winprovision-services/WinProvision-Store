using WinProvision.Core.Models;
using WinProvision.Store.Services;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ManualInstallCommandBuilderTests
{
    [Fact]
    public void ChocolateyInstallCreatesStandaloneCommand()
    {
        string command = ManualInstallCommandBuilder.BuildInstallCommand(
            new AppEntry { Id = "vlc", Name = "VLC", Source = "chocolatey" }, null);

        Assert.Equal("choco.exe install vlc -y --no-progress", command);
    }

    [Fact]
    public void ChocolateyInstallIncludesSupportedOptionsAndQuotesCustomArguments()
    {
        var options = new PackageInstallOptions(
            Interactive: true,
            Architecture: "x86",
            IgnoreSecurityHash: true,
            Version: "3.0.20",
            CustomArguments: "--params \"/S /v/qn\"");

        string command = ManualInstallCommandBuilder.BuildInstallCommand(
            new AppEntry { Id = "vlc", Name = "VLC", Source = "chocolatey" }, options);

        Assert.Equal(
            "choco.exe install vlc -y --no-progress --notsilent --forcex86 --ignore-checksum --version 3.0.20 --allow-downgrade --params '/S /v/qn'",
            command);
    }

    [Fact]
    public void ChocolateyInstallKeepsCustomShellCharactersAsOneLiteralArgument()
    {
        string command = ManualInstallCommandBuilder.BuildInstallCommand(
            new AppEntry { Id = "vlc", Name = "VLC", Source = "chocolatey" },
            new PackageInstallOptions(CustomArguments: "--note \"$env:PATH; echo no\""));

        Assert.Contains("--note", command, StringComparison.Ordinal);
        Assert.Contains("$env:PATH; echo no", command, StringComparison.Ordinal);
    }

    [Fact]
    public void ChocolateyInstallRejectsInvalidIdentifiersAndUnbalancedArguments()
    {
        Assert.Throws<ArgumentException>(() => ManualInstallCommandBuilder.BuildInstallCommand(
            new AppEntry { Id = "vlc; Write-Host bad", Name = "VLC", Source = "chocolatey" }, null));
        Assert.Throws<InvalidOperationException>(() => ManualInstallCommandBuilder.BuildInstallCommand(
            new AppEntry { Id = "vlc", Name = "VLC", Source = "chocolatey" },
            new PackageInstallOptions(CustomArguments: "--params \"/S")));
    }

    [Fact]
    public void WinGetManualCommandRemainsUnchanged()
    {
        string command = ManualInstallCommandBuilder.BuildInstallCommand(
            new AppEntry { Id = "Microsoft.VisualStudioCode", Name = "VS Code", Source = "winget" }, null);

        Assert.Equal(WingetCommandLineBuilder.BuildCommand("install", "Microsoft.VisualStudioCode", "winget"), command);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("upgrade")]
    [InlineData("uninstall")]
    public void WinGetPreviewAndExecutionUseTheSameArguments(string operation)
    {
        var options = new PackageInstallOptions(Interactive: true, Scope: "machine", Architecture: "x64",
            IgnoreSecurityHash: true, InstallLocation: @"C:\Apps\Demo", Version: operation == "uninstall" ? "1.0" : "2.4.1",
            CustomArguments: "--silent", OverrideArguments: "/S", UninstallPrevious: true);
        var app = new AppEntry { Id = "Contoso.Demo", Name = "Demo", Source = "winget" };
        string preview = ManualInstallCommandBuilder.BuildInstallCommand(app, options, operation);
        string executionArguments = WingetCommandLineBuilder.BuildArguments(operation, app.Id, app.Source, options,
            installedVersion: operation == "uninstall" ? "1.0" : null);

        Assert.Equal("winget " + executionArguments, preview);
        Assert.Contains("--source \"winget\"", preview, StringComparison.Ordinal);
        if (operation is "install" or "upgrade") Assert.Contains("--version \"2.4.1\"", preview, StringComparison.Ordinal);
        else Assert.Contains("--version \"1.0\"", preview, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("install")]
    [InlineData("upgrade")]
    [InlineData("uninstall")]
    public void ChocolateyPreviewAndExecutionUseTheSameArguments(string operation)
    {
        var options = new PackageInstallOptions(Interactive: true, Architecture: "x86", IgnoreSecurityHash: true,
            Version: "2.4.1", CustomArguments: "--params \"/S /v/qn\"");
        string preview = ChocolateyCommandLineBuilder.BuildPowerShellPreview(operation, "vlc", options);
        string[] executionArguments = ChocolateyCommandLineBuilder.BuildArguments(operation, "vlc", options);

        Assert.Equal("choco.exe " + CommandLineDisplayFormatter.FormatPowerShellArguments(executionArguments), preview);
        if (operation is "install" or "upgrade") Assert.Contains("--version 2.4.1", preview, StringComparison.Ordinal);
        else Assert.Contains("--version 2.4.1", preview, StringComparison.Ordinal);
    }
}
