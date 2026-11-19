using System.Text;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public enum OperationActionScope { Item, Header }
public enum OperationActionPlacement { Primary, Menu }
public enum OperationUnavailableBehavior { Hide, Disable }

public sealed record OperationActionDefinition(
    string Id, OperationActionScope Scope, string CommandId, string TextKey, string IconId,
    OperationActionPlacement Placement, string Group, int Order,
    IReadOnlyList<OperationState> States, IReadOnlyList<string> VisibleWhen,
    IReadOnlyList<string> EnabledWhen, OperationUnavailableBehavior WhenUnavailable,
    string? ParentId = null, bool Locked = false, bool Visible = true);

public sealed record OperationActionContext(
    OperationActionScope Scope,
    OperationState State = OperationState.Queued,
    OperationKind Kind = OperationKind.Install,
    bool HasPackage = false,
    bool HasInstallLocation = false,
    bool CanCancel = false,
    bool CanReplay = false,
    bool HasFailed = false,
    bool HasSucceeded = false,
    bool HasFinished = false,
    bool HasCancelable = false,
    string? Manager = null,
    bool IsElevated = false,
    bool IsInteractive = false,
    bool CanReplayElevated = false,
    bool CanReplayInteractive = false,
    bool CanReplaySkipIntegrity = false,
    bool IsDownloadHashMismatch = false);

public sealed record ResolvedAction(
    string Id, string TextKey, string IconId, bool Visible, bool Enabled,
    string Group, int Order, OperationActionPlacement Placement, string? ParentId,
    string CommandId, OperationActionScope Scope, bool Locked, bool IsSeparator);

public sealed record OperationActionCatalog(IReadOnlyList<OperationActionDefinition> Actions)
{
    public static OperationActionCatalog BuiltIn { get; } = new(CreateDefaults());

