using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinProvision.Store.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OperationTextProviderTests
{
    private static string Resource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, string> Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject().ToDictionary(
            property => property.Name, property => property.Value.GetString() ?? string.Empty, StringComparer.Ordinal);
    }

    [Fact]
    public void EmbeddedPortugueseMatchesGoldenAndEveryProductionKeyIsUsed()
    {
        string embedded = Resource("WinProvision.Store.Assets.Localization.operations.pt-BR.json");
        string golden = Resource("OperationTextTests.operations.pt-BR.golden.json");
        Dictionary<string, string> actual = Parse(embedded);
        Assert.Equal(Parse(golden).OrderBy(item => item.Key), actual.OrderBy(item => item.Key));

        string[] sourceNames =
        [
            "OperationTextTests.OperationsQueuePanel.xaml",
            "OperationTextTests.OperationsQueuePanel.xaml.cs",
            "OperationTextTests.OperationsViewModel.cs",
            "OperationTextTests.PackageBackend.cs",
            "OperationTextTests.OperationItem.cs",
            "OperationTextTests.OperationRunner.cs",
            "OperationTextTests.OperationOutputDialog.cs",
            "OperationTextTests.OperationHistoryPage.xaml.cs",
            "OperationTextTests.App.xaml.cs",
            "WinProvision.Store.Assets.operations-panel.json",
            "OperationMenuTests.OperationActionResolver.cs"
        ];
        string source = string.Join("\n", sourceNames.Select(Resource));
        var used = Regex.Matches(source, "\\\"(operations\\.[A-Za-z0-9_.]+)\\\"")
            .Select(match => match.Groups[1].Value)
            .Where(key => !key.EndsWith(".", StringComparison.Ordinal)
                && key is not ("operations.header.completed" or "operations.header.completed.failed")
                && !key.StartsWith("operations.batch.", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        string[] kinds = ["install", "update", "uninstall", "download"];
        foreach (string kind in kinds)
        {
            foreach (string state in new[] { "running", "queued", "completed", "failed", "canceled" })
                used.Add($"operations.title.{state}.{kind}");
            used.Add($"operations.message.completed.{kind}");
            used.Add($"operations.message.running.{kind}");
            used.Add($"operations.failure.userScopeElevationConflict.{kind}");
            used.Add($"operations.failure.elevationCanceled.{kind}");
            used.Add($"operations.failure.operationCanceled.{kind}");
            used.Add($"operations.failure.internalError.{kind}");
            used.Add($"operations.failure.fallback.{kind}");
        }
        used.Add("operations.header.completed.one");
        used.Add("operations.header.completed.other");
        used.Add("operations.header.completed.failed.one");
        used.Add("operations.header.completed.failed.other");
        used.Add("operations.output.empty");
        used.Add("operations.output.copy");
        used.Add("operations.output.close");
        used.Add("operations.output.title");
        used.Add("operations.output.historyTitle");
        used.Add("operations.batch.title");
        used.Add("operations.batch.completed.one");
        used.Add("operations.batch.completed.other");
        used.Add("operations.batch.failed.one");
        used.Add("operations.batch.failed.other");
        used.Add("operations.batch.canceled.one");
        used.Add("operations.batch.canceled.other");
        Assert.Equal(actual.Keys.Order(StringComparer.Ordinal), used.Order(StringComparer.Ordinal));

        string xaml = Resource(sourceNames[0]);
        string codeBehind = Resource(sourceNames[1]);
        string viewModel = ReadRepositoryFile("src/WinProvision.Store/ViewModels/OperationsViewModel.cs");
        string mainWindowCodeBehind = ReadRepositoryFile("src/WinProvision.Store/MainWindow.xaml.cs");
        string appCode = Resource("OperationTextTests.App.xaml.cs");
        Assert.DoesNotContain("\"Operação concluída\"", appCode, StringComparison.Ordinal);
        Assert.DoesNotContain("concluída(s)", appCode, StringComparison.Ordinal);
        string[] formerLiterals =
        [
            "Operações em massa", "Expandir ou recolher operações", "Mais opções", "Cancelar operação",
            "Tentar novamente as que falharam", "Limpar bem-sucedidas", "Limpar concluídas", "Cancelar tudo",
            "Ver saída do terminal", "Detalhes do pacote", "Opções de instalação", "Abrir local de instalação",
            "Disponível somente para operações de pacote.", "Pasta de instalação não encontrada.",
            "Aguardando o WinGet", "Falha ao remover", "Desinstalação de ", "Operação na fila"
        ];
        foreach (string literal in formerLiterals)
        {
            Assert.DoesNotContain(literal, xaml, StringComparison.Ordinal);
            Assert.DoesNotContain(literal, codeBehind, StringComparison.Ordinal);
            Assert.DoesNotContain(literal, viewModel, StringComparison.Ordinal);
            Assert.DoesNotContain(literal, mainWindowCodeBehind, StringComparison.Ordinal);
        }
        foreach (string menuImplementation in new[]
                 {
                     "FluentIcons.Common.Icon.", "OperationState.", "CanReplay", "CanCancel", "Directory.Exists",
                     "operations.menu.item.", "operations.menu.header.", "operations.tooltip.packageOnly",
                     "folderOpen", "arrowClockwise", "replayOperationVariant:"
                 })
            Assert.DoesNotContain(menuImplementation, codeBehind, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinProvision.Store.slnx")))
            directory = directory.Parent;
        string root = directory?.FullName ?? Directory.GetCurrentDirectory();
        return File.ReadAllText(Path.Combine(root, relativePath));
    }

    [Fact]
    public void UserLanguageOverridesThenFallsBackToPortugueseAndKey()
    {
        var provider = OperationTextProvider.CreateForTests(
            "{\"known\":\"português\",\"onlyDefault\":\"padrão\"}",
            "{\"known\":\"language\"}", "{\"known\":\"user\"}", "en-US");
        Assert.Equal("user", provider.GetText("known"));
        Assert.Equal("padrão", provider.GetText("onlyDefault"));
        Assert.Equal("unknown.key", provider.GetText("unknown.key"));
    }

    [Fact]
    public void MissingLanguageFileAndCorruptUserOverlayUseEmbeddedPortuguese()
    {
        var messages = new List<string>();
        var provider = OperationTextProvider.CreateForTests(
            "{\"label\":\"pt-BR\"}", null, "{", "fr-FR", messages.Add);
        Assert.Equal("pt-BR", provider.GetText("label"));
        Assert.Single(messages);
    }

    [Fact]
    public void PlaceholdersAreSafeAndMissingOrUnknownTokensRemainLiteralAndLogOnce()
    {
        var messages = new List<string>();
        var provider = OperationTextProvider.CreateForTests(
            "{\"line\":\"Nome: {packageName}; {missing}; {unknown}\"}", null, null, "pt-BR", messages.Add);
        var values = new Dictionary<string, string?> { ["packageName"] = "Pkg {x} 100%" };
        Assert.Equal("Nome: Pkg {x} 100%; {missing}; {unknown}", provider.Format("line", values));
        Assert.Equal("Nome: Pkg {x} 100%; {missing}; {unknown}", provider.Format("line", values));
        Assert.Equal(2, messages.Count);
    }

    [Theory]
    [InlineData(0, "other 0")]
    [InlineData(1, "one 1")]
    [InlineData(2, "other 2")]
    public void PluralSelectsOneOnlyForOne(int count, string expected)
    {
        var provider = OperationTextProvider.CreateForTests(
            "{\"count.one\":\"one {completed}\",\"count.other\":\"other {completed}\"}", null, null, "pt-BR");
        Assert.Equal(expected, provider.FormatPlural("count", count));
    }

    [Fact]
    public void ChangingLanguageRaisesChanged()
    {
        var provider = OperationTextProvider.CreateForTests("{\"a\":\"A\"}", null, null, "pt-BR");
        int changes = 0;
        provider.Changed += (_, _) => changes++;
        provider.SetLanguage("pt-BR");
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData("completed", 0, "0 concluídas")]
    [InlineData("failed", 1, "1 falha")]
    [InlineData("canceled", 2, "2 canceladas")]
    public void BatchNotificationLabelsUseLocalizedPluralKeys(string category, int count, string expected)
    {
        string json = Resource("WinProvision.Store.Assets.Localization.operations.pt-BR.json");
        var provider = OperationTextProvider.CreateForTests(json, null, null, "pt-BR");
        Assert.Equal(expected, provider.FormatPlural($"operations.batch.{category}", count));
    }

    [Fact]
    public void InstallCancellationMessageNamesTheUserAsCancellingParty()
    {
        string json = Resource("WinProvision.Store.Assets.Localization.operations.pt-BR.json");
        var provider = OperationTextProvider.CreateForTests(json, null, null, "pt-BR");

        Assert.Equal("A instalação de 7-Zip foi cancelada pelo usuário.",
            provider.Format("operations.failure.operationCanceled.install",
                new Dictionary<string, string?> { ["packageName"] = "7-Zip" }));
    }

    [Fact]
    public void ElevationCancellationMessageDoesNotTellTheUserToApproveAgain()
    {
        string json = Resource("WinProvision.Store.Assets.Localization.operations.pt-BR.json");
        var provider = OperationTextProvider.CreateForTests(json, null, null, "pt-BR");

        Assert.Equal("A solicitação de elevação para instalar 7-Zip foi cancelada.",
            provider.Format("operations.failure.elevationCanceled.install",
                new Dictionary<string, string?> { ["packageName"] = "7-Zip" }));
    }

    [Fact]
    public void UserOverlayCandidateSupportsDeleteAndRejectsMalformedJson()
    {
        var provider = OperationTextProvider.CreateForTests("{\"label\":\"default\"}", null, null, "pt-BR");
        Assert.True(provider.TryBuildUserOverlay("pt-BR", "{\"label\":\"custom\"}", out var custom));
        Assert.Equal("custom", custom["label"]);
        Assert.True(provider.TryBuildUserOverlay("pt-BR", null, out var deleted));
        Assert.Empty(deleted);
        Assert.False(provider.TryBuildUserOverlay("pt-BR", "{", out _));
        Assert.Equal("default", provider.GetText("label"));
    }
}

