using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using WinProvision.Core.Models;
using WinProvision.Store.Services;
using WinProvision.Store.Controls;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Views;

public sealed partial class OperationHistoryPage : Page, ITitleBarSearchTarget
{
    private bool _suppressLogSelectionReload;
    public OperationHistoryViewModel ViewModel { get; }
    internal ListView HistoryListForSmoke => HistoryList;
    internal TabView HistoryTabsForSmoke => HistoryTabs;
    bool ITitleBarSearchTarget.SearchContextAvailable => true;
    string ITitleBarSearchTarget.SearchText
    {
        get => ViewModel.SearchText;
        set => ViewModel.SearchText = value;
    }
    event EventHandler? ITitleBarSearchTarget.SearchContextChanged { add { } remove { } }
    Task ITitleBarSearchTarget.SubmitSearchAsync(string query)
    {
        ViewModel.SearchText = query;
        ViewModel.Refresh();
        return Task.CompletedTask;
    }

    public OperationHistoryPage(OperationHistoryViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        HistoryLogViewer.Configure(viewModel.ManagerTaskLogs, viewModel.AppLog, viewModel.Clipboard,
            viewModel.Presentation, viewModel.EnabledManagers, "history", operationHistoryLoader: viewModel.BuildOperationLogForViewerAsync,
            operationHistoryExportLoader: viewModel.BuildOperationLogForExportAsync);
        ActualThemeChanged += (_, _) => ViewModel.SetDarkTheme(ActualTheme == ElementTheme.Dark);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.SetDarkTheme(ActualTheme == ElementTheme.Dark);
        ViewModel.Refresh();
        if (HistoryTabs.SelectedIndex == 1) await LoadLogAndScrollAsync();
    }

    private async void HistoryTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressLogSelectionReload && HistoryTabs.SelectedIndex == 1) await LoadLogAndScrollAsync();
    }

    private async Task LoadLogAndScrollAsync() => await HistoryLogViewer.LoadAsync();


    public async Task ShowManagerLogsAsync(string managerId)
    {
        _suppressLogSelectionReload = true;
        try
        {
            HistoryTabs.SelectedIndex = 1;
            await HistoryLogViewer.LoadAsync(managerId);
        }
        finally { _suppressLogSelectionReload = false; }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e) => await ViewModel.ClearHistoryAsync();
    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: OperationHistoryRecord record }) await ViewModel.UndoAsync(record);
    }
    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: OperationHistoryRecord record }) await ViewModel.RepeatAsync(record);
    }
    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: OperationHistoryRecord record }) ViewModel.Remove(record);
    }
    private async void ViewOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OperationHistoryRecord record }) return;
        var texts = App.CurrentApp.Services.GetRequiredService<IOperationTextProvider>();
        await OperationOutputDialog.ShowAsync(XamlRoot,
            texts.Format("operations.output.historyTitle", new Dictionary<string, string?> { ["packageName"] = record.PackageName }),
            ViewModel.ReadOutput(record), texts.GetText);
    }
}
