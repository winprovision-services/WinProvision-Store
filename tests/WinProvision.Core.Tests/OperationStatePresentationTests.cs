using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using WinProvision.Core.Models;
using WinProvision.Store.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OperationStatePresentationTests
{
    private static string Resource(string id)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(id)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static double BrushAlpha(string theme, string key)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XDocument dictionary = XDocument.Parse(Resource("OperationStateTests.OperationsQueue.xaml"));
        string themeKey = theme.Equals("light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
        XElement? themeDictionary = dictionary.Descendants(p + "ResourceDictionary")
            .FirstOrDefault(element => (string?)element.Attribute(x + "Key") == themeKey);
        XElement? brush = themeDictionary?.Descendants(p + "SolidColorBrush")
            .FirstOrDefault(element => (string?)element.Attribute(x + "Key") == key)
            ?? dictionary.Descendants(p + "SolidColorBrush").FirstOrDefault(element => (string?)element.Attribute(x + "Key") == key);
        Assert.NotNull(brush);
        string? color = (string?)brush!.Attribute("Color");
        double colorAlpha = color is { Length: 9 } && color[0] == '#' ? Convert.ToByte(color.Substring(1, 2), 16) / 255d : 1d;
        double opacity = (double?)brush.Attribute("Opacity") ?? 1d;
        return colorAlpha * opacity;
    }

    [Fact]
    public void DefaultsMatchStateGoldenForBothThemes()
    {
        using JsonDocument golden = JsonDocument.Parse(Resource("OperationStateTests.operations-states-golden.json"));
        OperationsPanelConfiguration catalog = OperationsPanelCatalog.Create(Resource("WinProvision.Store.Assets.operations-panel.json")).Current;
        foreach (JsonElement item in golden.RootElement.EnumerateArray())
        {
            string name = item.GetProperty("state").GetString()!;
            OperationState state = name switch
            {
                "queued" => OperationState.Queued, "running-indeterminate" or "running-value" => OperationState.Running,
                "completed" => OperationState.Completed, "failed" => OperationState.Failed,
                "canceled" => OperationState.Canceled, _ => OperationState.Unconfirmed
            };
            bool indeterminate = state == OperationState.Queued || name == "running-indeterminate";
            ResolvedStatePresentation actual = OperationStatePresentationResolver.Resolve(state, indeterminate,
                item.GetProperty("theme").GetString() == "light", catalog);
            Assert.Equal(item.GetProperty("iconId").GetString(), actual.IconId);
            Assert.Equal(item.GetProperty("colorToken").GetString(), actual.ColorToken);
            Assert.Equal(item.GetProperty("backgroundAlpha").GetDouble(), BrushAlpha(item.GetProperty("theme").GetString()!, actual.ColorToken), 5);
            Assert.Equal(item.GetProperty("messageColorToken").GetString(), actual.MessageColorToken);
            Assert.Equal(item.GetProperty("trackColorToken").GetString(), actual.TrackColorToken);
            Assert.Equal(item.GetProperty("fillColorToken").GetString(), actual.FillColorToken);
            Assert.Equal(Enum.Parse<OperationProgressMode>(item.GetProperty("progressMode").GetString()!, true), actual.ProgressMode);
            Assert.Equal(item.GetProperty("progressVisible").GetBoolean(), actual.ProgressVisible);
            Assert.Equal(item.GetProperty("messageFont").GetString(), actual.MessageFontToken);
            Assert.Equal(item.GetProperty("messageFontWeight").GetString(), actual.MessageFontWeight);
            Assert.Equal(item.GetProperty("messageAlpha").GetDouble(), actual.MessageAlpha);
            Assert.Equal(item.GetProperty("messageFontFamily").GetString(), catalog.Theme.MessageFontFamily);
            Assert.Equal(item.GetProperty("primaryButton").GetString(),
                state is OperationState.Queued or OperationState.Running ? "operations.button.cancel" : "operations.button.close");
            Assert.Equal(0d, item.GetProperty("trackAlpha").GetDouble());
            Assert.Equal(item.GetProperty("trackAlpha").GetDouble(), BrushAlpha(item.GetProperty("theme").GetString()!, actual.TrackColorToken), 5);
            Assert.Equal(1d, item.GetProperty("fillAlpha").GetDouble());
            Assert.Equal(item.GetProperty("fillAlpha").GetDouble(), BrushAlpha(item.GetProperty("theme").GetString()!, actual.FillColorToken), 5);
            Assert.Equal(item.GetProperty("fillBrush").GetString(), actual.FillColorToken);
        }
    }

    [Fact]
    public void OverlayAppliesAllowlistedStateFieldsAndRejectsInvalidEntriesIndependently()
    {
        const string overlay = """
            {"schemaVersion":2,"states":{
              "running":{"iconId":"arrowClockwise","colorToken":"AppOperationSuccessSurfaceBrush","backgroundAlpha":0.25,"messageFont":"messageMonospace","progress":{"mode":"value","visible":false}},
              "failed":{"iconId":"unknown","colorToken":"notABrush","backgroundAlpha":2,"progress":{"mode":"complete"}},
              "canceled":{"colorToken":"AppOperationFailureSurfaceBrush"},
              "notAState":{"iconId":"apps"}}}
            """;
        List<string> warnings = [];
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(Resource("WinProvision.Store.Assets.operations-panel.json"), overlay,
            (exception, _) => warnings.Add(exception.Message)).Current;
        var running = config.States["running"];
        Assert.Equal("arrowClockwise", running.IconId);
        Assert.Equal("AppOperationSuccessSurfaceBrush", running.ColorToken);
        Assert.Equal(.25, running.BackgroundAlpha);
        Assert.Equal("messageMonospace", running.MessageFontToken);
        Assert.Equal("value", running.ProgressMode);
        Assert.False(running.ProgressVisible);
        Assert.Equal(OperationProgressMode.Indeterminate, OperationStatePresentationResolver.Resolve(OperationState.Running, true, false, config).ProgressMode);
        Assert.Equal(OperationProgressMode.Value, OperationStatePresentationResolver.Resolve(OperationState.Running, false, false, config).ProgressMode);
        Assert.Equal("apps", config.States["failed"].IconId);
        Assert.Equal("AppOperationFailureSurfaceBrush", config.States["failed"].ColorToken);
        Assert.Equal("AppOperationCanceledSurfaceBrush", config.States["canceled"].ColorToken);
        Assert.NotEmpty(warnings);
    }

    [Fact]
    public void CatalogTokensAndManagerIconsAreClosedAllowLists()
    {
        OperationsPanelConfiguration catalog = OperationsPanelCatalog.Create(Resource("WinProvision.Store.Assets.operations-panel.json")).Current;
        foreach (OperationStatePresentationDefinition definition in catalog.States.Values)
        {
            Assert.Contains(definition.IconId, OperationStatePresentationCatalog.IconIds);
            Assert.Contains(definition.ColorToken, OperationStatePresentationCatalog.ColorTokens);
            Assert.Contains(definition.MessageColorToken, OperationStatePresentationCatalog.ColorTokens);
            Assert.Contains(definition.TrackColorToken, OperationStatePresentationCatalog.ColorTokens);
            Assert.Contains(definition.FillColorToken, OperationStatePresentationCatalog.ColorTokens);
        }
        Assert.All(catalog.ManagerIcons.Values, icon => Assert.Contains(icon, OperationStatePresentationCatalog.ManagerIconIds));
    }

    [Fact]
    public void UiAndViewModelDoNotHardcodeStateBrushesAlphasOrIcons()
    {
        string ui = Resource("OperationTextTests.OperationsQueuePanel.xaml.cs");
        string viewModel = Resource("OperationTextTests.OperationsViewModel.cs");
        string sources = ui + viewModel;
        foreach (string literal in new[]
                 {
                     "OperationNeutralBackgroundBrush", "OperationAttentionBackgroundBrush", "OperationDescriptionBackgroundBrush",
                     "AppOperationSuccessSurfaceBrush", "AppOperationFailureSurfaceBrush", "AppOperationCanceledSurfaceBrush",
                     "AppOperationSuccessBrush", "AppOperationFailureBrush", "AppOperationCanceledBrush",
                     "managerWinget", "managerChocolatey", "managerOffice", "messageMonospace", "apps", "0.07", "0.10", ".07", ".10"
                 })
            Assert.DoesNotContain(literal, sources, StringComparison.Ordinal);
    }
}
