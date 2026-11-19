using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public sealed record ElevationSettingsGroup(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("titleTextKey")] string TitleTextKey,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("page")] string Page = "elevator",
    [property: JsonPropertyName("helpTextKey")] string? HelpTextKey = null,
    [property: JsonPropertyName("detailsTextKey")] string? DetailsTextKey = null,
    [property: JsonPropertyName("detailsTitleTextKey")] string? DetailsTitleTextKey = null,
    [property: JsonPropertyName("searchCategoryKey")] string? SearchCategoryKey = null,
    [property: JsonPropertyName("searchCategoryTextKey")] string? SearchCategoryTextKey = null,
    [property: JsonPropertyName("icon")] string? Icon = null);

public sealed record ElevationSettingChoice(
    [property: JsonPropertyName("value")] string Value,
    [property: JsonPropertyName("labelTextKey")] string LabelTextKey);

public sealed record ElevationSettingCondition(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("value")] bool? Value,
    [property: JsonPropertyName("risk")] string? Risk = null,
    [property: JsonPropertyName("differsFromDefault")] bool? DiffersFromDefault = null,
    [property: JsonPropertyName("minimumCount")] int? MinimumCount = null,
    [property: JsonPropertyName("valueText")] string? ValueText = null,
    [property: JsonPropertyName("values")] IReadOnlyList<string>? Values = null);

public sealed record ElevationConditionResult(bool IsValid, bool IsSatisfied);

public static class ElevationSettingConditionEvaluator
{
    public static bool IsValid(ElevationSettingCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (!string.IsNullOrWhiteSpace(condition.Key))
            return (condition.Value.HasValue ? 1 : 0)
                    + (!string.IsNullOrWhiteSpace(condition.ValueText) ? 1 : 0)
                    + (condition.Values is { Count: > 0 } ? 1 : 0) == 1
                && (condition.Values is null || condition.Values.All(value => !string.IsNullOrWhiteSpace(value)))
                && condition.Risk is null
                && condition.DiffersFromDefault is null && condition.MinimumCount is null;

        return condition.Value is null && condition.ValueText is null && condition.Values is null
            && condition.Risk is "normal" or "elevated" or "dangerous"
            && condition.DiffersFromDefault.HasValue
            && condition.MinimumCount is >= 1;
    }

    public static ElevationConditionResult Evaluate(
        ElevationSettingCondition condition,
        IReadOnlyList<ElevationSettingDefinition> settings,
        IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(values);
        if (!IsValid(condition))
            return new(false, false);

        if (!string.IsNullOrWhiteSpace(condition.Key))
            return new(true, values.TryGetValue(condition.Key, out string? value)
                && (condition.Value.HasValue
                    ? bool.TryParse(value, out bool booleanValue) && booleanValue == condition.Value
                    : condition.Values is { Count: > 0 }
                        ? condition.Values.Contains(value, StringComparer.Ordinal)
                        : string.Equals(value, condition.ValueText, StringComparison.Ordinal)));

        int matches = settings.Count(setting =>
            setting.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase)
            && setting.Risk.Equals(condition.Risk, StringComparison.OrdinalIgnoreCase)
            && setting.DefaultBooleanValue is bool defaultValue
            && values.TryGetValue(setting.Key, out string? currentValue)
            && bool.TryParse(currentValue, out bool currentBoolean)
            && (currentBoolean != defaultValue) == condition.DiffersFromDefault);
        return new(true, matches >= condition.MinimumCount!.Value);
    }

    public static ElevationConditionResult Evaluate(
        ElevationSettingCondition condition,
        IReadOnlyList<ElevationSettingDefinition> settings,
        IReadOnlyDictionary<string, bool> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Evaluate(condition, settings, values.ToDictionary(
            pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase));
    }
}

