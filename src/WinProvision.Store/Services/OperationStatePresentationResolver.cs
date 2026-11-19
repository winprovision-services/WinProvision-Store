using WinProvision.Core.Models;
using System.Text.Json;

namespace WinProvision.Store.Services;

public sealed record ResolvedStatePresentation(string IconId, string ColorToken, string MessageColorToken,
    string TrackColorToken, string FillColorToken, double? BackgroundAlpha, double MessageAlpha,
    string MessageFontToken, string MessageFontWeight, OperationProgressMode ProgressMode, bool ProgressVisible);

public enum OperationProgressMode { None, Indeterminate, Value, Complete }

/// <summary>Closed, UI-independent catalog and resolver for the appearance of operation states.</summary>
public static class OperationStatePresentationCatalog
{
    public const string NeutralBackgroundBrushKey = "OperationNeutralBackgroundBrush";
    public const string AttentionBackgroundBrushKey = "OperationAttentionBackgroundBrush";
    public const string DescriptionBackgroundBrushKey = "OperationDescriptionBackgroundBrush";
    public const string AttentionBaseBrushKey = "AppOperationWingetBrush";
    public static string ResolveMessageFontFamily(string fontToken, string defaultFamily) => fontToken switch
    {
        "messageMonospace" => "Consolas",
        _ => defaultFamily
    };
    public static string GetManagerIconId(string manager, IReadOnlyDictionary<string, string> managerIcons) =>
        managerIcons.GetValueOrDefault(manager, managerIcons.GetValueOrDefault("default", Defaults["queued"].IconId));
    private sealed record DefaultData(IReadOnlyDictionary<string, OperationStatePresentationDefinition> States,
        IReadOnlyDictionary<string, string> ManagerIcons);
    private static readonly DefaultData EmbeddedDefaults = LoadDefaults();
    public static IReadOnlyDictionary<string, OperationStatePresentationDefinition> Defaults => EmbeddedDefaults.States;
    public static IReadOnlyDictionary<string, string> DefaultManagerIcons => EmbeddedDefaults.ManagerIcons;

    private static DefaultData LoadDefaults()
    {
        using Stream stream = typeof(OperationStatePresentationCatalog).Assembly.GetManifestResourceStream("WinProvision.Store.Assets.operations-panel.json")
            ?? throw new InvalidDataException("operations-panel.json embutido não encontrado.");
        using JsonDocument document = JsonDocument.Parse(stream);
        var states = new Dictionary<string, OperationStatePresentationDefinition>(StringComparer.Ordinal);
        foreach (JsonProperty entry in document.RootElement.GetProperty("states").EnumerateObject())
        {
            JsonElement value = entry.Value, progress = value.GetProperty("progress");
            states.Add(entry.Name, new(value.GetProperty("iconId").GetString()!, value.GetProperty("colorToken").GetString()!,
                value.GetProperty("messageColorToken").GetString()!, value.GetProperty("trackColorToken").GetString()!,
                value.GetProperty("fillColorToken").GetString()!, value.TryGetProperty("backgroundAlpha", out JsonElement alpha) ? alpha.GetDouble() : null,
                value.TryGetProperty("messageAlpha", out JsonElement messageAlpha) ? messageAlpha.GetDouble() : null,
                value.GetProperty("messageFont").GetString()!,
                value.GetProperty("messageFontWeight").GetString()!, progress.GetProperty("mode").GetString()!, progress.GetProperty("visible").GetBoolean()));
        }
        var managers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty entry in document.RootElement.GetProperty("managerIcons").EnumerateObject()) managers.Add(entry.Name, entry.Value.GetString()!);
        return new(states, managers);
    }

    public static readonly IReadOnlySet<string> ColorTokens = new HashSet<string>(StringComparer.Ordinal)
    {
        "OperationNeutralBackgroundBrush", "OperationAttentionBackgroundBrush", "OperationDescriptionBackgroundBrush",
        "OperationProgressTrackTransparentBrush", "AppOperationDefaultBrush", "AppOperationSuccessBrush",
        "AppOperationWingetBrush",
        "AppOperationSuccessSurfaceBrush", "AppOperationFailureBrush", "AppOperationFailureSurfaceBrush",
        "AppOperationCanceledBrush", "AppOperationCanceledSurfaceBrush", "AppOperationUnconfirmedBrush", "AppOperationUnconfirmedSurfaceBrush"
    };
    public static readonly IReadOnlySet<string> IconIds = new HashSet<string>(StringComparer.Ordinal)
        { "apps", "document", "info", "settings", "folderOpen", "arrowClockwise", "shield", "desktop", "dismiss", "delete", "checkmark" };
    public static readonly IReadOnlySet<string> ManagerIconIds = new HashSet<string>(StringComparer.Ordinal)
        { "apps", "managerWinget", "managerChocolatey", "managerOffice" };

    public static ResolvedStatePresentation Resolve(OperationState state, bool isIndeterminate, bool isLightTheme,
        OperationsPanelConfiguration configuration)
    {
        string key = state switch
        {
            OperationState.Queued => "queued", OperationState.Running => "running", OperationState.Completed => "completed",
            OperationState.Failed => "failed", OperationState.Canceled => "canceled", _ => "unconfirmed"
        };
        var value = configuration.States.GetValueOrDefault(key) ?? Defaults[key];
        OperationProgressMode mode = state is OperationState.Queued or OperationState.Running
            ? (isIndeterminate ? OperationProgressMode.Indeterminate : OperationProgressMode.Value)
            : Enum.Parse<OperationProgressMode>(value.ProgressMode, ignoreCase: true);
        double? alpha = value.BackgroundAlpha;
        if (state == OperationState.Queued || state == OperationState.Unconfirmed) alpha ??= configuration.Theme.NeutralBackgroundAlpha;
        else if (state == OperationState.Running) alpha ??= configuration.Theme.AttentionBackgroundAlpha;
        double messageAlpha = value.MessageAlpha ?? configuration.Theme.MessageBackgroundAlpha;
        return new(value.IconId, value.ColorToken, value.MessageColorToken, value.TrackColorToken, value.FillColorToken,
            alpha, messageAlpha, value.MessageFontToken, value.MessageFontWeight, mode, value.ProgressVisible);
    }
}

public static class OperationStatePresentationResolver
{
    public static ResolvedStatePresentation Resolve(OperationState state, bool isIndeterminate, bool isLightTheme,
        OperationsPanelConfiguration catalog) => OperationStatePresentationCatalog.Resolve(state, isIndeterminate, isLightTheme, catalog);
}
