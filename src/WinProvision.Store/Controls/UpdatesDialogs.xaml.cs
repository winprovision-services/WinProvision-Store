using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Controls;
public sealed partial class UpdatesDialogs : UserControl
{
    public UpdatesViewModel ViewModel { get; }
    private readonly IManualWinGetCommandService _manualWinGetCommand;
    private bool _loaded, _suppressToggle;
    private FrameworkElement? _previousFocus;
    public UpdatesDialogs(UpdatesViewModel model, IManualWinGetCommandService manual)
    {
        ViewModel = model; _manualWinGetCommand = manual;
        InitializeComponent(); DataContext = model;
        IgnoredUpdatesModalList.ItemsSource = model.IgnoredPackages;
        Loaded += (_, _) => _loaded = true;
        Unloaded += (_, _) => { _loaded = false; CloseModal(); };
    }
    internal void ShowModal(string modal)
    {
        CloseModal();
        _previousFocus = FocusManager.GetFocusedElement(XamlRoot) as FrameworkElement;
        if (modal == "showIgnored") modal = "ignored";
        if (modal == "manualUpdate") modal = "manual";
        if (modal == "updateOptions") modal = "options";
        if (modal == "ignored")
        {
            ViewModel.RefreshIgnoredUpdates();
            IgnoredUpdatesOverlay.Visibility = Visibility.Visible;
        }
        else if (modal == "manual")
        {
            ViewModel.SelectedPackage = ViewModel.Packages.FirstOrDefault(package => package.IsSelectedForUpdate)
                ?? ViewModel.Packages.FirstOrDefault();
            ManualPackageIdBox.Text = ViewModel.SelectedPackage?.Id ?? string.Empty;
            ManualUpdateOverlay.Visibility = Visibility.Visible;
            ManualPackageIdBox.Focus(FocusState.Programmatic);
        }
        else if (modal == "options")
        {
            LoadOptions();
            OptionsOverlay.Visibility = Visibility.Visible;
            UpdateScheduleComboBox.Focus(FocusState.Programmatic);
        }
    }

    internal bool CloseModal()
    {
        bool wasOpen = IgnoredUpdatesOverlay.Visibility == Visibility.Visible
            || ManualUpdateOverlay.Visibility == Visibility.Visible || OptionsOverlay.Visibility == Visibility.Visible;
        IgnoredUpdatesOverlay.Visibility = ManualUpdateOverlay.Visibility = OptionsOverlay.Visibility = Visibility.Collapsed;
        if (wasOpen) _previousFocus?.Focus(FocusState.Programmatic);
        return wasOpen;
    }

    private async void RestoreIgnoredUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: IgnoredUpdateEntry entry })
            await ViewModel.RestoreAsync(new[] { entry });
    }

    private async void RestoreAllIgnored_Click(object sender, RoutedEventArgs e)
    {
        CloseModal();
        await ViewModel.RestoreAsync(ViewModel.IgnoredPackages.ToArray());
    }

    private void ExecuteManualUpdate_Click(object sender, RoutedEventArgs e)
    {
        string command;
        try { command = ManualWinGetCommandService.BuildUpgradeCommand(ManualPackageIdBox.Text); }
        catch (ArgumentException exception)
        {
            ViewModel.StatusText = exception.Message;
            ManualPackageIdBox.Focus(FocusState.Programmatic);
            return;
        }
        CloseModal();
        _manualWinGetCommand.TryOpenInTerminal(command, out string status);
        ViewModel.StatusText = status;
    }

    private void LoadOptions()
    {
        _suppressToggle = true;
        AutoUpdateSwitch.IsOn = ViewModel.AutoUpdateEnabled;
        SelectAllByDefaultSwitch.IsOn = ViewModel.SelectAllByDefault;
        _suppressToggle = false;
        UpdateScheduleComboBox.SelectedValue = ViewModel.AutomationSchedule;
        UpdateAutomationModeComboBox.SelectedValue = ViewModel.AutomationMode;
        UpdateAutomationScopeComboBox.SelectedValue = ViewModel.AutomationScope;
        UpdateScheduleDayComboBox.SelectedValue = ViewModel.AutomationDay;
        UpdateScheduleTimeBox.Text = ViewModel.AutomationTime;
        AutoUpdatePackageSelectionText.Text = ViewModel.AutomationSelectionText;
    }

    private void UpdateScheduleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        string? schedule = UpdateScheduleComboBox.SelectedValue as string;
        UpdateScheduleDayComboBox.Visibility = schedule == "Weekly" ? Visibility.Visible : Visibility.Collapsed;
        UpdateScheduleDetailsPanel.Visibility = schedule is "Daily" or "Weekly" ? Visibility.Visible : Visibility.Collapsed;
        UpdateScheduleIntervalText.Visibility = schedule == "Interval" ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ApplyAutomationOptions_Click(object sender, RoutedEventArgs e)
    {
        if (await ViewModel.ApplyAutomationOptionsAsync(UpdateScheduleComboBox.SelectedValue as string ?? string.Empty,
            UpdateAutomationModeComboBox.SelectedValue as string ?? string.Empty,
            UpdateAutomationScopeComboBox.SelectedValue as string ?? string.Empty,
            UpdateScheduleDayComboBox.SelectedValue as string ?? string.Empty, UpdateScheduleTimeBox.Text)) CloseModal();
    }

    private async void AutoUpdateSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppressToggle) return;
        await ViewModel.SetAutoUpdateAsync(AutoUpdateSwitch.IsOn);
        _suppressToggle = true;
        AutoUpdateSwitch.IsOn = ViewModel.AutoUpdateEnabled;
        _suppressToggle = false;
    }

    private void SelectAllByDefaultSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loaded && !_suppressToggle) ViewModel.SelectAllByDefault = SelectAllByDefaultSwitch.IsOn;
    }

    private void CloseIgnoredOverlay_Click(object sender, RoutedEventArgs e) => CloseModal();
    private void CloseManualOverlay_Click(object sender, RoutedEventArgs e) => CloseModal();
    private void CloseOptionsOverlay_Click(object sender, RoutedEventArgs e) => CloseModal();
    private void DismissModalOverlay_Click(object sender, TappedRoutedEventArgs e) => CloseModal();
    private void ModalCard_Click(object sender, TappedRoutedEventArgs e) => e.Handled = true;
    private void Dialog_KeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Escape && CloseModal()) e.Handled = true; }
}