[method: JsonConstructor]
public sealed record ElevationSettingDefinition(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("group")] string Group,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("risk")] string Risk,
    [property: JsonPropertyName("defaultValue")] JsonElement DefaultValue,
    [property: JsonPropertyName("dependsOn")] ElevationSettingCondition? DependsOn,
    [property: JsonPropertyName("lockedReasonTextKey")] string? LockedReasonTextKey,
    [property: JsonPropertyName("appliesImmediately")] bool AppliesImmediately,
    [property: JsonPropertyName("requiresConfirmation")] bool RequiresConfirmation,
    [property: JsonPropertyName("titleTextKey")] string TitleTextKey,
    [property: JsonPropertyName("descriptionTextKey")] string DescriptionTextKey,
    [property: JsonPropertyName("riskWarningTextKey")] string? RiskWarningTextKey,
    [property: JsonPropertyName("control")] string? Control = null,
    [property: JsonPropertyName("options")] IReadOnlyList<ElevationSettingChoice>? Options = null,
    [property: JsonPropertyName("confirmationWhen")] string? ConfirmationWhen = null,
    [property: JsonPropertyName("storageKey")] string? StorageKey = null,
    [property: JsonPropertyName("actionKey")] string? ActionKey = null,
    [property: JsonPropertyName("actionTextKey")] string? ActionTextKey = null,
    [property: JsonPropertyName("selectTextKey")] string? SelectTextKey = null,
    [property: JsonPropertyName("resetTextKey")] string? ResetTextKey = null,
    [property: JsonPropertyName("searchTerms")] string? SearchTerms = null,
    [property: JsonPropertyName("icon")] string? Icon = null,
    [property: JsonPropertyName("stateValueKey")] string? StateValueKey = null,
    [property: JsonPropertyName("maxLength")] int? MaxLength = null,
    [property: JsonPropertyName("visibleWhen")] ElevationSettingCondition? VisibleWhen = null,
    [property: JsonPropertyName("enabledTextKey")] string? EnabledTextKey = null,
    [property: JsonPropertyName("disabledTextKey")] string? DisabledTextKey = null,
    [property: JsonPropertyName("targetPage")] string? TargetPage = null,
    [property: JsonPropertyName("requiresUpdateCapability")] bool RequiresUpdateCapability = false,
    [property: JsonPropertyName("capabilityDescriptionTextKey")] string? CapabilityDescriptionTextKey = null,
    [property: JsonPropertyName("descriptionProviderId")] string? DescriptionProviderId = null,
    [property: JsonPropertyName("preferenceProviderId")] string? PreferenceProviderId = null)
{
    public string EffectiveControl => Control ?? (Kind.Equals("preference", StringComparison.OrdinalIgnoreCase)
        ? "toggle"
        : Kind.Equals("navigation", StringComparison.OrdinalIgnoreCase) ? "navigation" : "info");
    public bool? DefaultBooleanValue => DefaultValue.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    public string DefaultTextValue => DefaultValue.ValueKind switch
    {
        JsonValueKind.String => DefaultValue.GetString() ?? string.Empty,
        JsonValueKind.True => bool.TrueString,
        JsonValueKind.False => bool.FalseString,
        JsonValueKind.Number => DefaultValue.GetRawText(),
        _ => string.Empty
    };

    public ElevationSettingDefinition(
        string key, string group, string kind, string risk, bool? defaultValue,
        ElevationSettingCondition? dependsOn, string? lockedReasonTextKey, bool appliesImmediately,
        bool requiresConfirmation, string titleTextKey, string descriptionTextKey, string? riskWarningTextKey)
        : this(key, group, kind, risk,
            defaultValue.HasValue
                ? JsonSerializer.SerializeToElement(defaultValue.Value, ElevationSettingsCatalogJsonContext.Default.Boolean)
                : default,
            dependsOn, lockedReasonTextKey, appliesImmediately, requiresConfirmation,
            titleTextKey, descriptionTextKey, riskWarningTextKey)
    {
    }
}

public sealed record ElevationSettingsCatalogDocument(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("groups")] IReadOnlyList<ElevationSettingsGroup> Groups,
    [property: JsonPropertyName("settings")] IReadOnlyList<ElevationSettingDefinition> Settings);

public sealed record ElevationSettingPresentation(
    bool ShowToggle,
    bool IsEnabled,
    bool IsDimmed,
    bool ShowRiskWarning,
    bool RequiresConfirmation,
    bool ShowCombo = false,
    bool ShowText = false,
    bool ShowAction = false,
    bool ShowNavigation = false,
    bool IsVisible = true);

