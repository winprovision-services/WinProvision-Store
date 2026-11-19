using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using WinProvision.Core.Services;

namespace WinProvision.Store.ViewModels;

public interface ISettingsCatalogPreferenceProvider
{
    string Id { get; }
    string GetValue(ElevationSettingDefinition definition);
    Task SetValueAsync(ElevationSettingDefinition definition, string value, CancellationToken cancellationToken);
}

public sealed class SettingsCatalogDescriptionProviderRegistry
{
    private readonly Dictionary<string, Func<string>> _providers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string id, Func<string> provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(provider);
        _providers[id] = provider;
    }

    public bool TryGet(string? id, out string description)
    {
        if (id is not null && _providers.TryGetValue(id, out Func<string>? provider))
        {
            description = provider();
            return true;
        }

        description = string.Empty;
        return false;
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record SettingsCatalogChoiceOption(string Value, string Label);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class SettingsCatalogSettingOption : ObservableObject
{
    private readonly string _emptyFolderDescription;
    private readonly string _enabledText;
    private readonly string _disabledText;
    public ElevationSettingDefinition Definition { get; }
    public string Key => Definition.Key;
    public string Title { get; }
    public string Description { get; private set; } = string.Empty;
    public string? DisplayDescription => NormalizeDescription(Description);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string SecondaryDescription { get; private set; }
    public bool HasSecondaryDescription => !string.IsNullOrWhiteSpace(SecondaryDescription);
    public string? RiskWarning { get; }
    public NoticeOption? RiskNotice { get; }
    public string Control => Definition.EffectiveControl;
    public bool IsToggle => Control == "toggle";
    public bool IsToggleCard => Control == "toggle-card";
    public bool IsToggleSetting => IsToggle || IsToggleCard;
    public bool IsCombo => Control == "combo";
    public bool IsText => Control == "text";
    public bool IsFolder => Control == "folder";
    public bool IsAction => Control == "action";
    public bool IsNavigation => Definition.Kind.Equals("navigation", StringComparison.OrdinalIgnoreCase)
        && Control == "navigation" && !string.IsNullOrWhiteSpace(Definition.TargetPage);
    public bool IsRendered => IsVisible && !IsNavigation;
    public bool HasUpdateCapability => Definition.RequiresUpdateCapability;
    public string TargetPage => Definition.TargetPage ?? string.Empty;
    public bool HasRiskWarning => !string.IsNullOrWhiteSpace(RiskWarning);
    public bool ToggleValue => bool.TryParse(Value, out bool enabled) && enabled;
    public string StateText => ToggleValue
        ? string.IsNullOrWhiteSpace(_stateValue) ? _enabledText : $"{_enabledText} · {_stateValue}"
        : _disabledText;
    public string? Icon => Definition.Icon;
    public bool HasIcon => !string.IsNullOrWhiteSpace(Icon);
    public bool IsDimmed { get; private set; }
    public double CardOpacity => IsDimmed ? 0.55 : 1.0;
    public bool IsEnabled { get; private set; } = true;
    public bool IsVisible { get; private set; } = true;
    public string DependencyMessage { get; private set; } = string.Empty;
    public string SelectFolderText { get; }
    public string ResetFolderText { get; }
    public string ActionButtonText { get; private set; }
    public bool HasAuxiliaryAction => !IsAction && !string.IsNullOrWhiteSpace(ActionButtonText) && ActionCommand is not null;
    public bool IsAuxiliaryActionEnabled { get; private set; } = true;
    public bool IsAuxiliaryActionChecking { get; private set; }
    public bool IsAuxiliaryActionDownloading { get; private set; }
    public double AuxiliaryActionProgress { get; private set; }
    public bool HasCancelableOperation { get; private set; }
    public string CancelOperationText { get; private set; } = string.Empty;
    public ICommand? CancelOperationCommand { get; private set; }
    public IReadOnlyList<SettingsCatalogChoiceOption> Options { get; }
    public ICommand? ActionCommand { get; private set; }
    private string? _stateValue;

    [ObservableProperty]
    public partial string Value { get; set; }

    public SettingsCatalogSettingOption(
        ElevationSettingDefinition definition,
        string title,
        string description,
        string? riskWarning,
        string value,
        IReadOnlyDictionary<string, string> texts,
        ICommand? actionCommand = null,
        string? lockedReason = null,
        string? secondaryDescription = null)
    {
        Definition = definition;
        Title = title;
        Description = lockedReason ?? description;
        SecondaryDescription = secondaryDescription ?? string.Empty;
        RiskWarning = riskWarning;
        RiskNotice = string.IsNullOrWhiteSpace(riskWarning)
            ? null
            : new NoticeOption(
                new NoticeDefinition($"catalog-risk-{definition.Key}", "warning", "inline", definition.Key,
                    string.Empty, string.Empty, null, false, "none", null, null),
                title, riskWarning, null, _ => { });
        _emptyFolderDescription = texts.TryGetValue("operations.folder.noFolder", out string? folderDescription)
            ? folderDescription
            : "Downloads; perguntar a cada lote";
        string? enabledText = ResolveText(texts, definition.EnabledTextKey);
        string? disabledText = ResolveText(texts, definition.DisabledTextKey);
        _enabledText = string.IsNullOrWhiteSpace(enabledText)
            ? texts.TryGetValue("operations.state.enabled", out string? operationEnabled) ? operationEnabled : "Ativado"
            : enabledText;
        _disabledText = string.IsNullOrWhiteSpace(disabledText)
            ? texts.TryGetValue("operations.state.disabled", out string? operationDisabled) ? operationDisabled : "Desativado"
            : disabledText;
        SelectFolderText = ResolveText(texts, definition.SelectTextKey);
        ResetFolderText = ResolveText(texts, definition.ResetTextKey);
        ActionButtonText = ResolveText(texts, definition.ActionTextKey);
        ActionCommand = actionCommand;
        Options = (definition.Options ?? [])
            .Select(option =>
            {
                string label = texts.TryGetValue(option.LabelTextKey, out string? text) ? text : option.LabelTextKey;
                if (option.Value.Equals(definition.DefaultTextValue, StringComparison.Ordinal))
                    label += texts.TryGetValue("operations.defaultSuffix", out string? suffix) ? suffix : " (padrão)";
                return new SettingsCatalogChoiceOption(option.Value, label);
            })
            .ToArray();
        Value = value;
        UpdatePresentation();
    }

    partial void OnValueChanged(string value) => UpdatePresentation();

    public void RefreshBindings()
    {
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(DisplayDescription));
        OnPropertyChanged(nameof(HasDescription));
        OnPropertyChanged(nameof(ToggleValue));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(HasAuxiliaryAction));
        OnPropertyChanged(nameof(SecondaryDescription));
        OnPropertyChanged(nameof(HasSecondaryDescription));
        OnPropertyChanged(nameof(IsAuxiliaryActionEnabled));
        OnPropertyChanged(nameof(IsAuxiliaryActionChecking));
        OnPropertyChanged(nameof(IsAuxiliaryActionDownloading));
        OnPropertyChanged(nameof(AuxiliaryActionProgress));
        OnPropertyChanged(nameof(HasCancelableOperation));
        OnPropertyChanged(nameof(CancelOperationText));
        OnPropertyChanged(nameof(CancelOperationCommand));
    }

    public void SetDescription(string description)
    {
        Description = description;
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(DisplayDescription));
        OnPropertyChanged(nameof(HasDescription));
    }

    public void SetSecondaryDescription(string description)
    {
        SecondaryDescription = description;
        OnPropertyChanged(nameof(SecondaryDescription));
        OnPropertyChanged(nameof(HasSecondaryDescription));
    }

    public void SetAuxiliaryAction(
        string text,
        ICommand? command,
        bool isEnabled = true,
        bool isChecking = false,
        bool isDownloading = false,
        double progress = 0,
        string cancelText = "",
        ICommand? cancelCommand = null)
    {
        ActionButtonText = text;
        ActionCommand = command;
        IsAuxiliaryActionEnabled = isEnabled;
        IsAuxiliaryActionChecking = isChecking;
        IsAuxiliaryActionDownloading = isDownloading;
        AuxiliaryActionProgress = Math.Clamp(progress, 0, 1);
        HasCancelableOperation = isChecking || isDownloading;
        CancelOperationText = cancelText;
        CancelOperationCommand = cancelCommand;
        OnPropertyChanged(nameof(ActionButtonText));
        OnPropertyChanged(nameof(ActionCommand));
        OnPropertyChanged(nameof(HasAuxiliaryAction));
        OnPropertyChanged(nameof(IsAuxiliaryActionEnabled));
        OnPropertyChanged(nameof(IsAuxiliaryActionChecking));
        OnPropertyChanged(nameof(IsAuxiliaryActionDownloading));
        OnPropertyChanged(nameof(AuxiliaryActionProgress));
        OnPropertyChanged(nameof(HasCancelableOperation));
        OnPropertyChanged(nameof(CancelOperationText));
        OnPropertyChanged(nameof(CancelOperationCommand));
    }

    public void SetDependencyMessage(string message)
    {
        DependencyMessage = message;
        OnPropertyChanged(nameof(DependencyMessage));
    }

    public void RefreshPresentation(
        IReadOnlyDictionary<string, string> values,
        IReadOnlyList<ElevationSettingDefinition> settings,
        string? stateValue = null,
        string? dependencyMessage = null,
        bool isBusy = false,
        IReadOnlySet<string>? lockedKeys = null)
    {
        _stateValue = stateValue;
        ElevationSettingPresentation presentation = ElevationSettingsCatalog.ResolvePresentation(
            Definition, values, lockedKeys, settings);
        IsDimmed = presentation.IsDimmed;
        IsEnabled = presentation.IsEnabled && !isBusy;
        IsVisible = presentation.IsVisible;
        DependencyMessage = presentation.IsDimmed ? dependencyMessage ?? string.Empty : string.Empty;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(IsDimmed));
        OnPropertyChanged(nameof(CardOpacity));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsRendered));
        OnPropertyChanged(nameof(DependencyMessage));
    }

    private void UpdatePresentation()
    {
        if (IsFolder)
        {
            Description = string.IsNullOrWhiteSpace(Value)
                ? _emptyFolderDescription
                : Value;
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(DisplayDescription));
            OnPropertyChanged(nameof(HasDescription));
        }
        OnPropertyChanged(nameof(ToggleValue));
        OnPropertyChanged(nameof(StateText));
    }

    private static string ResolveText(IReadOnlyDictionary<string, string> texts, string? key) =>
        key is not null && texts.TryGetValue(key, out string? text) ? text : string.Empty;

    private static string? NormalizeDescription(string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : description;
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class SettingsCatalogGroupOption
{
    public string Title { get; }
    public string? Icon { get; }
    public bool HasIcon => !string.IsNullOrWhiteSpace(Icon);
    public string? HelpText { get; }
    public bool HasHelpText => !string.IsNullOrWhiteSpace(HelpText);
    public bool HasDetails => !string.IsNullOrWhiteSpace(DetailsText);
    public string? DetailsText { get; }
    public string? DetailsTitle { get; }
    public SettingsCatalogSettingOption? PrimarySetting { get; }
    public IReadOnlyList<SettingsCatalogSettingOption> Settings { get; }

    public SettingsCatalogGroupOption(
        string title,
        string? helpText,
        string? detailsTitle,
        string? detailsText,
        IReadOnlyList<SettingsCatalogSettingOption> settings,
        string? icon = null,
        SettingsCatalogSettingOption? primarySetting = null)
    {
        Title = title;
        Icon = icon;
        HelpText = helpText;
        DetailsTitle = detailsTitle;
        DetailsText = detailsText;
        Settings = settings;
        PrimarySetting = primarySetting;
    }
}
