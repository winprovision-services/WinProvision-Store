using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PackageManagerSettingsFieldViewModel : ObservableObject
{
    private readonly PackageManagersViewModel _owner;
    private readonly string _managerId;
    private readonly string _initialValue;
    private bool _isInitializing = true;
    public string Id { get; }
    public string Type { get; }
    public string Section { get; }
    public string Label { get; }
    public string? Description { get; }
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public string? Placeholder { get; }
    public string PathKind { get; }
    public string DefaultValue { get; }
    public IReadOnlyList<PackageManagerSettingsChoiceViewModel> Choices { get; }
    public PackageManagerSettingsChoiceViewModel? SelectedChoice
    {
        get => Choices.FirstOrDefault(choice => choice.Id == Value);
        set { if (value is not null && Value != value.Id) Value = value.Id; }
    }
    public bool IsToggle => Type == "toggle";
    public bool IsNotToggle => !IsToggle;
    public bool IsChoice => Type == "choice";
    public bool IsText => Type == "text";
    public bool IsPath => Type == "path";
    public bool IsDefault => string.Equals(Value, DefaultValue, StringComparison.Ordinal);
    public bool IsChanged => !string.Equals(Value, _initialValue, StringComparison.Ordinal);
    [ObservableProperty] public partial string Value { get; set; }
    public bool ToggleValue { get => bool.TryParse(Value, out bool value) && value; set => Value = value.ToString(); }
    public string SelectPathText => _owner.SelectPathText;
    public string ResetText => _owner.ResetText;
    public string EnabledStateText => _owner.EnabledStateText;
    public string DisabledStateText => _owner.DisabledStateText;
    public string ToggleStateText => ToggleValue ? EnabledStateText : DisabledStateText;
    public IAsyncRelayCommand SelectPathCommand { get; }
    public IRelayCommand ResetPathCommand { get; }

    public PackageManagerSettingsFieldViewModel(PackageManagersViewModel owner, string managerId, PackageManagerSettingsField field)
    {
        _owner = owner; _managerId = managerId; Id = field.Id; Type = field.Type; Section = field.Section;
        Label = field.Label; Description = string.IsNullOrWhiteSpace(field.Description) ? null : field.Description;
        Placeholder = field.Placeholder; PathKind = field.PathKind ?? string.Empty;
        DefaultValue = field.DefaultValue ?? string.Empty; _initialValue = field.Value ?? DefaultValue; Value = _initialValue;
        Choices = field.Choices.Select(choice => new PackageManagerSettingsChoiceViewModel(choice)).ToArray();
        SelectPathCommand = new AsyncRelayCommand(SelectPathAsync, () => IsPath);
        ResetPathCommand = new RelayCommand(() => Value = DefaultValue, () => IsPath && !IsDefault);
        _isInitializing = false;
    }
    partial void OnValueChanged(string value)
    {
        OnPropertyChanged(nameof(ToggleValue));
        OnPropertyChanged(nameof(ToggleStateText));
        OnPropertyChanged(nameof(SelectedChoice));
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(IsChanged));
        if (_isInitializing) return;
        ResetPathCommand.NotifyCanExecuteChanged();
        _owner.UpdateField(_managerId, Id, value);
        _owner.NotifySettingsChanged();
    }
    private async Task SelectPathAsync(CancellationToken cancellationToken)
    {
        string? selected = await _owner.SelectPathAsync(_managerId, Id, PathKind, cancellationToken);
        if (selected is not null) Value = selected;
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PackageManagerSettingsChoiceViewModel
{
    public string Id { get; }
    public string Label { get; }
    public PackageManagerSettingsChoiceViewModel(PackageManagerSettingsChoice choice)
    { Id = choice.Id; Label = choice.Label; }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PackageManagerSettingsActionViewModel
{
    public string Id { get; }
    public string Label { get; }
    public string Kind { get; }
    public bool RequiresConfirmation { get; }
    public string Section { get; }
    public bool IsLink => Kind.Equals("link", StringComparison.OrdinalIgnoreCase);
    public PackageManagerSettingsActionViewModel(PackageManagerSettingsAction action)
    { Id = action.Id; Label = action.Label; Kind = action.Kind; RequiresConfirmation = action.Confirm; Section = action.Section; }
}