public sealed class ElevationSettingsCatalog
{
    private static readonly ElevationSettingsGroup[] FallbackGroups =
    [
        new("adminRequests", "group.adminRequests", 0, SearchCategoryKey: "Elevator", SearchCategoryTextKey: "search.category.elevator"),
        new("installationRestrictions", "group.installationRestrictions", 1, SearchCategoryKey: "Elevator", SearchCategoryTextKey: "search.category.elevator"),
        new("concurrency", "group.concurrency", 0, "operations", "group.concurrency.help",
            SearchCategoryKey: "Execution", SearchCategoryTextKey: "search.category.operations"),
        new("installerDownloads", "group.installerDownloads", 1, "operations",
            SearchCategoryKey: "Execution", SearchCategoryTextKey: "search.category.operations"),
        new("portableSettings", "group.portableSettings", 2, "operations", "group.portableSettings.help",
            "group.portableSettings.details", "group.portableSettings.detailsTitle",
            "Execution", "search.category.operations"),
        new("searchNotifications", "search.category.notifications", 0, "settings-search", SearchCategoryKey: "Notifications"),
        new("searchGeneral", "search.category.general", 1, "settings-search", SearchCategoryKey: "General"),
        new("searchInterface", "search.category.interface", 2, "settings-search", SearchCategoryKey: "Interface"),
        new("searchOperations", "search.category.cache", 3, "settings-search", SearchCategoryKey: "Operations"),
        new("searchPackageManagers", "search.category.packageManagers", 4, "settings-search", SearchCategoryKey: "PackageManagers"),
        new("maintenanceTasks", "maintenance.group.tasks", 0, "maintenance",
            DetailsTitleTextKey: "maintenance.group.detailsTitle",
            SearchCategoryKey: "Maintenance", SearchCategoryTextKey: "search.category.maintenance", Icon: "\uE823"),
        new("generalUpdater", "general.group.updater", 0, "general",
            SearchCategoryKey: "General", SearchCategoryTextKey: "search.category.general", Icon: "\uE72C"),
        new("generalManage", "general.group.manage", 1, "general",
            SearchCategoryKey: "General", SearchCategoryTextKey: "search.category.general"),
        new("generalRelated", "general.group.related", 2, "general",
            SearchCategoryKey: "General", SearchCategoryTextKey: "search.category.general"),
        new("imageCache", "cache.group.images", 0, "cache",
            DetailsTitleTextKey: "cache.images.details.title",
            DetailsTextKey: "cache.images.details",
            SearchCategoryKey: "Operations", SearchCategoryTextKey: "search.category.cache", Icon: "\uE8B7"),
        new("installerCache", "cache.group.installers", 1, "cache",
            SearchCategoryKey: "Operations", SearchCategoryTextKey: "search.category.cache", Icon: "\uE8AD")
    ];

