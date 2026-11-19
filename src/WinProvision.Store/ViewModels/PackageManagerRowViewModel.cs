using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using WinProvision.Core.Models;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PackageManagerRowViewModel : ObservableObject
{
    private readonly PackageManagerCatalog _catalog;
    private readonly Func<PackageManagerRowViewModel, bool, Task> _setEnabled;
    private readonly Action<string> _openDetails;
    private bool _suppressEnabledChange;
    private PackageManagerProbeState? _probe;
    private PackageManagerProbeState? _parentProbe;
    private bool _parentEnabled = true;

    public PackageManagerDescriptor Definition { get; }
    public string Id => Definition.Id;
    public string DisplayName { get; }
    public string Description { get; }
    public string IconGlyph => Definition.IconGlyph ?? "\uE8F1";
    public string? IconUri => Definition.IconUri;
    public bool HasIconUri => !string.IsNullOrWhiteSpace(IconUri);
    public SvgImageSource? IconSource => Uri.TryCreate(IconUri, UriKind.Absolute, out Uri? iconUri)
        ? new SvgImageSource { UriSource = iconUri } : null;
    public bool HasDetailsPage => Definition.HasDetailsPage || Definition.ParentId is not null;
    public string ToggleAutomationName => _catalog.GetText("managerPreferences.toggle", "Ativar ou desativar {name}").Replace("{name}", DisplayName, StringComparison.Ordinal);
    public string ToggleHelpText => _catalog.GetText(Definition.DisabledReasonKey, string.Empty);
    public string DetailsAutomationName => _catalog.GetText("managerPreferences.openDetails", "Abrir detalhes de {name}").Replace("{name}", DisplayName, StringComparison.Ordinal);
    [ObservableProperty] public partial bool IsBusy { get; private set; }
    [ObservableProperty] public partial string ProgressText { get; private set; } = string.Empty;
    [ObservableProperty] public partial string FeedbackText { get; private set; } = string.Empty;
    public bool CanToggle => Definition.CanToggle && !IsBusy;
    public bool HasProgressText => !string.IsNullOrWhiteSpace(ProgressText);
    public bool HasFeedbackText => !string.IsNullOrWhiteSpace(FeedbackText);

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusStyle { get; private set; } = "info";

    [ObservableProperty]
    public partial string VersionText { get; private set; } = string.Empty;

    public IRelayCommand OpenDetailsCommand { get; }

    public PackageManagerRowViewModel(PackageManagerDescriptor definition, PackageManagerCatalog catalog,
        bool enabled, Func<PackageManagerRowViewModel, bool, Task> setEnabled, Action<string> openDetails)
    {
        Definition = definition;
        _catalog = catalog;
        _setEnabled = setEnabled;
        _openDetails = openDetails;
        DisplayName = catalog.GetText(definition.DisplayNameKey, definition.DisplayName);
        Description = catalog.GetText(definition.DescriptionKey, definition.Description);
        IsEnabled = enabled;
        OpenDetailsCommand = new RelayCommand(() => _openDetails(Id), () => HasDetailsPage);
        UpdateStatus();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (_suppressEnabledChange) return;
        UpdateStatus();
        _ = ApplyEnabledChangeAsync(value);
    }

    public void SetProbe(PackageManagerProbeState? probe) { _probe = probe; UpdateStatus(); }
    public void SetParentState(bool enabled, PackageManagerProbeState? probe) { _parentEnabled = enabled; _parentProbe = probe; UpdateStatus(); }

    public void RevertEnabled(bool value)
    {
        if (IsEnabled == value) return;
        _suppressEnabledChange = true;
        IsEnabled = value;
        _suppressEnabledChange = false;
    }

    public void SetProgress(string? message)
    {
        ProgressText = message ?? string.Empty;
        OnPropertyChanged(nameof(HasProgressText));
    }

    public void SetBusy(bool busy)
    {
        IsBusy = busy;
        if (!busy) SetProgress(string.Empty);
        OnPropertyChanged(nameof(CanToggle));
    }

    public void SetFeedback(string? message)
    {
        FeedbackText = message ?? string.Empty;
        OnPropertyChanged(nameof(HasFeedbackText));
    }

    private async Task ApplyEnabledChangeAsync(bool enabled)
    {
        bool previous = !enabled;
        IsBusy = true;
        OnPropertyChanged(nameof(CanToggle));
        SetFeedback(string.Empty);
        SetProgress(enabled ? "Ativando…" : "Desativando…");
        try
        {
            await _setEnabled(this, enabled);
            FeedbackText = string.Empty;
            OnPropertyChanged(nameof(HasFeedbackText));
        }
        catch (OperationCanceledException exception)
        {
            RevertEnabled(previous);
            SetFeedback(exception.Message);
        }
        catch (Exception exception)
        {
            RevertEnabled(previous);
            SetFeedback(exception.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            OnPropertyChanged(nameof(CanToggle));
            OnPropertyChanged(nameof(HasProgressText));
            UpdateStatus();
        }
    }

    private void UpdateStatus()
    {
        PackageManagerRowStatus status = PackageManagerStatusResolver.Resolve(Definition, IsEnabled, _probe, _parentEnabled, _parentProbe);
        StatusText = _catalog.GetText($"status.{status.Status}", status.Status);
        StatusStyle = _catalog.GetStatusStyle(status.Status);
        VersionText = string.IsNullOrWhiteSpace(status.Version) ? string.Empty : status.Version;
        OnPropertyChanged(nameof(CanToggle));
    }
}
