using System.Text.RegularExpressions;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class ElevationArchitectureTests
{
    private static readonly IReadOnlyDictionary<string, string> ScriptElevationExceptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/WinProvision.Core/Services/Profile/ScriptSelfElevation.cs"] =
                "Implementação compartilhada da exceção UAC: scripts entregues ao usuário executam fora do processo da Store e não podem acessar IElevatedRunner.",
            ["src/WinProvision.Store/Services/CollectionScriptService.cs"] =
                "Emite o trecho compartilhado nos scripts de coleção entregues ao usuário.",
            ["src/WinProvision.Core/Services/Profile/InstallOffice.ps1"] =
                "Template do script Office entregue ao usuário; recebe o mesmo trecho compartilhado pelo OfflineOfficeModuleBuilder."
        };

    private static readonly Regex RunAsInvocation = new(
        "(?im)(?:\\bStart-Process\\b[^\\r\\n]*?-Verb\\s+RunAs\\b|\\b\\w*Verb\\s*=\\s*['\"]RunAs['\"]|\\brunas(?:\\.exe)?\\b)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void OnlyTheDocumentedStandaloneScriptExceptionUsesRunAs()
    {
        string root = FindRepositoryRoot();
        var violations = EnumerateProductFiles(root)
            .Where(file => !IsElevationModule(file, root))
            .SelectMany(file => RunAsInvocation.Matches(File.ReadAllText(file))
                .Select(match => (Path: Relative(root, file), Match: match.Value)))
            .ToArray();

        Assert.Single(violations);
        Assert.Equal("src/WinProvision.Core/Services/Profile/ScriptSelfElevation.cs", violations[0].Path);
        Assert.Contains("scripts entregues ao usuário", ScriptElevationExceptions[violations[0].Path],
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, ScriptElevationExceptions.Count);

        string collection = File.ReadAllText(Path.Combine(root, "src/WinProvision.Store/Services/CollectionScriptService.cs"));
        string officeTemplate = File.ReadAllText(Path.Combine(root, "src/WinProvision.Core/Services/Profile/InstallOffice.ps1"));
        string officeBuilder = File.ReadAllText(Path.Combine(root, "src/WinProvision.Core/Services/Profile/OfflineOfficeModuleBuilder.cs"));
        string sharedSnippet = File.ReadAllText(Path.Combine(root, "src/WinProvision.Core/Services/Profile/ScriptSelfElevation.cs"));

        Assert.Contains("ScriptSelfElevation.PowerShell", collection, StringComparison.Ordinal);
        Assert.Contains("__SELF_ELEVATION_FUNCTIONS__", officeTemplate, StringComparison.Ordinal);
        Assert.Contains(".Replace(\"__SELF_ELEVATION_FUNCTIONS__\", ScriptSelfElevation.PowerShell", officeBuilder, StringComparison.Ordinal);
        Assert.Contains("if (Test-WinProvisionElevated) { return 0 }", sharedSnippet, StringComparison.Ordinal);
        Assert.Contains("$identity.User.Value -eq 'S-1-5-18'", sharedSnippet, StringComparison.Ordinal);
        Assert.Contains("WindowsBuiltInRole]::Administrator", sharedSnippet, StringComparison.Ordinal);
        Assert.Contains("'-File',$ScriptPath", sharedSnippet, StringComparison.Ordinal);
        Assert.Contains("+ $ArgumentList", sharedSnippet, StringComparison.Ordinal);
        Assert.Contains("ConvertTo-WinProvisionArgument", sharedSnippet, StringComparison.Ordinal);
        Assert.Contains("if ($ElevationRequested -or $SetupComplete)", officeTemplate, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Start-Process -FilePath $script -Verb RunAs")]
    [InlineData("$start.Verb = 'RunAs'")]
    [InlineData("ProcessStartInfo.Verb = \"runas\"")]
    [InlineData("runas.exe program.exe")]
    public void ArchitectureDetectorRecognizesAllForbiddenElevationForms(string source)
        => Assert.NotEmpty(RunAsInvocation.Matches(source));

    [Fact]
    public void LegacyHelperRunnerHasBeenRemovedFromProductionSource()
    {
        string root = FindRepositoryRoot();
        string source = Path.Combine(root, "src");

        Assert.False(File.Exists(Path.Combine(source, "WinProvision.Core/Services/GsudoElevationService.cs")));
        Assert.False(File.Exists(Path.Combine(source, "WinProvision.Core/Services/GsudoBinary.cs")));
        Assert.DoesNotContain(EnumerateProductFiles(root).Where(file => file.StartsWith(source, StringComparison.OrdinalIgnoreCase)),
            file => File.ReadAllText(file).Contains("GsudoElevationService", StringComparison.Ordinal));
    }

    private static IEnumerable<string> EnumerateProductFiles(string root)
    {
        string[] extensions = [".cs", ".ps1", ".psm1", ".psd1", ".cmd", ".bat", ".xaml", ".csproj", ".props", ".targets"];
        foreach (string directory in new[] { "src", "scripts" })
        {
            string path = Path.Combine(root, directory);
            if (!Directory.Exists(path)) continue;
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)) yield return file;
            }
        }
    }

    private static bool IsElevationModule(string file, string root)
        => Relative(root, file).StartsWith("src/WinProvision.Elevation/", StringComparison.OrdinalIgnoreCase);

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "WinProvision.Store.slnx"))) return directory.FullName;
        throw new DirectoryNotFoundException("Não foi possível localizar a raiz do repositório para o teste arquitetural.");
    }
}