    private static readonly ElevationSettingDefinition[] FallbackSettings =
    [
        new("allowElevation", "adminRequests", "preference", "dangerous", true, null, null, true, true,
            "setting.allowElevation.title", "setting.allowElevation.description", "setting.allowElevation.warning"),
        new("allowManualSilentArguments", "installationRestrictions", "preference", "dangerous", true, null, null, true, true,
            "setting.allowManualArguments.title", "setting.allowManualArguments.description", "setting.allowManualArguments.warning"),
        new("allowImportedSilentArguments", "installationRestrictions", "preference", "dangerous", true, null, null, true, true,
            "setting.allowExternalArguments.title", "setting.allowExternalArguments.description", "setting.allowExternalArguments.warning"),
        Combo("parallelOperations", "concurrency", "1", "ParallelOperations", "operations.parallel.title", "operations.parallel.description",
            OperationConcurrencyLimits.ParallelOperationChoices
                .Select(choice => (choice.Value, choice.LabelTextKey)).ToArray()),
        Combo("parallelDownloads", "concurrency", "3", "ParallelDownloads", "operations.downloads.parallel.title", "operations.downloads.parallel.description",
            ("1", "operations.option.1"), ("2", "operations.option.2"), ("3", "operations.option.3"), ("4", "operations.option.4"), ("6", "operations.option.6"), ("8", "operations.option.8")),
        new("downloadDirectory", "installerDownloads", "preference", "normal",
            JsonSerializer.SerializeToElement(string.Empty, ElevationSettingsCatalogJsonContext.Default.String), null, null, true, false,
            "operations.downloads.folder.title", "operations.downloads.folder.description", null,
            "folder", StorageKey: "DownloadDirectory", SelectTextKey: "operations.folder.choose", ResetTextKey: "operations.folder.default"),
        Combo("installerNames", "installerDownloads", "1", "InstallerNames", "operations.downloads.names.title", "operations.downloads.names.description",
            ("0", "operations.names.original"), ("1", "operations.names.packageVersion"), ("2", "operations.names.packageVersionArchitecture")),
        new("maintenance-enabled", "maintenanceTasks", "preference", "normal",
            JsonSerializer.SerializeToElement(false, ElevationSettingsCatalogJsonContext.Default.Boolean), null, null, true, false,
            "maintenance.enabled.title", "maintenance.enabled.description", null, "toggle-card",
            StorageKey: "AutoUpdateAtStartup", Icon: "\uE72C", StateValueKey: "maintenance-frequency",
            SearchTerms: "automático verificar atualizações tarefa agendada",
            EnabledTextKey: "maintenance.state.enabled", DisabledTextKey: "maintenance.state.disabled"),
        Combo("maintenance-frequency", "maintenanceTasks", "AtLogon", "UpdateSchedule", "maintenance.frequency.title", "maintenance.frequency.description",
            ("AtLogon", "maintenance.frequency.logon"), ("Interval", "maintenance.frequency.interval"),
            ("Daily", "maintenance.frequency.daily"), ("Weekly", "maintenance.frequency.weekly"))
            with { DependsOn = new("maintenance-enabled", true), SearchTerms = "repetição intervalo diário semanal entrar Windows" },
        Combo("maintenance-mode", "maintenanceTasks", "InstallAutomatically", "UpdateMode", "maintenance.mode.title", "maintenance.mode.description",
            ("NotifyOnly", "maintenance.mode.notify"), ("InstallAutomatically", "maintenance.mode.install"))
            with { DependsOn = new("maintenance-enabled", true), SearchTerms = "avisos instalar atualizações notificar" },
        Combo("maintenance-scope", "maintenanceTasks", "AllSupportedPackages", "UpdateScope", "maintenance.scope.title", "maintenance.scope.description",
            ("AllSupportedPackages", "maintenance.scope.all"), ("SelectedPackagesOnly", "maintenance.scope.selected"))
            with { DependsOn = new("maintenance-enabled", true), SearchTerms = "aplicativos selecionados menu instalados" },
        Combo("maintenance-interval", "maintenanceTasks", "60", "UpdateIntervalMinutes", "maintenance.interval.title", "maintenance.interval.description",
            ("15", "maintenance.interval.15"), ("30", "maintenance.interval.30"), ("60", "maintenance.interval.60"),
            ("120", "maintenance.interval.120"), ("240", "maintenance.interval.240"), ("720", "maintenance.interval.720"),
            ("1440", "maintenance.interval.1440"))
            with
            {
                DependsOn = new("maintenance-enabled", true),
                VisibleWhen = new("maintenance-frequency", null, ValueText: "Interval"),
                SearchTerms = "intervalo minutos horas"
            },
        new ElevationSettingDefinition("maintenance-time", "maintenanceTasks", "preference", "normal",
            JsonSerializer.SerializeToElement("09:00", ElevationSettingsCatalogJsonContext.Default.String),
            new("maintenance-enabled", true), null, true, false,
            "maintenance.time.title", "maintenance.time.description", null, "text",
            StorageKey: "UpdateScheduleTime", MaxLength: 5, SearchTerms: "horário hora diária")
            with { VisibleWhen = new("maintenance-frequency", null, Values: new[] { "Daily", "Weekly" }) },
        Combo("maintenance-day", "maintenanceTasks", "Monday", "UpdateScheduleDay", "maintenance.day.title", "maintenance.day.description",
            ("Sunday", "maintenance.day.sunday"), ("Monday", "maintenance.day.monday"),
            ("Tuesday", "maintenance.day.tuesday"), ("Wednesday", "maintenance.day.wednesday"),
            ("Thursday", "maintenance.day.thursday"), ("Friday", "maintenance.day.friday"),
            ("Saturday", "maintenance.day.saturday"))
            with
            {
                DependsOn = new("maintenance-enabled", true),
                VisibleWhen = new("maintenance-frequency", null, ValueText: "Weekly"),
                SearchTerms = "dia semana semanal"
            },
        new("maintenance-run", "maintenanceTasks", "info", "normal", default, null, null, true, false,
            "maintenance.run.title", "maintenance.run.description", null, "action",
            ActionKey: "runMaintenanceNow", ActionTextKey: "maintenance.run.button",
            SearchTerms: "executar agora histórico última próxima"),
        new("warnInstallerHostChange", "installerDownloads", "preference", "elevated",
            JsonSerializer.SerializeToElement(true, ElevationSettingsCatalogJsonContext.Default.Boolean), null, null, true, true,
            "operations.domain.title", "operations.domain.description", "operations.domain.warning",
            "toggle", ConfirmationWhen: "disabled", StorageKey: "WarnInstallerHostChange"),
        new("portableExport", "portableSettings", "info", "normal", default, null, null, true, false,
            "operations.portable.export.title", "operations.portable.export.description", null,
            "action", ActionKey: "exportSettings", ActionTextKey: "operations.portable.export.button"),
        new("portableImport", "portableSettings", "info", "normal", default, null, null, true, false,
            "operations.portable.import.title", "operations.portable.import.description", null,
            "action", ActionKey: "importSettings", ActionTextKey: "operations.portable.import.button"),
        new ElevationSettingDefinition("automaticAppUpdateChecks", "generalUpdater", "preference", "normal",
            JsonSerializer.SerializeToElement(true, ElevationSettingsCatalogJsonContext.Default.Boolean), null, null,
            true, false, "general.update.automatic.title", "general.update.automatic.description", null,
            "toggle-card", StorageKey: "AutomaticAppUpdateChecksEnabled",
            EnabledTextKey: "general.update.enabled", DisabledTextKey: "general.update.disabled",
            ActionKey: "checkAppUpdates", ActionTextKey: "general.update.check.button",
            RequiresUpdateCapability: true,
            CapabilityDescriptionTextKey: "general.update.capability.description"),
        new ElevationSettingDefinition("includePrereleaseAppUpdates", "generalUpdater", "preference", "normal",
            JsonSerializer.SerializeToElement(false, ElevationSettingsCatalogJsonContext.Default.Boolean), null, null,
            true, false, "general.update.prerelease.title", "general.update.prerelease.description", null,
            "toggle", StorageKey: "IncludePrereleaseAppUpdates"),
        new("reset-preferences", "generalManage", "info", "normal", default, null, null, true, false,
            "search.reset.title", "general.manage.reset.description", null,
            "action", ActionKey: "resetPreferences", ActionTextKey: "general.manage.reset.button"),
        new ElevationSettingDefinition("relatedMaintenance", "generalRelated", "navigation", "normal", default, null, null,
            true, false, "general.related.maintenance.title", "general.related.maintenance.description", null,
            "navigation", TargetPage: "Maintenance"),
        new ElevationSettingDefinition("relatedExecution", "generalRelated", "navigation", "normal", default, null, null,
            true, false, "general.related.execution.title", "general.related.execution.description", null,
            "navigation", TargetPage: "Execution"),
        Combo("image-cache-limit", "imageCache", "512", "MaxDiskMegabytes", "cache.images.limit.title", "cache.images.limit.description",
            ("64", "cache.images.limit.64"), ("128", "cache.images.limit.128"), ("256", "cache.images.limit.256"),
            ("512", "cache.images.limit.512"), ("1024", "cache.images.limit.1024"), ("2048", "cache.images.limit.2048"))
            with { PreferenceProviderId = "image-cache", SearchTerms = "limite espaço imagens baixadas disco megabytes gigabytes" },
        Combo("image-cache-revalidate", "imageCache", "14", "RevalidateAfterDays", "cache.images.revalidate.title", "cache.images.revalidate.description",
            ("1", "cache.images.revalidate.1"), ("3", "cache.images.revalidate.3"), ("7", "cache.images.revalidate.7"),
            ("14", "cache.images.revalidate.14"), ("30", "cache.images.revalidate.30"))
            with { PreferenceProviderId = "image-cache", SearchTerms = "revalidar verificar validar dias rede imagem" },
        new ElevationSettingDefinition("image-cache-clear", "imageCache", "info", "normal", default, null, null,
            true, false, "cache.images.clear.title", "cache.images.clear.description", null,
            "action", ActionKey: "clearImagesCache", ActionTextKey: "cache.images.clear.button",
            DescriptionProviderId: "cache-images-usage", SearchTerms: "limpar cache espaço imagens ícones arquivos"),
        new ElevationSettingDefinition("image-cache-memory", "imageCache", "info", "normal", default, null, null,
            true, false, "cache.images.memory.title", "cache.images.memory.description", null,
            "action", ActionKey: "releaseImageMemory", ActionTextKey: "cache.images.memory.button",
            DescriptionProviderId: "cache-images-memory", SearchTerms: "liberar memória imagens RAM"),
        new ElevationSettingDefinition("installer-cache-clear", "installerCache", "info", "normal", default, null, null,
            true, false, "cache.installers.clear.title", "cache.installers.clear.description", null,
            "action", ActionKey: "clearInstallerCache", ActionTextKey: "cache.installers.clear.button",
            DescriptionProviderId: "cache-installers-usage"),
        SearchInfo("notification-categories", "searchNotifications", "search.notifications.title", "search.notifications.description",
            "notificações avisos erros sucesso resumo lotes toast windows"),
        SearchInfo("theme", "searchInterface", "search.theme.title", "search.theme.description",
            "tema aparência claro escuro sistema cor"),
        SearchInfo("backdrop", "searchInterface", "search.backdrop.title", "search.backdrop.description",
            "fundo mica acrylic translúcido material aparência"),
        SearchInfo("package-icons", "searchInterface", "search.icons.title", "search.icons.description",
            "ícone ícones pacote pacotes aplicativo aplicativos imagens"),
        SearchInfo("package-screenshots", "searchInterface", "search.screenshots.title", "search.screenshots.description",
            "captura capturas screenshot screenshots imagens galeria detalhes"),
        SearchInfo("search-on-demand", "searchInterface", "search.onDemand.title", "search.onDemand.description",
            "pesquisa sob demanda carregar aplicativos catálogo memória cards banners"),
        SearchInfo("startup", "searchInterface", "search.startup.title", "search.startup.description",
            "inicialização iniciar login windows automático"),
        SearchInfo("startup-page", "searchInterface", "search.startupPage.title", "search.startupPage.description",
            "página inicial abrir início home descobrir atualizações instalados startup"),
        SearchInfo("package-managers", "searchPackageManagers", "search.packageManagers.title", "search.packageManagers.description",
            "fonte fontes repositório winget origem gerenciadores pacotes")
    ];