    public static OperationActionCatalog Load(string embeddedJson, string? userJson = null,
        Action<string>? log = null)
    {
        IReadOnlyList<OperationActionDefinition> defaults = ParseDefinitions(embeddedJson, log);
        if (string.IsNullOrWhiteSpace(userJson)) return new(defaults);
        try
        {
            using JsonDocument document = JsonDocument.Parse(userJson);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("schemaVersion", out JsonElement schema) || schema.GetInt32() != 2)
                throw new InvalidDataException("operations-panel.json actions requires schemaVersion 2.");
            if (!root.TryGetProperty("actions", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("O bloco actions deve ser um array.");

            var replacements = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("id", out JsonElement idValue)
                    || idValue.ValueKind != JsonValueKind.String)
                {
                    log?.Invoke("Entrada actions sem ID válido ignorada.");
                    continue;
                }
                string id = idValue.GetString()!;
                if (!defaults.Any(action => action.Id == id))
                {
                    log?.Invoke($"Ação desconhecida ignorada: {id}");
                    continue;
                }
                if (!replacements.TryAdd(id, entry)) log?.Invoke($"Ação duplicada ignorada: {id}");
            }

            var merged = defaults.Select(action =>
            {
                if (!replacements.TryGetValue(action.Id, out JsonElement entry)) return action;
                return MergeOverride(action, entry, log);
            }).ToArray();
            if (!HasRequiredPrimaryActions(merged))
            {
                log?.Invoke("A sobreposição removeria a ação principal de algum estado; usando ações padrão.");
                return new(defaults);
            }
            return new(merged);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or InvalidOperationException or FormatException)
        {
            log?.Invoke($"Sobreposição actions inválida; usando padrões. {exception.Message}");
            return new(defaults);
        }
    }

    public static IReadOnlyList<OperationActionDefinition> ParseDefinitions(string json, Action<string>? log = null)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("schemaVersion", out JsonElement schema) || schema.GetInt32() != 2)
            throw new InvalidDataException("operations-panel.json actions requires schemaVersion 2.");
        if (!root.TryGetProperty("actions", out JsonElement entries)) return CreateDefaults();
        if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("O bloco actions deve ser um array.");
        var defaults = CreateDefaults().ToDictionary(action => action.Id, StringComparer.Ordinal);
        var result = new Dictionary<string, OperationActionDefinition>(StringComparer.Ordinal);
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            try
            {
                if (!entry.TryGetProperty("id", out JsonElement idValue) || idValue.ValueKind != JsonValueKind.String
                    || !defaults.TryGetValue(idValue.GetString()!, out OperationActionDefinition? action))
                    throw new InvalidDataException("Ação desconhecida ou sem ID ignorada.");
                OperationActionDefinition parsed = ParseDefinition(entry);
                if (!IsImmutableEquivalent(action, parsed))
                    throw new InvalidDataException($"Campos bloqueados da ação '{action.Id}' diferem do allowlist embutido.");
                if (action.Locked && (!parsed.Visible || parsed.Group != action.Group || parsed.Placement != OperationActionPlacement.Primary))
                    throw new InvalidDataException($"Ação principal locked não pode ser ocultada ou movida: {action.Id}.");
                result[action.Id] = action with
                {
                    Group = parsed.Group,
                    Order = parsed.Order,
                    IconId = parsed.IconId,
                    TextKey = parsed.TextKey,
                    Visible = parsed.Visible,
                    Locked = action.Locked,
                    Placement = action.Locked ? OperationActionPlacement.Primary : parsed.Placement
                };
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
            { log?.Invoke(exception.Message); }
        }
        return CreateDefaults().Select(action => result.GetValueOrDefault(action.Id, action)).ToArray();
    }

    public OperationActionCatalog Merge(string? userJson, Action<string>? log = null)
    {
        string embedded = SerializeDefinitions(Actions);
        return Load(embedded, userJson, log);
    }

    public static string SerializeDefinitions(IEnumerable<OperationActionDefinition> actions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteNumber("schemaVersion", 2); writer.WriteStartArray("actions");
            foreach (OperationActionDefinition action in actions)
            {
                writer.WriteStartObject();
                writer.WriteString("id", action.Id); writer.WriteString("scope", ScopeName(action.Scope));
                writer.WriteString("commandId", action.CommandId); writer.WriteString("textKey", action.TextKey);
                writer.WriteString("iconId", action.IconId); writer.WriteString("placement", action.Placement.ToString().ToLowerInvariant());
                writer.WriteString("group", action.Group); writer.WriteNumber("order", action.Order);
                writer.WriteStartArray("states"); foreach (OperationState state in action.States) writer.WriteStringValue(state.ToString()); writer.WriteEndArray();
                writer.WriteStartArray("visibleWhen"); foreach (string condition in action.VisibleWhen) writer.WriteStringValue(condition); writer.WriteEndArray();
                writer.WriteStartArray("enabledWhen"); foreach (string condition in action.EnabledWhen) writer.WriteStringValue(condition); writer.WriteEndArray();
                writer.WriteString("whenUnavailable", UnavailableName(action.WhenUnavailable));
                if (action.ParentId is not null) writer.WriteString("parentId", action.ParentId);
                writer.WriteBoolean("locked", action.Locked); writer.WriteBoolean("visible", action.Visible);
                writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static OperationActionDefinition MergeOverride(OperationActionDefinition action, JsonElement entry, Action<string>? log)
    {
        try
        {
            string? commandId = OptionalString(entry, "commandId");
            string? scope = OptionalString(entry, "scope");
            string? parentId = OptionalString(entry, "parentId");
            bool hasStates = entry.TryGetProperty("states", out _);
            bool hasConditions = entry.TryGetProperty("visibleWhen", out _) || entry.TryGetProperty("enabledWhen", out _);
            foreach (string condition in OptionalStringArray(entry, "visibleWhen").Concat(OptionalStringArray(entry, "enabledWhen")))
                if (!OperationActionConditionEvaluator.IsConditionRegistered(condition))
                    throw new InvalidDataException($"Condição desconhecida: {condition}");
            bool lockedFlag = entry.TryGetProperty("locked", out JsonElement lockedElement) && lockedElement.GetBoolean();
            if ((commandId is not null && commandId != action.CommandId) || (scope is not null && scope != ScopeName(action.Scope))
                || (parentId is not null && parentId != action.ParentId) || hasStates || hasConditions
                || (lockedFlag != action.Locked && entry.TryGetProperty("locked", out _)))
                throw new InvalidDataException($"Campos não sobrescrevíveis ignorados na ação '{action.Id}'.");
            string? placement = OptionalString(entry, "placement");
            string? whenUnavailable = OptionalString(entry, "whenUnavailable");
            if ((action.Locked && placement is not null && placement != "primary")
                || (whenUnavailable is not null && whenUnavailable != UnavailableName(action.WhenUnavailable)))
                throw new InvalidDataException($"Ação locked ou estratégia de indisponibilidade não pode ser alterada: {action.Id}.");
            string iconId = OptionalString(entry, "iconId") ?? action.IconId;
            string textKey = OptionalString(entry, "textKey") ?? action.TextKey;
            if (!OperationActionRegistry.IsIconRegistered(iconId)) throw new InvalidDataException($"Ícone desconhecido: {iconId}");
            if (!OperationActionRegistry.IsTextKeyRegistered(textKey))
            {
                log?.Invoke($"Chave de texto desconhecida '{textKey}' na ação '{action.Id}'; mantendo o padrão.");
                textKey = action.TextKey;
            }
            bool visible = entry.TryGetProperty("visible", out JsonElement visibleElement) ? visibleElement.GetBoolean() : action.Visible;
            string group = OptionalString(entry, "group") ?? action.Group;
            int order = entry.TryGetProperty("order", out JsonElement orderElement) ? orderElement.GetInt32() : action.Order;
            if (action.Locked && (!visible || group != action.Group || (placement is not null && placement != "primary")))
                throw new InvalidDataException($"Ação principal locked não pode ser ocultada ou movida: {action.Id}.");
            return action with { Group = group, Order = order, IconId = iconId, TextKey = textKey, Visible = visible };
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            log?.Invoke(exception.Message);
            return action;
        }
    }

    private static OperationActionDefinition ParseDefinition(JsonElement entry)
    {
        string id = RequiredString(entry, "id");
        string scopeText = RequiredString(entry, "scope");
        string placementText = RequiredString(entry, "placement");
        string unavailableText = RequiredString(entry, "whenUnavailable");
        string[] states = StringArray(entry, "states");
        string[] visibleWhen = StringArray(entry, "visibleWhen");
        string[] enabledWhen = StringArray(entry, "enabledWhen");
        return new(id,
            Enum.Parse<OperationActionScope>(scopeText, true),
            RequiredString(entry, "commandId"), RequiredString(entry, "textKey"), RequiredString(entry, "iconId"),
            Enum.Parse<OperationActionPlacement>(placementText, true), RequiredString(entry, "group"),
            entry.GetProperty("order").GetInt32(), states.Select(ParseState).ToArray(), visibleWhen, enabledWhen,
            Enum.Parse<OperationUnavailableBehavior>(unavailableText, true), OptionalString(entry, "parentId"),
            entry.TryGetProperty("locked", out JsonElement locked) && locked.GetBoolean(),
            !entry.TryGetProperty("visible", out JsonElement visible) || visible.GetBoolean());
    }

    private static bool IsImmutableEquivalent(OperationActionDefinition expected, OperationActionDefinition actual) =>
        expected.Id == actual.Id && expected.Scope == actual.Scope && expected.CommandId == actual.CommandId
        && expected.Placement == actual.Placement && expected.States.SequenceEqual(actual.States)
        && expected.VisibleWhen.SequenceEqual(actual.VisibleWhen) && expected.EnabledWhen.SequenceEqual(actual.EnabledWhen)
        && expected.WhenUnavailable == actual.WhenUnavailable && expected.ParentId == actual.ParentId && expected.Locked == actual.Locked;

    private static bool HasRequiredPrimaryActions(IEnumerable<OperationActionDefinition> actions)
    {
        OperationActionDefinition[] primary = actions.Where(action => action.Scope == OperationActionScope.Item
            && action.Placement == OperationActionPlacement.Primary && action.Visible).ToArray();
        return new[] { OperationState.Queued, OperationState.Running, OperationState.Completed, OperationState.Failed,
            OperationState.Canceled, OperationState.Unconfirmed }.All(state => primary.Any(action => action.States.Contains(state)))
            && primary.Any(action => action.Id == "cancel" && action.States.Contains(OperationState.Queued))
            && primary.Any(action => action.Id == "cancel" && action.States.Contains(OperationState.Running))
            && new[] { OperationState.Completed, OperationState.Failed, OperationState.Canceled, OperationState.Unconfirmed }
                .All(state => primary.Any(action => action.Id == "close" && action.States.Contains(state)));
    }

    private static List<OperationActionDefinition> CreateDefaults()
    {
        var actions = new List<OperationActionDefinition>();
        void Add(string id, string scope, string command, string text, string icon, string placement, string group,
            int order, string[] states, string[] visible, string[] enabled, string unavailable, bool locked = false) =>
            actions.Add(new(id, scope == "item" ? OperationActionScope.Item : OperationActionScope.Header, command, text,
                icon, placement == "primary" ? OperationActionPlacement.Primary : OperationActionPlacement.Menu, group,
                order, states.Select(ParseState).ToArray(), visible, enabled,
                unavailable == "hide" ? OperationUnavailableBehavior.Hide : OperationUnavailableBehavior.Disable,
                null, locked));
        string[] finished = ["Completed", "Failed", "Canceled", "Unconfirmed"];
        string[] all = ["Queued", "Running", "Completed", "Failed", "Canceled", "Unconfirmed"];
        Add("cancel", "item", "cancelOperation", "operations.button.cancel", "dismiss", "primary", "primary", 0,
            ["Queued", "Running"], [], [], "hide", true);
        Add("close", "item", "removeOperation", "operations.button.close", "dismiss", "primary", "primary", 1,
            finished, [], [], "hide", true);
        Add("output", "item", "showOperationOutput", "operations.menu.item.output", "document", "menu", "operation", 0, all, [], [], "hide");
        Add("packageDetails", "item", "showPackageDetails", "operations.menu.item.packageDetails", "info", "menu", "operation", 10, all, [], ["hasPackage"], "disable");
        Add("packageOptions", "item", "showPackageOptions", "operations.menu.item.installOptions", "settings", "menu", "operation", 20, all, [], ["hasPackage"], "disable");
        Add("installLocation", "item", "openInstallLocation", "operations.menu.item.installLocation", "folderOpen", "menu", "operation", 30, all, [], ["hasInstallLocation"], "disable");
        Add("retry", "item", "replayOperation", "operations.menu.item.retry", "arrowClockwise", "menu", "operation", 40, finished, ["canReplay"], [], "hide");
        Add("replayElevated", "item", "replayOperationVariant:elevated", "operations.menu.item.replayElevated", "shield", "menu", "operation", 50, finished, ["canReplayElevated"], [], "hide");
        Add("replayInteractive", "item", "replayOperationVariant:interactive", "operations.menu.item.replayInteractive", "desktop", "menu", "operation", 60, finished, ["canReplayInteractive"], [], "hide");
        Add("cancelOperation", "item", "cancelOperation", "operations.menu.item.cancel", "dismiss", "menu", "operation", 70, ["Queued", "Running"], ["canCancel"], [], "hide");
        Add("remove", "item", "removeOperation", "operations.menu.item.remove", "delete", "menu", "operation", 80, finished, [], [], "hide");
        Add("retryFailed", "header", "replayFailed", "operations.menu.header.retryFailed", "arrowClockwise", "menu", "bulk", 0, all, [], ["hasFailed"], "disable");
        Add("clearSuccessful", "header", "clearSucceeded", "operations.menu.header.clearSuccessful", "checkmark", "menu", "bulk", 10, all, [], ["hasSucceeded"], "disable");
        Add("clearFinished", "header", "clearFinished", "operations.menu.header.clearFinished", "dismiss", "menu", "bulk", 20, all, [], ["hasFinished"], "disable");
        Add("separator.cancelAll", "header", "separator", "", "none", "menu", "separator", 30, all, [], [], "disable");
        Add("cancelAll", "header", "cancelAll", "operations.menu.header.cancelAll", "dismiss", "menu", "bulk", 40, all, [], ["hasCancelable"], "disable");
        return actions;
    }

    private static OperationState ParseState(string value) => Enum.Parse<OperationState>(value, true);
    private static string RequiredString(JsonElement entry, string name) => entry.GetProperty(name).GetString()
        ?? throw new InvalidDataException($"Campo {name} deve ser texto.");
    private static string? OptionalString(JsonElement entry, string name) => entry.TryGetProperty(name, out JsonElement value)
        ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new InvalidDataException($"Campo {name} deve ser texto.")
        : null;
    private static string[] StringArray(JsonElement entry, string name) => entry.GetProperty(name).EnumerateArray()
        .Select(value => value.GetString() ?? throw new InvalidDataException($"Campo {name} contém item não textual.")).ToArray();
    private static string[] OptionalStringArray(JsonElement entry, string name) => entry.TryGetProperty(name, out JsonElement value)
        ? StringArray(entry, name) : [];
    private static string ScopeName(OperationActionScope value) => value == OperationActionScope.Item ? "item" : "header";
    private static string UnavailableName(OperationUnavailableBehavior value) => value.ToString().ToLowerInvariant();
}

public static class OperationActionRegistry
{
    private static readonly HashSet<string> TextKeys = new(StringComparer.Ordinal)
    {
        "operations.button.cancel", "operations.button.close", "operations.menu.item.output",
        "operations.menu.item.packageDetails", "operations.menu.item.installOptions", "operations.menu.item.installLocation",
        "operations.menu.item.retry", "operations.menu.item.replayElevated", "operations.menu.item.replayInteractive",
        "operations.menu.item.cancel", "operations.menu.item.remove", "operations.menu.header.retryFailed",
        "operations.menu.header.clearSuccessful", "operations.menu.header.clearFinished", "operations.menu.header.cancelAll"
    };
    private static readonly HashSet<string> Commands = new(StringComparer.Ordinal)
    {
        "showOperationOutput", "showPackageDetails", "showPackageOptions", "openInstallLocation", "replayOperation",
        "replayOperationVariant:elevated", "replayOperationVariant:interactive", "cancelOperation", "removeOperation",
        "replayFailed", "clearSucceeded", "clearFinished", "cancelAll", "separator"
    };
    private static readonly HashSet<string> Icons = new(StringComparer.Ordinal)
    {
        "none", "document", "info", "settings", "folderOpen", "arrowClockwise", "shield", "desktop", "dismiss", "delete", "checkmark"
    };

    public static IReadOnlyCollection<string> RegisteredCommands => Commands;
    public static IReadOnlyCollection<string> RegisteredIcons => Icons;
    public static bool IsCommandRegistered(string id) => Commands.Contains(id);
    public static bool IsIconRegistered(string id) => Icons.Contains(id);
    public static bool IsTextKeyRegistered(string id) => TextKeys.Contains(id);
    public static string? GetUnavailableTooltipKey(string actionId) => actionId switch
    {
        "packageDetails" or "packageOptions" => "operations.tooltip.packageOnly",
        "installLocation" => "operations.tooltip.installLocationMissing",
        _ => null
    };
}

public interface IOperationCommandRegistry
{
    IReadOnlyCollection<string> RegisteredCommands { get; }
    void Register(string commandId, Func<OperationActionContext, bool> canExecute, Func<object?, Task> execute);
    bool CanExecute(string commandId, OperationActionContext context);
    Task ExecuteAsync(string commandId, OperationActionContext context, object? target);
}

public sealed class OperationCommandRegistry : IOperationCommandRegistry
{
    private readonly Dictionary<string, (Func<OperationActionContext, bool> CanExecute, Func<object?, Task> Execute)> _commands = new(StringComparer.Ordinal);
    public IReadOnlyCollection<string> RegisteredCommands => _commands.Keys;

    public void Register(string commandId, Func<OperationActionContext, bool> canExecute, Func<object?, Task> execute)
    {
        if (!OperationActionRegistry.IsCommandRegistered(commandId))
            throw new InvalidOperationException($"Tentativa de registrar comando fora do allowlist: {commandId}");
        if (!_commands.TryAdd(commandId, (canExecute, execute)))
            throw new InvalidOperationException($"Comando duplicado: {commandId}");
    }

    public bool CanExecute(string commandId, OperationActionContext context) =>
        _commands.TryGetValue(commandId, out var command) && command.CanExecute(context);

    public Task ExecuteAsync(string commandId, OperationActionContext context, object? target)
    {
        if (!_commands.TryGetValue(commandId, out var command))
            throw new KeyNotFoundException($"Comando de operação não registrado: {commandId}");
        if (!command.CanExecute(context)) return Task.CompletedTask;
        return command.Execute(target);
    }
}

public sealed class OperationActionConditionEvaluator
{
    private static readonly HashSet<string> Conditions = new(StringComparer.Ordinal)
    {
        "isQueued", "isRunning", "isFinished", "isSucceeded", "isFailed", "isCanceled", "isUnconfirmed",
        "canCancel", "canReplay", "hasPackage", "hasInstallLocation", "isWinget", "isChocolatey",
        "canReplayElevated", "canReplayInteractive", "hasFailed", "hasSucceeded", "hasFinished", "hasCancelable"
    };

    public static IReadOnlyCollection<string> RegisteredConditions => Conditions;
    public static bool IsConditionRegistered(string id) => Conditions.Contains(id);

    public static bool Evaluate(string id, OperationActionContext context) => id switch
    {
        "isQueued" => context.State == OperationState.Queued,
        "isRunning" => context.State == OperationState.Running,
        "isFinished" => context.State is OperationState.Completed or OperationState.Failed or OperationState.Canceled or OperationState.Unconfirmed,
        "isSucceeded" => context.State == OperationState.Completed,
        "isFailed" => context.State == OperationState.Failed,
        "isCanceled" => context.State == OperationState.Canceled,
        "isUnconfirmed" => context.State == OperationState.Unconfirmed,
        "canCancel" => context.CanCancel,
        "canReplay" => context.CanReplay,
        "hasPackage" => context.HasPackage,
        "hasInstallLocation" => context.HasInstallLocation,
        "isWinget" => context.Manager == "winget",
        "isChocolatey" => context.Manager == "chocolatey",
        "canReplayElevated" => context.CanReplayElevated,
        "canReplayInteractive" => context.CanReplayInteractive,
        "hasFailed" => context.HasFailed,
        "hasSucceeded" => context.HasSucceeded,
        "hasFinished" => context.HasFinished,
        "hasCancelable" => context.HasCancelable,
        _ => throw new KeyNotFoundException($"Condição de ação não registrada: {id}")
    };

    public static bool All(IEnumerable<string> conditions, OperationActionContext context) =>
        conditions.All(condition => Evaluate(condition, context));
}

public interface IOperationConditionEvaluator
{
    IReadOnlyCollection<string> RegisteredConditions { get; }
    bool IsConditionRegistered(string conditionId);
    bool Evaluate(string conditionId, OperationActionContext context);
    bool All(IEnumerable<string> conditionIds, OperationActionContext context);
}

public sealed class OperationConditionEvaluator : IOperationConditionEvaluator
{
    public IReadOnlyCollection<string> RegisteredConditions => OperationActionConditionEvaluator.RegisteredConditions;
    public bool IsConditionRegistered(string conditionId) => OperationActionConditionEvaluator.IsConditionRegistered(conditionId);
    public bool Evaluate(string conditionId, OperationActionContext context) => OperationActionConditionEvaluator.Evaluate(conditionId, context);
    public bool All(IEnumerable<string> conditionIds, OperationActionContext context) => OperationActionConditionEvaluator.All(conditionIds, context);
}

public static class OperationActionResolver
{
    public static IReadOnlyList<ResolvedAction> Resolve(OperationActionScope scope, OperationActionContext context,
        OperationActionCatalog catalog, IOperationConditionEvaluator? conditions = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (context.Scope != scope) throw new ArgumentException("Scope do contexto não corresponde ao resolver.", nameof(context));
        conditions ??= new OperationConditionEvaluator();
        return catalog.Actions.Where(action => action.Scope == scope)
            .OrderBy(action => action.Order).ThenBy(action => action.Group, StringComparer.Ordinal)
            .Select(action => ResolveOne(action, context, conditions))
            .ToArray();
    }

    private static ResolvedAction ResolveOne(OperationActionDefinition action, OperationActionContext context,
        IOperationConditionEvaluator conditions)
    {
        bool stateMatch = action.States.Contains(context.State);
        bool visibleMatch = conditions.All(action.VisibleWhen, context);
        bool enabledMatch = conditions.All(action.EnabledWhen, context);
        bool available = action.Visible && stateMatch && visibleMatch
            && (action.WhenUnavailable != OperationUnavailableBehavior.Hide || enabledMatch);
        bool enabled = available && enabledMatch;
        bool visible = action.WhenUnavailable == OperationUnavailableBehavior.Disable || available;
        return new(action.Id, action.TextKey, action.IconId, visible, enabled, action.Group, action.Order,
            action.Placement, action.ParentId, action.CommandId, action.Scope, action.Locked, action.CommandId == "separator");
    }
}
