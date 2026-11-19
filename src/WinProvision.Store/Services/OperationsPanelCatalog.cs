using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed record OperationsPanelLayout(
    double PanelMinHeight, double PanelMaxHeight, double PanelDefaultHeight,
    double RowHeight, double RowSpacing, double RowHorizontalPadding, double RowVerticalPadding,
    double IconColumnWidth, double IconSize, double RowIconHeight, double FlexibleColumnSpacing,
    double TitleMaxWidthFraction, double TitleMaxWidth, double TitleMinWidth,
    double DescriptionMinWidth, double DescriptionHeight,
    double ActionButtonWidth, double MoreButtonWidth, double ActionButtonHeight)
{
    public double RowMarginVertical => RowSpacing / 2;
}

public sealed record OperationsPanelTheme(
    double RowCornerRadius, double MessageCornerRadius, double ProgressCornerRadius,
    double ProgressTrackHeight, double ProgressHeight,
    double MessagePaddingHorizontal, double MessagePaddingTop, double MessagePaddingBottom,
    double TitleFontSize, double MessageFontSize, double ActionFontSize,
    string MessageFontFamily,
    double NeutralBackgroundAlpha, double AttentionBackgroundAlpha, double MessageBackgroundAlpha);

public enum OperationOrderingMode { PendingFirst, QueueOrder }

public sealed record OperationPanelBehavior(
    int AutoCloseDelaySeconds, bool PauseOnPointer, bool PauseOnFocus, bool PauseOnPopup,
    double HeightDefault, double HeightMin, double HeightMax, double HeightMaxFractionOfContent,
    bool DefaultExpanded, OperationOrderingMode OrderingMode);

public sealed record OperationsPanelValidationIssue(string FieldPath, string Reason, bool IsError);
public sealed record OperationsPanelValidationReport(bool IsValid, IReadOnlyList<OperationsPanelValidationIssue> Issues);

public static class OperationPanelBehaviorResolver
{
    public static OperationPanelBehavior Resolve(OperationsPanelConfiguration configuration, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        OperationPanelBehavior value = configuration.Behavior;
        OperationPanelBehavior defaults = OperationsPanelCatalog.BuiltInDefaults.Behavior;
        double maxFraction = double.IsFinite(value.HeightMaxFractionOfContent)
            && value.HeightMaxFractionOfContent is >= .2 and <= .8 ? value.HeightMaxFractionOfContent : defaults.HeightMaxFractionOfContent;
        if (maxFraction != value.HeightMaxFractionOfContent) log?.Invoke("height.maxFractionOfContent inválido; padrão embutido mantido.");
        int delay = value.AutoCloseDelaySeconds is >= 1 and <= 60 ? value.AutoCloseDelaySeconds : defaults.AutoCloseDelaySeconds;
        if (delay != value.AutoCloseDelaySeconds) log?.Invoke("autoClose.delaySeconds inválido; padrão embutido mantido.");
        OperationOrderingMode ordering = Enum.IsDefined(value.OrderingMode) ? value.OrderingMode : defaults.OrderingMode;
        if (ordering != value.OrderingMode) log?.Invoke("ordering.mode inválido; padrão embutido mantido.");
        OperationsPanelLayout layout = configuration.Layout;
        return value with
        {
            AutoCloseDelaySeconds = delay,
            HeightDefault = layout.PanelDefaultHeight,
            HeightMin = layout.PanelMinHeight,
            HeightMax = layout.PanelMaxHeight,
            HeightMaxFractionOfContent = maxFraction,
            OrderingMode = ordering
        };
    }
}

public static class OperationOrderingResolver
{
    public static OperationItem[] Resolve(IEnumerable<OperationItem> operations, OperationOrderingMode mode)
    {
        ArgumentNullException.ThrowIfNull(operations);
        OperationItem[] ordered = operations.ToArray();
        return mode == OperationOrderingMode.QueueOrder
            ? ordered
            : ordered.OrderBy(operation => operation.IsFinished).ToArray();
    }
}

public sealed record OperationStatePresentationDefinition(string IconId, string ColorToken, string MessageColorToken,
    string TrackColorToken, string FillColorToken, double? BackgroundAlpha, double? MessageAlpha,
    string MessageFontToken, string MessageFontWeight, string ProgressMode, bool ProgressVisible);