    private static ElevationSettingDefinition Combo(
        string key, string group, string defaultValue, string storageKey, string titleTextKey,
        string descriptionTextKey, params (string Value, string LabelTextKey)[] options) =>
        new(key, group, "preference", "normal",
            JsonSerializer.SerializeToElement(defaultValue, ElevationSettingsCatalogJsonContext.Default.String), null, null,
            true, false, titleTextKey, descriptionTextKey, null, "combo",
            options.Select(option => new ElevationSettingChoice(option.Value, option.LabelTextKey)).ToArray(),
            StorageKey: storageKey);

    private static ElevationSettingDefinition SearchInfo(
        string key, string group, string titleTextKey, string descriptionTextKey, string searchTerms) =>
        new(key, group, "info", "normal", default, null, null, true, false,
            titleTextKey, descriptionTextKey, null, "info", SearchTerms: searchTerms);

    private ElevationSettingsCatalog(
        IReadOnlyList<ElevationSettingsGroup> groups,
        IReadOnlyList<ElevationSettingDefinition> settings)
    {
        Groups = groups;
        Settings = settings;
    }

    public IReadOnlyList<ElevationSettingsGroup> Groups { get; }
    public IReadOnlyList<ElevationSettingDefinition> Settings { get; }

    public static ElevationSettingsCatalog Default { get; } = new(FallbackGroups, FallbackSettings);

