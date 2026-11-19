using System.Reflection;
using WinProvision.Core.Models;
using WinProvision.Store.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OperationsPanelCatalogTests
{
    private const string LegacyDefaults = """
        {
          "schemaVersion": 1,
          "metrics": { "rowHeight":48, "rowCornerRadius":8, "rowSpacing":8, "iconSize":32,
            "progressHeight":2.5, "panelMinHeight":120, "panelMaxHeight":480, "panelDefaultHeight":240,
            "titleMaxWidthFraction":0.4, "titleMaxWidth":260, "titleMinWidth":96, "descriptionMinWidth":150,
            "descriptionHeight":32, "descriptionCornerRadius":5, "descriptionPaddingHorizontal":8,
            "descriptionPaddingTop":7, "descriptionPaddingBottom":4, "descriptionFontSize":14,
            "descriptionProgressTrackHeight":12, "descriptionBackgroundAlpha":0.18,
            "descriptionProgressTrackAlpha":0.10, "iconColumnWidth":40, "actionButtonWidth":110,
            "moreButtonWidth":36, "actionButtonHeight":32, "flexibleColumnSpacing":12,
            "rowHorizontalPadding":8, "rowVerticalPadding":8, "titleFontSize":14, "actionFontSize":14,
            "descriptionTopTextOffset":7, "rowIconHeight":24 },
          "timing": { "autoCloseSeconds":5, "expandAnimationMs":160, "hoverPausesAutoClose":true },
          "states": {}, "actions": {}, "behavior": {}
        }
        """;

    private static string EmbeddedDefaults()
    {
        using Stream stream = typeof(OperationsPanelCatalogTests).Assembly
            .GetManifestResourceStream("WinProvision.Store.Assets.operations-panel.json")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void EmbeddedDefaults_ReproduceCurrentPanelMetrics()
    {
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults()).Current;
        Assert.Equal(16, config.Actions?.Count);
        Assert.Equal(4, config.Theme.RowCornerRadius);
        Assert.Equal(4, config.Theme.MessageCornerRadius);
        Assert.Equal(4, config.Theme.ProgressCornerRadius);
        Assert.Equal(48, config.Layout.RowHeight);
        Assert.Equal(32, config.Layout.DescriptionHeight);
        Assert.Equal(2.5, config.Theme.ProgressHeight);
        Assert.Equal(12, config.Theme.ProgressTrackHeight);
        Assert.Equal(8, config.Theme.MessagePaddingHorizontal);
        Assert.Equal(7, config.Theme.MessagePaddingTop);
        Assert.Equal(4, config.Theme.MessagePaddingBottom);
        Assert.Equal(8, config.Layout.RowSpacing);
        Assert.Equal(4, config.Layout.RowMarginVertical);
        Assert.Equal(40, config.Layout.IconColumnWidth);
        Assert.Equal(110, config.Layout.ActionButtonWidth);
        Assert.Equal(36, config.Layout.MoreButtonWidth);
        Assert.Equal(.07, config.Theme.NeutralBackgroundAlpha);
        Assert.Equal(.10, config.Theme.AttentionBackgroundAlpha);
        Assert.Equal(.18, config.Theme.MessageBackgroundAlpha);
        Assert.Equal("Segoe UI Variable Text, Segoe UI", config.Theme.MessageFontFamily);
        Assert.Equal(5, config.Behavior.AutoCloseDelaySeconds);
        Assert.True(config.Behavior.PauseOnPointer && config.Behavior.PauseOnFocus && config.Behavior.PauseOnPopup);
        Assert.Equal(.45, config.Behavior.HeightMaxFractionOfContent);
        Assert.Equal(240, config.Behavior.HeightDefault);
        Assert.Equal(120, config.Behavior.HeightMin);
        Assert.Equal(480, config.Behavior.HeightMax);
        Assert.True(config.Behavior.DefaultExpanded);
        Assert.Equal(OperationOrderingMode.PendingFirst, config.Behavior.OrderingMode);
    }

    [Fact]
    public void EmbeddedPanelJsonSupportsDeterminateAndIndeterminateProviderProgress()
    {
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults()).Current;
        Assert.Equal(OperationProgressMode.Indeterminate,
            OperationStatePresentationCatalog.Resolve(OperationState.Running, true, false, config).ProgressMode);
        Assert.Equal(OperationProgressMode.Value,
            OperationStatePresentationCatalog.Resolve(OperationState.Running, false, false, config).ProgressMode);
        Assert.Equal(OperationProgressMode.Complete,
            OperationStatePresentationCatalog.Resolve(OperationState.Completed, false, false, config).ProgressMode);
    }

    [Fact]
    public void Schema2Overlay_AppliesValidValuesAndKeepsMissingDefaults()
    {
        const string overlay = """
            { "schemaVersion":2, "layout":{"rowHeight":64}, "theme":{"rowCornerRadius":9}, "metrics":{"progressHeight":3.5} }
            """;
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults(), overlay).Current;
        Assert.Equal(64, config.Layout.RowHeight);
        Assert.Equal(9, config.Theme.RowCornerRadius);
        Assert.Equal(3.5, config.Theme.ProgressHeight);
        Assert.Equal(4, config.Theme.MessageCornerRadius);
        Assert.Equal(8, config.Layout.RowSpacing);
    }

    [Fact]
    public void InvalidField_FallsBackIndependentlyAndLogsWarning()
    {
        var warnings = new List<string>();
        const string overlay = """
            { "schemaVersion":2, "layout":{"rowHeight":72}, "metrics":{"progressHeight":99} }
            """;
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults(), overlay,
            (_, context) => warnings.Add(context)).Current;
        Assert.Equal(72, config.Layout.RowHeight);
        Assert.Equal(2.5, config.Theme.ProgressHeight);
        Assert.Contains("OperationsPanel.UserConfig", warnings);
    }

    [Fact]
    public void Schema1Overlay_MapsKnownFieldsAndIgnoresObsoleteRadii()
    {
        var warnings = new List<string>();
        const string overlay = """
            { "schemaVersion":1,
              "metrics":{"rowHeight":60,"rowCornerRadius":8,"descriptionCornerRadius":5,"actionButtonWidth":125},
              "timing":{"autoCloseSeconds":9}, "states":{}, "actions":{}, "behavior":{} }
            """;
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults(), overlay,
            (exception, context) => warnings.Add(exception.Message + "|" + context)).Current;
        Assert.Equal(60, config.Layout.RowHeight);
        Assert.Equal(125, config.Layout.ActionButtonWidth);
        Assert.Equal(4, config.Theme.RowCornerRadius);
        Assert.Equal(4, config.Theme.MessageCornerRadius);
        Assert.Equal(9, config.Behavior.AutoCloseDelaySeconds);
        Assert.Contains(warnings, warning => warning.Contains("rowCornerRadius", StringComparison.Ordinal));
        Assert.Contains(warnings, warning => warning.Contains("descriptionCornerRadius", StringComparison.Ordinal));
    }

    [Fact]
    public void BehaviorOverlay_MergesFieldsIndependentlyAndUsesBounds()
    {
        const string overlay = """
            { "schemaVersion":2, "behavior": {
              "autoClose":{"delaySeconds":10,"pauseOnPointer":false,"pauseOnFocus":true,"pauseOnPopup":false},
              "height":{"default":300,"min":100,"max":600,"maxFractionOfContent":0.7},
              "defaultExpanded":false,"ordering":{"mode":"queueOrder"} } }
            """;
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults(), overlay).Current;
        Assert.Equal(10, config.Behavior.AutoCloseDelaySeconds);
        Assert.False(config.Behavior.PauseOnPointer);
        Assert.True(config.Behavior.PauseOnFocus);
        Assert.False(config.Behavior.PauseOnPopup);
        Assert.Equal(300, config.Behavior.HeightDefault);
        Assert.Equal(100, config.Behavior.HeightMin);
        Assert.Equal(600, config.Behavior.HeightMax);
        Assert.Equal(.7, config.Behavior.HeightMaxFractionOfContent);
        Assert.False(config.Behavior.DefaultExpanded);
        Assert.Equal(OperationOrderingMode.QueueOrder, config.Behavior.OrderingMode);
        Assert.Equal(300, config.Layout.PanelDefaultHeight);
        Assert.Equal(100, config.Layout.PanelMinHeight);
        Assert.Equal(600, config.Layout.PanelMaxHeight);
    }

    [Fact]
    public void InvalidBehaviorFields_FallBackIndependentlyAndLog()
    {
        var warnings = new List<string>();
        const string overlay = """
            { "schemaVersion":2, "behavior": {
              "autoClose":{"delaySeconds":0,"pauseOnPointer":"no"},
              "height":{"maxFractionOfContent":0.9},"ordering":{"mode":"other"} } }
            """;
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults(), overlay,
            (_, context) => warnings.Add(context)).Current;
        Assert.Equal(5, config.Behavior.AutoCloseDelaySeconds);
        Assert.True(config.Behavior.PauseOnPointer);
        Assert.Equal(.45, config.Behavior.HeightMaxFractionOfContent);
        Assert.Equal(OperationOrderingMode.PendingFirst, config.Behavior.OrderingMode);
        Assert.True(warnings.Count >= 4);
    }

    [Fact]
    public void RemovedBehaviorFields_AreIgnoredWithWarnings()
    {
        var warnings = new List<string>();
        const string overlay = """
            { "schemaVersion":2, "behavior":{"persistCollapsed":false,"autoClose":{"hoverPausesAutoClose":false}} }
            """;
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults(), overlay,
            (exception, _) => warnings.Add(exception.Message)).Current;
        Assert.True(config.Behavior.PauseOnPointer);
        Assert.True(config.Behavior.DefaultExpanded);
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, message => message.Contains("persistCollapsed", StringComparison.Ordinal));
        Assert.Contains(warnings, message => message.Contains("hoverPausesAutoClose", StringComparison.Ordinal));
    }

    [Fact]
    public void OrderingResolver_PendingFirstGroupsStableAndQueueOrderPreservesArrival()
    {
        var firstCompleted = new WinProvision.Core.Models.OperationItem("Concluída 1", WinProvision.Core.Models.OperationKind.Install)
            { State = WinProvision.Core.Models.OperationState.Completed };
        var pending = new WinProvision.Core.Models.OperationItem("Pendente", WinProvision.Core.Models.OperationKind.Install);
        var secondCompleted = new WinProvision.Core.Models.OperationItem("Concluída 2", WinProvision.Core.Models.OperationKind.Install)
            { State = WinProvision.Core.Models.OperationState.Failed };
        var arrival = new[] { firstCompleted, pending, secondCompleted };
        Assert.Equal(new[] { pending, firstCompleted, secondCompleted },
            OperationOrderingResolver.Resolve(arrival, OperationOrderingMode.PendingFirst));
        Assert.Equal(arrival, OperationOrderingResolver.Resolve(arrival, OperationOrderingMode.QueueOrder));
    }

    [Fact]
    public void BehaviorConsumers_DoNotEmbedBehaviorDefaults()
    {
        string root = FindRepositoryRoot();
        string viewModel = File.ReadAllText(Path.Combine(root, "src/WinProvision.Store/ViewModels/OperationsViewModel.cs"));
        string panelCodeBehind = File.ReadAllText(Path.Combine(root, "src/WinProvision.Store/Controls/OperationsQueuePanel.xaml.cs"));
        string mainWindow = File.ReadAllText(Path.Combine(root, "src/WinProvision.Store/MainWindow.xaml.cs"));
        foreach (string consumer in new[] { viewModel, panelCodeBehind, mainWindow })
        {
            Assert.DoesNotContain("TimeSpan.FromSeconds(5)", consumer, StringComparison.Ordinal);
            Assert.DoesNotContain("0.45", consumer, StringComparison.Ordinal);
            Assert.DoesNotContain("pendingFirst", consumer, StringComparison.Ordinal);
            Assert.DoesNotContain("Math.Clamp(_operationsPanelHeight, 120", consumer, StringComparison.Ordinal);
            Assert.DoesNotContain("Math.Clamp(_operationsPanelHeight, 480", consumer, StringComparison.Ordinal);
            Assert.DoesNotContain("new GridLength(240)", consumer, StringComparison.Ordinal);
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WinProvision.Store.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    [Fact]
    public void UnknownSchema_DiscardsOverlayAndLogsWarning()
    {
        var warnings = new List<string>();
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults(),
            "{\"schemaVersion\":77,\"layout\":{\"rowHeight\":90}}", (_, context) => warnings.Add(context)).Current;
        Assert.Equal(48, config.Layout.RowHeight);
        Assert.Contains("OperationsPanel.UserConfig", warnings);
    }

    [Fact]
    public void CorruptOverlay_UsesEmbeddedDefaultsAndLogsWarning()
    {
        var warnings = new List<string>();
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults(), "{ invalid", (_, context) => warnings.Add(context)).Current;
        Assert.Equal(48, config.Layout.RowHeight);
        Assert.Equal(4, config.Theme.RowCornerRadius);
        Assert.Contains("OperationsPanel.UserConfig", warnings);
    }

    [Fact]
    public void Schema2Overlay_AcceptsJsonCommentsAndTrailingCommas()
    {
        const string overlay = """
            {
              // Comments are supported for user managed overlays.
              "schemaVersion": 2,
              "layout": { "rowHeight": 52, },
            }
            """;
        OperationsPanelConfiguration config = OperationsPanelCatalog.Create(EmbeddedDefaults(), overlay).Current;
        Assert.Equal(52, config.Layout.RowHeight);
    }

    [Fact]
    public void ReplacingConfiguration_IsAtomicAndRaisesChanged()
    {
        var catalog = OperationsPanelCatalog.Create(EmbeddedDefaults());
        int changed = 0;
        catalog.Changed += (_, _) => changed++;
        OperationsPanelConfiguration replacement = catalog.Current with
        { Theme = catalog.Current.Theme with { RowCornerRadius = 7 } };
        catalog.ReplaceConfiguration(replacement);
        Assert.Same(replacement, catalog.Current);
        Assert.Equal(1, changed);
    }

    [Fact]
    public void ReloadCandidate_IsValidatedOffSnapshotAndKeepsLastValidOnStructuralFailure()
    {
        OperationsPanelCatalog catalog = OperationsPanelCatalog.Create(EmbeddedDefaults());
        const string valid = "{\"schemaVersion\":2,\"layout\":{\"rowHeight\":62}}";
        Assert.True(catalog.TryBuildReloadCandidate(valid, out OperationsPanelConfiguration candidate));
        Assert.Equal(62, candidate.Layout.RowHeight);
        Assert.Equal(48, catalog.Current.Layout.RowHeight);
        Assert.False(catalog.TryBuildReloadCandidate("{ truncated", out OperationsPanelConfiguration rejected));
        Assert.Same(catalog.Current, rejected);
        Assert.False(catalog.TryBuildReloadCandidate("{\"schemaVersion\":88}", out _));
    }

    [Fact]
    public void ReloadCandidate_AcceptsBomAndJsonComments()
    {
        OperationsPanelCatalog catalog = OperationsPanelCatalog.Create(EmbeddedDefaults());
        string json = "\uFEFF{\"schemaVersion\":2, // user note\n \"layout\":{\"rowHeight\":61}}";
        Assert.True(catalog.TryBuildReloadCandidate(json, out OperationsPanelConfiguration candidate));
        Assert.Equal(61, candidate.Layout.RowHeight);
    }

    [Fact]
    public void ValidationReport_ExposesFieldWarningsAndStructuralErrors()
    {
        OperationsPanelCatalog catalog = OperationsPanelCatalog.Create(EmbeddedDefaults());
        OperationsPanelValidationReport fieldReport = catalog.ValidateUserJson("{\"schemaVersion\":2,\"layout\":{\"rowHeight\":5000}}");
        Assert.True(fieldReport.IsValid);
        Assert.Contains(fieldReport.Issues, issue => issue.FieldPath == "$.layout.rowHeight" && !issue.IsError);
        OperationsPanelValidationReport structuralReport = catalog.ValidateUserJson("{\"schemaVersion\":99}");
        Assert.False(structuralReport.IsValid);
        Assert.Contains(structuralReport.Issues, issue => issue.FieldPath == "$" && issue.IsError);
    }

    [Fact]
    public void CompatibilityValidation_HandlesEmptyOversizedWrongTypeAndLegacySchema()
    {
        OperationsPanelCatalog catalog = OperationsPanelCatalog.Create(EmbeddedDefaults());
        Assert.False(catalog.TryBuildReloadCandidate(string.Empty, out _));
        OperationsPanelValidationReport oversized = catalog.ValidateUserJson(new string(' ', 1024 * 1024 + 1));
        Assert.False(oversized.IsValid);
        Assert.Contains(oversized.Issues, issue => issue.IsError && issue.Reason.Contains("1 MB", StringComparison.Ordinal));
        Assert.True(catalog.TryBuildReloadCandidate("{\"schemaVersion\":2,\"layout\":{\"rowHeight\":\"large\"}}", out OperationsPanelConfiguration wrongType));
        Assert.Equal(48, wrongType.Layout.RowHeight);
        Assert.True(catalog.TryBuildReloadCandidate("{\"schemaVersion\":1,\"metrics\":{\"rowHeight\":60}}", out OperationsPanelConfiguration legacy));
        Assert.Equal(60, legacy.Layout.RowHeight);
    }
}
