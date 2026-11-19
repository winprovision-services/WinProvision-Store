using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using WinProvision.Core.Models;
using WinProvision.Store.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class OperationActionResolverTests
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private sealed record GoldenCase(string Context, IReadOnlyList<GoldenAction> Primary, IReadOnlyList<GoldenAction> Menu);
    private sealed record GoldenAction(string Id, string TextKey, string IconId, bool Visible, bool Enabled,
        string Group, int Order, string Placement, string? ParentId, string CommandId, string Scope, bool Locked, bool IsSeparator);

    private static string Resource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource not found: {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static OperationActionCatalog DefaultCatalog() => OperationActionCatalog.Load(
        Resource("WinProvision.Store.Assets.operations-panel.json"));

    private static GoldenAction[] ToGolden(IEnumerable<ResolvedAction> actions) => actions.Select(action => new GoldenAction(
        action.Id, action.TextKey, action.IconId, action.Visible, action.Enabled, action.Group, action.Order,
        action.Placement.ToString().ToLowerInvariant(), action.ParentId, action.CommandId,
        action.Scope.ToString().ToLowerInvariant(), action.Locked, action.IsSeparator)).ToArray();

    [Fact]
    public void DefaultResolverMatchesCapturedLegacyMenuFixture()
    {
        OperationActionCatalog catalog = DefaultCatalog();
        List<GoldenCase> actual = [];
        bool[] flags = [false, true];
        string?[] managers = [null, "winget", "chocolatey", "other"];
        foreach (OperationState state in Enum.GetValues<OperationState>())
        foreach (OperationKind kind in Enum.GetValues<OperationKind>())
        foreach (string? manager in managers)
        foreach (bool hasInstallLocation in flags)
        foreach (bool canCancel in flags)
        foreach (bool elevated in flags)
        foreach (bool interactive in flags)
        {
            bool finished = state is OperationState.Completed or OperationState.Failed or OperationState.Canceled or OperationState.Unconfirmed;
            bool hasPackage = manager is "winget" or "chocolatey";
            var context = new OperationActionContext(OperationActionScope.Item, state, kind,
                HasPackage: hasPackage, HasInstallLocation: hasInstallLocation,
                CanCancel: canCancel && state is OperationState.Queued or OperationState.Running,
                CanReplay: finished && hasPackage, Manager: manager,
                CanReplayElevated: finished && hasPackage && !elevated,
                CanReplayInteractive: finished && hasPackage && !interactive);
            string id = $"item:{state}:{kind}:manager={manager ?? "none"}:path={hasInstallLocation}:cancel={canCancel}:elevated={elevated}:interactive={interactive}";
            GoldenAction[] primary = ToGolden(OperationActionResolver.Resolve(OperationActionScope.Item, context, catalog)
                .Where(action => action.Placement == OperationActionPlacement.Primary));
            GoldenAction[] menu = ToGolden(OperationActionResolver.Resolve(OperationActionScope.Item, context, catalog)
                .Where(action => action.Placement == OperationActionPlacement.Menu));
            AssertLegacyItemBehavior(catalog, context, primary, menu);
            actual.Add(new(id, primary, menu));
        }

        foreach (bool hasFailed in flags)
        foreach (bool hasSucceeded in flags)
        foreach (bool hasFinished in flags)
        foreach (bool hasCancelable in flags)
        {
            var context = new OperationActionContext(OperationActionScope.Header, HasFailed: hasFailed,
                HasSucceeded: hasSucceeded, HasFinished: hasFinished, HasCancelable: hasCancelable);
            string id = $"header:failed={hasFailed}:succeeded={hasSucceeded}:finished={hasFinished}:cancelable={hasCancelable}";
            GoldenAction[] menu = ToGolden(OperationActionResolver.Resolve(OperationActionScope.Header, context, catalog));
            AssertLegacyHeaderBehavior(menu, hasFailed, hasSucceeded, hasFinished, hasCancelable);
            actual.Add(new(id, [], menu));
        }

        string json = JsonSerializer.Serialize(actual, JsonOptions);
        if (Environment.GetEnvironmentVariable("GENERATE_OPERATIONS_MENU_GOLDEN") == "1")
        {
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "operations-menu-golden.generated.json"), json);
            return;
        }
        var expected = JsonSerializer.Deserialize<List<GoldenCase>>(Resource("OperationMenuTests.operations-menu-golden.json"), JsonOptions)!;
        Assert.Equal(JsonSerializer.Serialize(expected, JsonOptions), JsonSerializer.Serialize(actual, JsonOptions));
    }

    private static void AssertLegacyItemBehavior(OperationActionCatalog catalog, OperationActionContext context,
        GoldenAction[] primary, GoldenAction[] menu)
    {
        string expectedPrimary = context.State is OperationState.Queued or OperationState.Running ? "cancel" : "close";
        Assert.Single(primary, action => action.Visible);
        GoldenAction button = Assert.Single(primary, action => action.Visible);
        Assert.Equal(expectedPrimary, button.Id);
        Assert.Equal(expectedPrimary == "cancel" ? "operations.button.cancel" : "operations.button.close", button.TextKey);
        Assert.Equal(expectedPrimary == "cancel" ? "cancelOperation" : "removeOperation", button.CommandId);
        Assert.True(button.Locked);
        bool finished = context.State is OperationState.Completed or OperationState.Failed or OperationState.Canceled or OperationState.Unconfirmed;
        bool canReplay = finished && context.HasPackage;
        var expected = new Dictionary<string, (bool Visible, bool Enabled)>(StringComparer.Ordinal)
        {
            ["output"] = (true, true), ["packageDetails"] = (true, context.HasPackage),
            ["packageOptions"] = (true, context.HasPackage), ["installLocation"] = (true, context.HasInstallLocation),
            ["retry"] = (canReplay, canReplay),
            ["replayElevated"] = (context.CanReplayElevated, context.CanReplayElevated),
            ["replayInteractive"] = (context.CanReplayInteractive, context.CanReplayInteractive),
            ["cancelOperation"] = (context.CanCancel, context.CanCancel), ["remove"] = (finished, finished)
        };
        Assert.Equal(expected.Count, menu.Length);
        var legacy = new Dictionary<string, (string Key, string Icon, string Command, int Order)>(StringComparer.Ordinal)
        {
            ["output"] = ("operations.menu.item.output", "document", "showOperationOutput", 0),
            ["packageDetails"] = ("operations.menu.item.packageDetails", "info", "showPackageDetails", 10),
            ["packageOptions"] = ("operations.menu.item.installOptions", "settings", "showPackageOptions", 20),
            ["installLocation"] = ("operations.menu.item.installLocation", "folderOpen", "openInstallLocation", 30),
            ["retry"] = ("operations.menu.item.retry", "arrowClockwise", "replayOperation", 40),
            ["replayElevated"] = ("operations.menu.item.replayElevated", "shield", "replayOperationVariant:elevated", 50),
            ["replayInteractive"] = ("operations.menu.item.replayInteractive", "desktop", "replayOperationVariant:interactive", 60),
            ["cancelOperation"] = ("operations.menu.item.cancel", "dismiss", "cancelOperation", 70),
            ["remove"] = ("operations.menu.item.remove", "delete", "removeOperation", 80)
        };
        Assert.Equal(legacy.Keys.OrderBy(id => legacy[id].Order), menu.OrderBy(action => action.Order).Select(action => action.Id));
        foreach (GoldenAction item in menu)
        {
            Assert.True(expected.TryGetValue(item.Id, out var state), item.Id);
            Assert.Equal(legacy[item.Id].Key, item.TextKey);
            Assert.Equal(legacy[item.Id].Icon, item.IconId);
            Assert.Equal(legacy[item.Id].Command, item.CommandId);
            Assert.Equal(legacy[item.Id].Order, item.Order);
            Assert.True(state.Visible == item.Visible, $"{context.State}/{context.Kind}/{context.HasPackage}/{context.HasInstallLocation}/{context.CanCancel}/{context.CanReplayElevated}/{context.CanReplayInteractive} action={item.Id} visible expected={state.Visible} actual={item.Visible}");
            Assert.True(state.Enabled == item.Enabled, $"{context.State}/{context.Kind}/{context.HasPackage}/{context.HasInstallLocation}/{context.CanCancel}/{context.CanReplayElevated}/{context.CanReplayInteractive} action={item.Id} enabled expected={state.Enabled} actual={item.Enabled}");
        }
        Assert.Equal(catalog.Actions.Count(action => action.Scope == OperationActionScope.Item && action.Placement == OperationActionPlacement.Menu), menu.Length);
    }

    private static void AssertLegacyHeaderBehavior(GoldenAction[] menu, bool failed, bool succeeded, bool finished, bool cancelable)
    {
        Assert.Equal(new[] { "retryFailed", "clearSuccessful", "clearFinished", "separator.cancelAll", "cancelAll" },
            menu.Select(action => action.Id));
        Assert.Equal(new[] { failed, succeeded, finished, true, cancelable }, menu.Select(action => action.Enabled));
        Assert.Equal(new[] { "operations.menu.header.retryFailed", "operations.menu.header.clearSuccessful",
            "operations.menu.header.clearFinished", "", "operations.menu.header.cancelAll" }, menu.Select(action => action.TextKey));
        Assert.Equal(new[] { "arrowClockwise", "checkmark", "dismiss", "none", "dismiss" }, menu.Select(action => action.IconId));
        Assert.Equal(new[] { "replayFailed", "clearSucceeded", "clearFinished", "separator", "cancelAll" }, menu.Select(action => action.CommandId));
        Assert.All(menu, action => Assert.True(action.Visible));
    }

    [Fact]
    public void CatalogHasRegisteredCommandsIconsTextKeysAndClosedConditions()
    {
        OperationActionCatalog catalog = DefaultCatalog();
        Dictionary<string, string> text = JsonDocument.Parse(Resource("WinProvision.Store.Assets.Localization.operations.pt-BR.json"))
            .RootElement.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!);
        foreach (OperationActionDefinition action in catalog.Actions)
        {
            Assert.Contains(action.CommandId, OperationActionRegistry.RegisteredCommands);
            Assert.Contains(action.IconId, OperationActionRegistry.RegisteredIcons);
            if (action.TextKey.Length > 0) Assert.Contains(action.TextKey, text.Keys);
            foreach (string condition in action.VisibleWhen.Concat(action.EnabledWhen))
                Assert.Contains(condition, OperationActionConditionEvaluator.RegisteredConditions);
        }
    }

    [Fact]
    public void OverridesOnlyChangeAllowedFieldsAndKeepLockedButtons()
    {
        var logs = new List<string>();
        string overlay = "{\"schemaVersion\":2,\"actions\":["
            + "{\"id\":\"output\",\"order\":5,\"group\":\"first\",\"visible\":false,\"iconId\":\"info\",\"textKey\":\"missing.key\"},"
            + "{\"id\":\"cancel\",\"visible\":false,\"placement\":\"menu\"},"
            + "{\"id\":\"retry\",\"commandId\":\"arbitrary\"},"
            + "{\"id\":\"remove\",\"visibleWhen\":[\"arbitrary\"]}] }";
        OperationActionCatalog merged = DefaultCatalog().Merge(overlay, logs.Add);
        OperationActionDefinition output = merged.Actions.Single(action => action.Id == "output");
        Assert.Equal(5, output.Order);
        Assert.Equal("first", output.Group);
        Assert.False(output.Visible);
        Assert.Equal("info", output.IconId);
        Assert.Equal("operations.menu.item.output", output.TextKey);
        Assert.Contains(merged.Actions, action => action.Id == "cancel" && action.Visible && action.Locked && action.Placement == OperationActionPlacement.Primary);
        Assert.Contains(logs, message => message.Contains("missing.key", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("não sobrescrevíveis", StringComparison.Ordinal));
        Assert.Contains(logs, message => message.Contains("Condição desconhecida", StringComparison.Ordinal));
    }
}