    public static ElevationSettingsCatalog Load(string? json, Action<string>? log = null)
    {
        try
        {
            ElevationSettingsCatalogDocument? document = JsonSerializer.Deserialize(
                json ?? string.Empty, ElevationSettingsCatalogJsonContext.Default.ElevationSettingsCatalogDocument);
            if (document is null || document.SchemaVersion != 1 || document.Groups is null || document.Settings is null)
                throw new JsonException("Catálogo de configurações do Elevator ausente ou com schemaVersion incompatível.");

            var groups = document.Groups
                .Where(group => IsValidKey(group.Key) && !string.IsNullOrWhiteSpace(group.TitleTextKey))
                .GroupBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(group => group.Order)
                .ToArray();
            if (groups.Length != document.Groups.Count)
                log?.Invoke("Grupos inválidos ou duplicados do catálogo do Elevator foram ignorados.");

            var knownGroups = groups.Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var settings = document.Settings
                .Where(setting => IsValidKey(setting.Key)
                    && knownGroups.Contains(setting.Group)
                    && IsValidSetting(setting))
                .GroupBy(setting => setting.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            if (settings.Count != document.Settings.Count)
                log?.Invoke("Configurações inválidas, desconhecidas ou duplicadas do catálogo do Elevator foram ignoradas.");

            settings = RemoveCyclicDependencies(settings, log);
            settings = settings.Select(setting => setting.StorageKey == "ParallelOperations"
                ? setting with { Options = OperationConcurrencyLimits.ParallelOperationChoices }
                : setting).ToList();
            return new ElevationSettingsCatalog(groups, settings);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or ArgumentException)
        {
            log?.Invoke($"Catálogo de configurações do Elevator inválido; usando o padrão embutido. {exception.Message}");
            return Default;
        }
    }

    public IReadOnlyList<ElevationSettingDefinition> GetSettings(string groupKey) =>
        Settings.Where(setting => setting.Group.Equals(groupKey, StringComparison.OrdinalIgnoreCase)).ToArray();

    public IReadOnlyList<ElevationSettingDefinition> GetSettingsForPage(string page) =>
        Settings.Where(setting => Groups.Any(group => group.Key.Equals(setting.Group, StringComparison.OrdinalIgnoreCase)
            && group.Page.Equals(page, StringComparison.OrdinalIgnoreCase))).ToArray();

    public IReadOnlyList<ElevationSettingsGroup> GetGroups(string page) =>
        Groups.Where(group => group.Page.Equals(page, StringComparison.OrdinalIgnoreCase)).ToArray();

    public static ElevationSettingPresentation ResolvePresentation(
        ElevationSettingDefinition setting,
        IReadOnlyDictionary<string, bool> values,
        IReadOnlySet<string>? lockedKeys = null,
        IReadOnlyList<ElevationSettingDefinition>? settings = null) =>
        ResolvePresentation(setting, values.ToDictionary(
            pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            lockedKeys, settings);

    public static ElevationSettingPresentation ResolvePresentation(
        ElevationSettingDefinition setting,
        IReadOnlyDictionary<string, string> values,
        IReadOnlySet<string>? lockedKeys = null,
        IReadOnlyList<ElevationSettingDefinition>? settings = null)
    {
        ArgumentNullException.ThrowIfNull(setting);
        ArgumentNullException.ThrowIfNull(values);
        bool showToggle = setting.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase)
            && setting.EffectiveControl is "toggle" or "toggle-card";
        bool locked = lockedKeys?.Contains(setting.Key) == true
            || !string.IsNullOrWhiteSpace(setting.LockedReasonTextKey);
        bool dependencySatisfied = setting.DependsOn is null
            || ElevationSettingConditionEvaluator.Evaluate(
                setting.DependsOn, settings ?? Array.Empty<ElevationSettingDefinition>(), values).IsSatisfied;
        bool isVisible = setting.VisibleWhen is null
            || ElevationSettingConditionEvaluator.Evaluate(
                setting.VisibleWhen, settings ?? Array.Empty<ElevationSettingDefinition>(), values).IsSatisfied;
        return new ElevationSettingPresentation(
            ShowToggle: showToggle,
            IsEnabled: (setting.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase)
                || setting.EffectiveControl.Equals("action", StringComparison.OrdinalIgnoreCase)
                || setting.Kind.Equals("navigation", StringComparison.OrdinalIgnoreCase))
                && !locked && dependencySatisfied,
            IsDimmed: !dependencySatisfied,
            ShowRiskWarning: setting.Risk is "dangerous" or "elevated",
            RequiresConfirmation: showToggle && setting.RequiresConfirmation,
            ShowCombo: setting.EffectiveControl == "combo",
            ShowText: setting.EffectiveControl == "text",
            ShowAction: setting.EffectiveControl == "action",
            ShowNavigation: setting.Kind.Equals("navigation", StringComparison.OrdinalIgnoreCase)
                && setting.EffectiveControl == "navigation",
            IsVisible: isVisible);
    }