public sealed record OperationsPanelConfiguration(
    OperationsPanelLayout Layout,
    OperationsPanelTheme Theme,
    IReadOnlyList<OperationActionDefinition>? Actions = null)
{
    public OperationPanelBehavior Behavior { get; init; } = new(5, true, true, true, 240, 120, 480, .45, true, OperationOrderingMode.PendingFirst);
    public IReadOnlyDictionary<string, OperationStatePresentationDefinition> States { get; init; } = OperationStatePresentationCatalog.Defaults;
    public IReadOnlyDictionary<string, string> ManagerIcons { get; init; } = OperationStatePresentationCatalog.DefaultManagerIcons;
}

public interface IOperationsPanelCatalog
{
    OperationsPanelConfiguration Current { get; }
    event EventHandler? Changed;
}

/// <summary>Catálogo imutável de apresentação das operações, com substituição atômica preparada.</summary>
public sealed class OperationsPanelCatalog : IOperationsPanelCatalog
{
    private const string Resource = "WinProvision.Store.Assets.operations-panel.json";
    private readonly string _embeddedJson;
    private OperationsPanelConfiguration _current;

    public static string UserFilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStoreWinUI3", "operations-panel.json");

    public OperationsPanelCatalog()
    {
        _embeddedJson = ReadEmbeddedJson();
        _current = Parse(_embeddedJson, ReadUserJson(UserFilePath));
    }
    private OperationsPanelCatalog(string embeddedJson, OperationsPanelConfiguration configuration)
    { _embeddedJson = embeddedJson; _current = configuration; }

    public OperationsPanelConfiguration Current => Volatile.Read(ref _current);
    public event EventHandler? Changed;

    public void ReplaceConfiguration(OperationsPanelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        Interlocked.Exchange(ref _current, configuration);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Builds a candidate without publishing it; intended for background validation.</summary>
    public bool TryBuildReloadCandidate(string? userJson, out OperationsPanelConfiguration candidate)
    {
        userJson = userJson?.TrimStart('\uFEFF');
        OperationsPanelValidationReport report = ValidateUserJson(userJson);
        if (!report.IsValid) { candidate = Current; return false; }
        candidate = Parse(_embeddedJson, userJson, (_, _) => { });
        return true;
    }

    /// <summary>Returns and logs structural errors and field-level warnings for a user overlay.</summary>
    public OperationsPanelValidationReport ValidateUserJson(string? userJson)
    {
        userJson = userJson?.TrimStart('\uFEFF');
        var issues = new List<OperationsPanelValidationIssue>();
        if (userJson is null) return new(true, issues);
        if (Encoding.UTF8.GetByteCount(userJson) > 1024 * 1024)
            issues.Add(new("$", "O arquivo excede o limite de 1 MB.", true));
        else
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(userJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                _ = GetSchema(document.RootElement);
                _ = Parse(_embeddedJson, userJson, (exception, context) =>
                {
                    string reason = exception.Message;
                    string field = FindQuotedField(reason);
                    string path = !string.IsNullOrEmpty(field) ? FindFieldPath(document.RootElement, field) ?? "$" : context.EndsWith(".Actions", StringComparison.Ordinal) ? "$.actions" : "$.states";
                    issues.Add(new(path, reason, false));
                });
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                issues.Add(new("$", exception.Message, true));
            }
        }
        foreach (OperationsPanelValidationIssue issue in issues)
            CoreLog.Warning(new InvalidDataException($"{issue.FieldPath}: {issue.Reason}"), "OperationsPanel.Validation");
        return new(!issues.Any(issue => issue.IsError), issues);
    }

    private static string FindQuotedField(string message)
    {
        int start = message.IndexOf('\'', StringComparison.Ordinal);
        int end = start < 0 ? -1 : message.IndexOf('\'', start + 1);
        return start >= 0 && end > start ? message[(start + 1)..end] : string.Empty;
    }

