using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Store.ViewModels;

namespace WinProvision.Store.Services;

/// <summary>Presentation contract consumed by the shared package screen.</summary>
public interface IPackageScreenProvider : INotifyPropertyChanged
{
    PackageScreenDefinition Definition { get; }
    IPresentationService PresentationService => throw new NotSupportedException("Este provider não oferece confirmações de ações.");
    ObservableCollection<PackageScreenRow> Rows { get; }
    ObservableCollection<PackageScreenFilterOption> Filters { get; }
    ObservableCollection<PackageScreenAction> Actions { get; }
    ObservableCollection<PackageScreenAction> ContextActions { get; }

    string Subtitle { get; }
    string StatusText { get; }
    string? StatusCardText => null;
    string? GetStatusValue(string fieldId) => null;
    string SearchText { get; set; }
    string SearchPlaceholder { get; }
    string ViewMode { get; set; }
    string SortBy { get; set; }
    bool SortAscending { get; set; }
    bool IsLoading { get; }
    double? Progress => null;
    string? ErrorText { get; }
    string? ErrorTitle => null;
    bool IsFilterOpen { get; set; }
    bool HasActiveFilters { get; }
    int SelectedCount { get; }
    bool? MasterSelection { get; }
    string EmptyTitle { get; }
    string EmptyMessage { get; }
    void CancelPendingSearch() { }

    IAsyncRelayCommand RefreshCommand { get; }
    ICommand ToggleSelectAllCommand { get; }
    ICommand SetSortCommand { get; }
    ICommand SetViewModeCommand { get; }
    ICommand SetFilterCommand { get; }
    ICommand ToggleRowSelectionCommand { get; }
    ICommand? GetActionCommand(string actionId, PackageScreenRow? row = null);
    string? GetActionTitle(string actionId) => null;
    object? GetActionParameter(string actionId, PackageScreenRow? row = null);
    async Task SubmitSearchAsync(string query)
    {
        SearchText = query;
        ICommand? command = GetActionCommand("search");
        object? parameter = GetActionParameter("search") ?? query;
        if (command is IAsyncRelayCommand asyncCommand)
            await asyncCommand.ExecuteAsync(parameter);
        else if (command?.CanExecute(parameter) == true)
            command.Execute(parameter);
    }
    int? GetConfirmationItemCount(string actionId, PackageScreenRow? row = null) => null;
    Task ExecuteConfirmedActionAsync(string actionId, PackageScreenRow? row = null) =>
        PackageScreenActionExecutor.ExecuteCommandAsync(this, actionId, row);
}

public sealed record PackageScreenFilterValue(string Id, string Title);

public sealed record PackageScreenAction(
    string Id,
    string Title,
    string? Icon,
    ICommand Command,
    object? Parameter = null,
    string Kind = "action");