    private static bool IsValidKey(string? key) =>
        !string.IsNullOrWhiteSpace(key) && key.Length <= 128
        && key.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsValidSetting(ElevationSettingDefinition setting)
    {
        bool knownKind = setting.Kind is "preference" or "enforced" or "info" or "navigation" or "status";
        bool knownRisk = setting.Risk is "normal" or "elevated" or "dangerous";
        bool knownControl = setting.EffectiveControl is "toggle" or "toggle-card" or "combo" or "text" or "folder" or "action" or "navigation" or "info";
        if (!knownKind || !knownRisk || string.IsNullOrWhiteSpace(setting.TitleTextKey)
            || string.IsNullOrWhiteSpace(setting.DescriptionTextKey) || !knownControl)
            return false;
        if (setting.DescriptionProviderId is { } descriptionProviderId
            && (!IsValidKey(descriptionProviderId) || setting.EffectiveControl != "action"))
            return false;
        if (setting.PreferenceProviderId is { } preferenceProviderId
            && (!IsValidKey(preferenceProviderId) || setting.Kind != "preference"))
            return false;
        if (setting.DependsOn is not null && !ElevationSettingConditionEvaluator.IsValid(setting.DependsOn))
            return false;
        if (setting.VisibleWhen is not null && !ElevationSettingConditionEvaluator.IsValid(setting.VisibleWhen))
            return false;
        bool isNavigation = setting.Kind.Equals("navigation", StringComparison.OrdinalIgnoreCase);
        if (setting.TargetPage is not null
            && (!isNavigation || string.IsNullOrWhiteSpace(setting.TargetPage)))
            return false;
        if (isNavigation)
            return setting.EffectiveControl == "navigation"
                && !string.IsNullOrWhiteSpace(setting.TargetPage)
                && setting.ActionKey is null && setting.ActionTextKey is null
                && setting.Options is null or { Count: 0 };
        if (setting.EffectiveControl == "navigation" || setting.ActionKey == "navigateToPage")
            return false;
        if (setting.Kind == "preference")
        {
            bool validDefault = setting.EffectiveControl switch
            {
                "toggle" or "toggle-card" => setting.DefaultBooleanValue.HasValue,
                "combo" or "text" or "folder" => setting.DefaultValue.ValueKind == JsonValueKind.String,
                _ => false
            };
            bool validOptions = setting.EffectiveControl == "combo"
                ? setting.Options is { Count: > 0 }
                    && setting.Options.All(option => !string.IsNullOrWhiteSpace(option.Value)
                        && !string.IsNullOrWhiteSpace(option.LabelTextKey))
                    && setting.Options.Select(option => option.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == setting.Options.Count
                    && setting.Options.Any(option => option.Value.Equals(setting.DefaultTextValue, StringComparison.Ordinal))
                : setting.Options is null or { Count: 0 };
            return validDefault && validOptions
                && ((setting.EffectiveControl is "toggle" or "toggle-card") && setting.StorageKey is null
                    || !string.IsNullOrWhiteSpace(setting.StorageKey));
        }
        if (setting.Kind == "enforced")
            return !string.IsNullOrWhiteSpace(setting.LockedReasonTextKey);
        if (setting.Kind == "status")
            return setting.EffectiveControl == "info";
        return setting.EffectiveControl == "action"
            ? !string.IsNullOrWhiteSpace(setting.ActionKey) && !string.IsNullOrWhiteSpace(setting.ActionTextKey)
            : setting.EffectiveControl == "info";
    }

    private static List<ElevationSettingDefinition> RemoveCyclicDependencies(
        List<ElevationSettingDefinition> settings,
        Action<string>? log)
    {
        var byKey = settings.ToDictionary(setting => setting.Key, StringComparer.OrdinalIgnoreCase);
        var cyclicKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string key)
        {
            if (active.Contains(key))
            {
                cyclicKeys.Add(key);
                return;
            }
            if (!visited.Add(key) || !byKey.TryGetValue(key, out ElevationSettingDefinition? setting)
                || setting.DependsOn is not { Key: { Length: > 0 } dependencyKey }
                || !byKey.ContainsKey(dependencyKey))
                return;

            active.Add(key);
            Visit(dependencyKey);
            if (cyclicKeys.Contains(dependencyKey))
                cyclicKeys.Add(key);
            active.Remove(key);
        }

        foreach (string key in byKey.Keys)
            Visit(key);

        List<ElevationSettingDefinition> result;
        if (cyclicKeys.Count == 0)
        {
            result = settings.Select(setting => setting.DependsOn is { Key: { Length: > 0 } dependencyKey }
                && !byKey.ContainsKey(dependencyKey)
                ? setting with { DependsOn = null }
                : setting).ToList();
        }
        else
        {
            log?.Invoke($"Dependências circulares ignoradas no catálogo do Elevator: {string.Join(", ", cyclicKeys)}.");
            result = settings.Select(setting => setting.DependsOn is { Key: { Length: > 0 } dependencyKey }
                && (cyclicKeys.Contains(setting.Key) || cyclicKeys.Contains(dependencyKey) || !byKey.ContainsKey(dependencyKey))
            ? setting with { DependsOn = null }
            : setting).ToList();
        }

        return result.Select(setting => setting.VisibleWhen is { Key: { Length: > 0 } visibleKey }
            && !byKey.ContainsKey(visibleKey)
            ? setting with { VisibleWhen = null }
            : setting).ToList();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ElevationSettingsCatalogDocument))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(string))]
public partial class ElevationSettingsCatalogJsonContext : JsonSerializerContext;