    private static string? FindFieldPath(JsonElement node, string field, string path = "$")
    {
        if (node.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty property in node.EnumerateObject())
            {
                string propertyPath = $"{path}.{property.Name}";
                if (property.NameEquals(field)) return propertyPath;
                string? nested = FindFieldPath(property.Value, field, propertyPath);
                if (nested is not null) return nested;
            }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in node.EnumerateArray())
            {
                string? nested = FindFieldPath(item, field, $"{path}[{index++}]");
                if (nested is not null) return nested;
            }
        }
        return null;
    }

    private static string ReadEmbeddedJson()
    {
        try
        {
            using Stream stream = typeof(OperationsPanelCatalog).Assembly.GetManifestResourceStream(Resource) ?? throw new FileNotFoundException(Resource);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception exception)
        {
            CoreLog.Warning(exception, "OperationsPanel.DefaultConfig");
            return "{\"schemaVersion\":2}";
        }
    }

    private static string? ReadUserJson(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception exception) { CoreLog.Warning(exception, "OperationsPanel.UserConfig"); return null; }
    }

    private static OperationsPanelConfiguration LoadEmbedded()
    {
        string defaults = ReadEmbeddedJson();
        return Parse(defaults, ReadUserJson(UserFilePath));
    }

    /// <summary>Fábrica também usada pelos testes do parser e da compatibilidade de schema.</summary>
    public static OperationsPanelCatalog Create(string embeddedJson, string? userJson = null, Action<Exception, string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(embeddedJson);
        return new OperationsPanelCatalog(embeddedJson, Parse(embeddedJson, userJson, log));
    }

    private static OperationsPanelConfiguration Parse(string defaultsJson, string? userJson, Action<Exception, string>? log = null)
    {
        OperationsPanelConfiguration defaults;
        try
        {
            using JsonDocument document = JsonDocument.Parse(defaultsJson);
            if (GetSchema(document.RootElement) != 2) throw new InvalidDataException("O catálogo embutido deve usar schemaVersion 2.");
            defaults = MergeV2(BuiltInDefaults, document.RootElement, BuiltInDefaults, log);
            defaults = NormalizePanelBounds(defaults, BuiltInDefaults, log);
        }
        catch (Exception exception)
        {
            (log ?? Log)(exception, "OperationsPanel.DefaultConfig");
            defaults = BuiltInDefaults;
        }

        userJson = userJson?.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(userJson)) return defaults;
        try
        {
            using JsonDocument document = JsonDocument.Parse(userJson, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            int schema = GetSchema(document.RootElement);
            WarnRemovedBehaviorFields(document.RootElement, log);
            OperationsPanelConfiguration merged = schema switch
            {
                2 => MergeV2(defaults, document.RootElement, defaults, log, processActions: false, processStates: true),
                1 => MergeV1(defaults, document.RootElement, log),
                _ => throw new InvalidDataException($"Schema de operations-panel.json não suportado: {schema}.")
            };
            OperationsPanelConfiguration normalized = NormalizePanelBounds(merged, defaults, log);
            if (schema == 2 && document.RootElement.TryGetProperty("actions", out JsonElement userActions)
                && userActions.ValueKind == JsonValueKind.Array)
            {
                IReadOnlyList<OperationActionDefinition> actionDefaults = defaults.Actions ?? OperationActionCatalog.BuiltIn.Actions;
                string actionDefaultsJson = OperationActionCatalog.SerializeDefinitions(actionDefaults);
                IReadOnlyList<OperationActionDefinition> actions = OperationActionCatalog.Load(actionDefaultsJson, userJson,
                    message => (log ?? Log)(new InvalidDataException(message), "OperationsPanel.Actions")).Actions;
                normalized = normalized with { Actions = actions };
            }
            return normalized;
        }
        catch (Exception exception)
        {
            (log ?? Log)(exception, "OperationsPanel.UserConfig");
            return defaults;
        }
    }

    private static int GetSchema(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out JsonElement value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int schema))
            throw new InvalidDataException("operations-panel.json requer schemaVersion inteiro.");
        if (schema is not (1 or 2)) throw new InvalidDataException($"Schema de operations-panel.json não suportado: {schema}.");
        return schema;
    }

    private static OperationsPanelConfiguration MergeV2(OperationsPanelConfiguration current, JsonElement root,
        OperationsPanelConfiguration fallback, Action<Exception, string>? log, bool processActions = true, bool processStates = true)
    {
        JsonElement layout = Object(root, "layout");
        JsonElement theme = Object(root, "theme");
        JsonElement metrics = Object(root, "metrics");
        JsonElement actionsElement = Object(root, "actions");
        var l = current.Layout;
        var lf = fallback.Layout;
        var t = current.Theme;
        var tf = fallback.Theme;
        var configuration = new OperationsPanelConfiguration(
            new(
                Number(layout, "panelMinHeight", l.PanelMinHeight, lf.PanelMinHeight, 80, 1000, log),
                Number(layout, "panelMaxHeight", l.PanelMaxHeight, lf.PanelMaxHeight, 120, 2000, log),
                Number(layout, "panelDefaultHeight", l.PanelDefaultHeight, lf.PanelDefaultHeight, 80, 2000, log),
                Number(layout, "rowHeight", l.RowHeight, lf.RowHeight, 24, 120, log),
                Number(layout, "rowSpacing", l.RowSpacing, lf.RowSpacing, 0, 32, log),
                Number(layout, "rowHorizontalPadding", l.RowHorizontalPadding, lf.RowHorizontalPadding, 0, 24, log),
                Number(layout, "rowVerticalPadding", l.RowVerticalPadding, lf.RowVerticalPadding, 0, 16, log),
                Number(layout, "iconColumnWidth", l.IconColumnWidth, lf.IconColumnWidth, 24, 80, log),
                Number(layout, "iconSize", l.IconSize, lf.IconSize, 16, 64, log),
                Number(layout, "rowIconHeight", l.RowIconHeight, lf.RowIconHeight, 16, 48, log),
                Number(layout, "flexibleColumnSpacing", l.FlexibleColumnSpacing, lf.FlexibleColumnSpacing, 4, 24, log),
                Number(layout, "titleMaxWidthFraction", l.TitleMaxWidthFraction, lf.TitleMaxWidthFraction, .1, .8, log),
                Number(layout, "titleMaxWidth", l.TitleMaxWidth, lf.TitleMaxWidth, 160, 800, log),
                Number(layout, "titleMinWidth", l.TitleMinWidth, lf.TitleMinWidth, 48, 240, log),
                Number(layout, "descriptionMinWidth", l.DescriptionMinWidth, lf.DescriptionMinWidth, 100, 400, log),
                Number(layout, "descriptionHeight", l.DescriptionHeight, lf.DescriptionHeight, 24, 64, log),
                Number(layout, "actionButtonWidth", l.ActionButtonWidth, lf.ActionButtonWidth, 80, 180, log),
                Number(layout, "moreButtonWidth", l.MoreButtonWidth, lf.MoreButtonWidth, 28, 56, log),
                Number(layout, "actionButtonHeight", l.ActionButtonHeight, lf.ActionButtonHeight, 24, 48, log)),
            new(
                Number(theme, "rowCornerRadius", t.RowCornerRadius, tf.RowCornerRadius, 0, 16, log),
                Number(theme, "messageCornerRadius", t.MessageCornerRadius, tf.MessageCornerRadius, 0, 16, log),
                Number(theme, "progressCornerRadius", t.ProgressCornerRadius, tf.ProgressCornerRadius, 0, 16, log),
                Number(metrics, "progressTrackHeight", t.ProgressTrackHeight, tf.ProgressTrackHeight, 4, 18, log),
                Number(metrics, "progressHeight", t.ProgressHeight, tf.ProgressHeight, 1, 8, log),
                Number(metrics, "messagePaddingHorizontal", t.MessagePaddingHorizontal, tf.MessagePaddingHorizontal, 0, 24, log),
                Number(metrics, "messagePaddingTop", t.MessagePaddingTop, tf.MessagePaddingTop, 0, 16, log),
                Number(metrics, "messagePaddingBottom", t.MessagePaddingBottom, tf.MessagePaddingBottom, 0, 16, log),
                Number(metrics, "titleFontSize", t.TitleFontSize, tf.TitleFontSize, 10, 24, log),
                Number(metrics, "messageFontSize", t.MessageFontSize, tf.MessageFontSize, 10, 20, log),
                Number(metrics, "actionFontSize", t.ActionFontSize, tf.ActionFontSize, 10, 24, log),
                String(metrics, "messageFontFamily", t.MessageFontFamily, tf.MessageFontFamily, 1, 128, log),
                Number(theme, "neutralBackgroundAlpha", t.NeutralBackgroundAlpha, tf.NeutralBackgroundAlpha, 0, .5, log),
                Number(theme, "attentionBackgroundAlpha", t.AttentionBackgroundAlpha, tf.AttentionBackgroundAlpha, 0, .5, log),
                Number(theme, "messageBackgroundAlpha", t.MessageBackgroundAlpha, tf.MessageBackgroundAlpha, 0, .5, log)),
            processActions && actionsElement.ValueKind == JsonValueKind.Array
                ? OperationActionCatalog.ParseDefinitions("{\"schemaVersion\":2,\"actions\":" + actionsElement.GetRawText() + "}", message => (log ?? Log)(new InvalidDataException(message), "OperationsPanel.Actions"))
                : current.Actions);
        JsonElement stateElement = Object(root, "states");
        if (processStates && stateElement.ValueKind == JsonValueKind.Object)
            configuration = configuration with { States = MergeStates(current.States, stateElement, fallback.States, log, allowProgressMode: true) };
        else if (stateElement.ValueKind == JsonValueKind.Object)
            configuration = configuration with { States = MergeStates(current.States, stateElement, fallback.States, log, allowProgressMode: false) };
        JsonElement managerIcons = Object(root, "managerIcons");
        if (managerIcons.ValueKind == JsonValueKind.Object)
            configuration = configuration with { ManagerIcons = MergeManagerIcons(current.ManagerIcons, managerIcons, fallback.ManagerIcons, log) };
        return MergeBehavior(configuration, root, fallback, log);
    }

    private static OperationsPanelConfiguration MergeBehavior(OperationsPanelConfiguration configuration,
        JsonElement root, OperationsPanelConfiguration fallback, Action<Exception, string>? log)
    {
        JsonElement behavior = Object(root, "behavior");
        JsonElement autoClose = Object(behavior, "autoClose");
        JsonElement height = Object(behavior, "height");
        JsonElement ordering = Object(behavior, "ordering");
        OperationPanelBehavior current = configuration.Behavior;
        OperationPanelBehavior basis = fallback.Behavior;
        OperationsPanelLayout layout = configuration.Layout;
        OperationsPanelLayout fallbackLayout = fallback.Layout;
        int delay = Integer(autoClose, "delaySeconds", current.AutoCloseDelaySeconds, basis.AutoCloseDelaySeconds, 1, 60, log);
        bool pausePointer = Boolean(autoClose, "pauseOnPointer", current.PauseOnPointer, basis.PauseOnPointer, log);
        bool pauseFocus = Boolean(autoClose, "pauseOnFocus", current.PauseOnFocus, basis.PauseOnFocus, log);
        bool pausePopup = Boolean(autoClose, "pauseOnPopup", current.PauseOnPopup, basis.PauseOnPopup, log);
        double heightDefault = Number(height, "default", layout.PanelDefaultHeight, fallbackLayout.PanelDefaultHeight, 80, 2000, log);
        double heightMin = Number(height, "min", layout.PanelMinHeight, fallbackLayout.PanelMinHeight, 80, 1000, log);
        double heightMax = Number(height, "max", layout.PanelMaxHeight, fallbackLayout.PanelMaxHeight, 120, 2000, log);
        double maxFraction = Number(height, "maxFractionOfContent", current.HeightMaxFractionOfContent, basis.HeightMaxFractionOfContent, .2, .8, log);
        bool expanded = Boolean(behavior, "defaultExpanded", current.DefaultExpanded, basis.DefaultExpanded, log);
        OperationOrderingMode orderingMode = current.OrderingMode;
        if (ordering.ValueKind == JsonValueKind.Object && ordering.TryGetProperty("mode", out JsonElement mode))
        {
            string? requested = mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
            if (requested == "pendingFirst") orderingMode = OperationOrderingMode.PendingFirst;
            else if (requested == "queueOrder") orderingMode = OperationOrderingMode.QueueOrder;
            else { (log ?? Log)(new InvalidDataException("ordering.mode inválido; padrão embutido mantido."), "OperationsPanel.UserConfig"); orderingMode = basis.OrderingMode; }
        }
        var mergedLayout = layout with { PanelDefaultHeight = heightDefault, PanelMinHeight = heightMin, PanelMaxHeight = heightMax };
        var mergedBehavior = new OperationPanelBehavior(delay, pausePointer, pauseFocus, pausePopup,
            heightDefault, heightMin, heightMax, maxFraction, expanded, orderingMode);
        return configuration with { Layout = mergedLayout, Behavior = mergedBehavior };
    }

    private static void WarnRemovedBehaviorFields(JsonElement root, Action<Exception, string>? log)
    {
        JsonElement behavior = Object(root, "behavior");
        JsonElement autoClose = Object(behavior, "autoClose");
        JsonElement timing = Object(root, "timing");
        foreach ((string name, JsonElement container) in new[] { ("hoverPausesAutoClose", root), ("hoverPausesAutoClose", behavior),
                     ("hoverPausesAutoClose", autoClose), ("hoverPausesAutoClose", timing),
                     ("persistCollapsed", root), ("persistCollapsed", behavior) })
            if (container.ValueKind == JsonValueKind.Object && container.TryGetProperty(name, out _))
                (log ?? Log)(new InvalidDataException($"Campo antigo '{name}' não é mais suportado; ignorado."), "OperationsPanel.UserConfig");
    }

    private static int Integer(JsonElement node, string key, int current, int fallback, int min, int max, Action<Exception, string>? log)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out JsonElement value)) return current;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int parsed) && parsed >= min && parsed <= max) return parsed;
        (log ?? Log)(new InvalidDataException($"Valor inválido em '{key}'; padrão embutido mantido."), "OperationsPanel.UserConfig");
        return fallback;
    }

    private static bool Boolean(JsonElement node, string key, bool current, bool fallback, Action<Exception, string>? log)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out JsonElement value)) return current;
        if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
        (log ?? Log)(new InvalidDataException($"Valor inválido em '{key}'; padrão embutido mantido."), "OperationsPanel.UserConfig");
        return fallback;
    }

    private static IReadOnlyDictionary<string, OperationStatePresentationDefinition> MergeStates(
        IReadOnlyDictionary<string, OperationStatePresentationDefinition> current, JsonElement node,
        IReadOnlyDictionary<string, OperationStatePresentationDefinition> fallback, Action<Exception, string>? log, bool allowProgressMode)
    {
        var result = current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (JsonProperty entry in node.EnumerateObject())
        {
            if (!fallback.TryGetValue(entry.Name, out var basis) || entry.Value.ValueKind != JsonValueKind.Object)
            { (log ?? Log)(new InvalidDataException($"Estado desconhecido/inválido '{entry.Name}'; padrão mantido."), "OperationsPanel.States"); continue; }
            var now = result.GetValueOrDefault(entry.Name) ?? basis;
            string icon = StateString(entry.Value, "iconId", now.IconId, basis.IconId, OperationStatePresentationCatalog.IconIds, log);
            string color = StateString(entry.Value, "colorToken", now.ColorToken, basis.ColorToken, OperationStatePresentationCatalog.ColorTokens, log);
            string message = StateString(entry.Value, "messageColorToken", now.MessageColorToken, basis.MessageColorToken, OperationStatePresentationCatalog.ColorTokens, log);
            string track = StateString(entry.Value, "trackColorToken", now.TrackColorToken, basis.TrackColorToken, OperationStatePresentationCatalog.ColorTokens, log);
            string fill = StateString(entry.Value, "fillColorToken", now.FillColorToken, basis.FillColorToken, OperationStatePresentationCatalog.ColorTokens, log);
            double? alpha = StateAlpha(entry.Value, "backgroundAlpha", now.BackgroundAlpha, basis.BackgroundAlpha, log);
            double? messageAlpha = StateAlpha(entry.Value, "messageAlpha", now.MessageAlpha, basis.MessageAlpha, log);
            string font = StateString(entry.Value, "messageFont", now.MessageFontToken, basis.MessageFontToken, new HashSet<string>(new[] { "messageDefault", "messageMonospace" }), log);
            string weight = StateString(entry.Value, "messageFontWeight", now.MessageFontWeight, basis.MessageFontWeight, new HashSet<string>(new[] { "normal", "semibold", "bold" }), log);
            string mode = now.ProgressMode;
            bool visible = now.ProgressVisible;
            if (entry.Value.TryGetProperty("progress", out JsonElement progress) && progress.ValueKind == JsonValueKind.Object)
            {
                if (progress.TryGetProperty("visible", out var vis))
                {
                    if (vis.ValueKind is JsonValueKind.True or JsonValueKind.False) visible = vis.GetBoolean();
                    else (log ?? Log)(new InvalidDataException($"progress.visible inválido em '{entry.Name}'; padrão mantido."), "OperationsPanel.States");
                }
                if (progress.TryGetProperty("mode", out var modeValue) && modeValue.ValueKind == JsonValueKind.String)
                {
                    string requested = modeValue.GetString() ?? "";
                    if (allowProgressMode && (requested == basis.ProgressMode || (entry.Name is "queued" or "running" && requested is "indeterminate" or "value"))) mode = requested;
                    else (log ?? Log)(new InvalidDataException($"Modo de progresso não permitido em '{entry.Name}'; padrão mantido."), "OperationsPanel.States");
                }
            }
            result[entry.Name] = new(icon, color, message, track, fill, alpha, messageAlpha, font, weight, mode, visible);
        }
        var terminalColors = new[] { result["failed"].ColorToken, result["canceled"].ColorToken };
        if (terminalColors[0] == terminalColors[1])
        {
            (log ?? Log)(new InvalidDataException("Falha e cancelada não podem compartilhar colorToken; entradas terminais revertidas."), "OperationsPanel.States");
            result["failed"] = fallback["failed"]; result["canceled"] = fallback["canceled"];
        }
        return result;
    }

    private static IReadOnlyDictionary<string, string> MergeManagerIcons(IReadOnlyDictionary<string, string> current,
        JsonElement node, IReadOnlyDictionary<string, string> fallback, Action<Exception, string>? log)
    {
        var result = current.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty entry in node.EnumerateObject())
            if (fallback.ContainsKey(entry.Name) && entry.Value.ValueKind == JsonValueKind.String
                && OperationStatePresentationCatalog.ManagerIconIds.Contains(entry.Value.GetString() ?? "")) result[entry.Name] = entry.Value.GetString()!;
            else (log ?? Log)(new InvalidDataException($"Ícone de gerenciador inválido '{entry.Name}'; padrão mantido."), "OperationsPanel.ManagerIcons");
        return result;
    }
    private static string StateString(JsonElement node, string key, string current, string fallback, IReadOnlySet<string> allowed, Action<Exception, string>? log)
    {
        if (!node.TryGetProperty(key, out var value)) return current;
        if (value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString() ?? "")) return value.GetString()!;
        (log ?? Log)(new InvalidDataException($"Valor inválido em '{key}'; padrão mantido."), "OperationsPanel.States"); return fallback;
    }
    private static double? StateAlpha(JsonElement node, string key, double? current, double? fallback, Action<Exception, string>? log)
    {
        if (!node.TryGetProperty(key, out var value)) return current;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double alpha) && double.IsFinite(alpha)
            && alpha is >= 0 and <= 1 && (key != "messageAlpha" || alpha <= .8)) return alpha;
        (log ?? Log)(new InvalidDataException($"Alfa inválido em '{key}'; padrão mantido."), "OperationsPanel.States"); return fallback;
    }

    private static OperationsPanelConfiguration MergeV1(OperationsPanelConfiguration defaults, JsonElement root, Action<Exception, string>? log)
    {
        JsonElement legacy = Object(root, "metrics");
        foreach (string obsolete in new[] { "rowCornerRadius", "descriptionCornerRadius" })
            if (legacy.ValueKind == JsonValueKind.Object && legacy.TryGetProperty(obsolete, out _))
                (log ?? Log)(new InvalidDataException($"Campo schema 1 '{obsolete}' obsoleto; ignorado. O raio padrão continua 4."), "OperationsPanel.UserConfig");

        JsonElement layout = LegacyObject(legacy,
            ("panelMinHeight", "panelMinHeight"), ("panelMaxHeight", "panelMaxHeight"), ("panelDefaultHeight", "panelDefaultHeight"),
            ("rowHeight", "rowHeight"), ("rowSpacing", "rowSpacing"), ("rowHorizontalPadding", "rowHorizontalPadding"),
            ("rowVerticalPadding", "rowVerticalPadding"), ("iconColumnWidth", "iconColumnWidth"), ("iconSize", "iconSize"),
            ("rowIconHeight", "rowIconHeight"), ("flexibleColumnSpacing", "flexibleColumnSpacing"),
            ("titleMaxWidthFraction", "titleMaxWidthFraction"), ("titleMaxWidth", "titleMaxWidth"), ("titleMinWidth", "titleMinWidth"),
            ("descriptionMinWidth", "descriptionMinWidth"), ("descriptionHeight", "descriptionHeight"),
            ("actionButtonWidth", "actionButtonWidth"), ("moreButtonWidth", "moreButtonWidth"), ("actionButtonHeight", "actionButtonHeight"));
        JsonElement theme = LegacyObject(legacy, ("descriptionBackgroundAlpha", "messageBackgroundAlpha"));
        JsonElement metrics = LegacyObject(legacy,
            ("progressHeight", "progressHeight"), ("descriptionProgressTrackHeight", "progressTrackHeight"),
            ("descriptionPaddingHorizontal", "messagePaddingHorizontal"), ("descriptionPaddingTop", "messagePaddingTop"),
            ("descriptionPaddingBottom", "messagePaddingBottom"), ("titleFontSize", "titleFontSize"),
            ("descriptionFontSize", "messageFontSize"), ("actionFontSize", "actionFontSize"),
            ("messageFontFamily", "messageFontFamily"));

        OperationsPanelConfiguration merged = MergeV2(defaults, new JsonObjectRoot(layout, theme, metrics).AsElement(), defaults, log);
        int autoClose = defaults.Behavior.AutoCloseDelaySeconds;
        JsonElement timing = Object(root, "timing");
        if (timing.ValueKind == JsonValueKind.Object && timing.TryGetProperty("autoCloseSeconds", out JsonElement seconds))
        {
            if (seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt32(out int value) && value is >= 1 and <= 60) autoClose = value;
            else (log ?? Log)(new InvalidDataException("Valor inválido em timing 'autoCloseSeconds'; padrão mantido."), "OperationsPanel.UserConfig");
        }
        return merged with { Behavior = merged.Behavior with { AutoCloseDelaySeconds = autoClose } };
    }

    private sealed class JsonObjectRoot(JsonElement layout, JsonElement theme, JsonElement metrics)
    {
        public JsonElement AsElement()
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2);
                writer.WritePropertyName("layout"); layout.WriteTo(writer);
                writer.WritePropertyName("theme"); theme.WriteTo(writer);
                writer.WritePropertyName("metrics"); metrics.WriteTo(writer);
                writer.WriteEndObject();
            }
            using JsonDocument document = JsonDocument.Parse(stream.ToArray());
            return document.RootElement.Clone();
        }
    }

    private static JsonElement LegacyObject(JsonElement source, params (string Old, string New)[] fields)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (source.ValueKind == JsonValueKind.Object)
            foreach ((string oldName, string newName) in fields)
                if (source.TryGetProperty(oldName, out JsonElement value)) values[newName] = value.Clone();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach ((string key, JsonElement value) in values) { writer.WritePropertyName(key); value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static JsonElement Object(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object ? value : default;

    private static double Number(JsonElement node, string key, double current, double fallback, double min, double max, Action<Exception, string>? log)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out JsonElement value)) return current;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) && number >= min && number <= max) return number;
        (log ?? Log)(new InvalidDataException($"Valor inválido em '{key}'; padrão embutido mantido."), "OperationsPanel.UserConfig");
        return fallback;
    }

    private static string String(JsonElement node, string key, string current, string fallback, int minLength, int maxLength, Action<Exception, string>? log)
    {
        if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(key, out JsonElement value)) return current;
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        if (!string.IsNullOrWhiteSpace(text) && text.Length >= minLength && text.Length <= maxLength) return text;
        (log ?? Log)(new InvalidDataException($"Valor inválido em '{key}'; padrão embutido mantido."), "OperationsPanel.UserConfig");
        return fallback;
    }

    private static OperationsPanelConfiguration NormalizePanelBounds(OperationsPanelConfiguration value, OperationsPanelConfiguration fallback, Action<Exception, string>? log)
    {
        OperationsPanelLayout l = value.Layout;
        OperationsPanelLayout d = fallback.Layout;
        if (l.PanelMinHeight > l.PanelMaxHeight)
        {
            (log ?? Log)(new InvalidDataException("panelMinHeight excede panelMaxHeight; padrões embutidos mantidos."), "OperationsPanel.UserConfig");
            l = l with { PanelMinHeight = d.PanelMinHeight, PanelMaxHeight = d.PanelMaxHeight };
        }
        if (l.PanelDefaultHeight < l.PanelMinHeight || l.PanelDefaultHeight > l.PanelMaxHeight)
        {
            (log ?? Log)(new InvalidDataException("panelDefaultHeight está fora dos limites; padrão embutido mantido."), "OperationsPanel.UserConfig");
            l = l with { PanelDefaultHeight = d.PanelDefaultHeight };
        }
        OperationsPanelConfiguration normalized = value with { Layout = l };
        return normalized with { Behavior = OperationPanelBehaviorResolver.Resolve(normalized,
            message => (log ?? Log)(new InvalidDataException(message), "OperationsPanel.UserConfig")) };
    }

    private static void Log(Exception exception, string context) => CoreLog.Warning(exception, context);

    public static OperationsPanelConfiguration BuiltInDefaults { get; } = new(
        new(120, 480, 240, 48, 8, 8, 8, 40, 32, 24, 12, .4, 260, 96, 150, 32, 110, 36, 32),
        new(4, 4, 4, 12, 2.5, 8, 7, 4, 14, 14, 14, "Segoe UI Variable Text, Segoe UI", .07, .10, .18),
        OperationActionCatalog.BuiltIn.Actions)
    {
        Behavior = new(5, true, true, true, 240, 120, 480, .45, true, OperationOrderingMode.PendingFirst)
    };
}
