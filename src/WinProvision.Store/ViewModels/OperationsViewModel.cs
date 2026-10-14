using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OperationViewModel : ObservableObject, IDisposable
{
    private readonly IUiDispatcher _dispatcher;
    private bool _disposed;
    public OperationItem Operation { get; }
    public IRelayCommand ActionCommand { get; }
    public IRelayCommand CancelCommand { get; }

    public OperationViewModel(OperationItem operation, IUiDispatcher dispatcher, Action dismiss)
    {
        Operation = operation;
        _dispatcher = dispatcher;
        CancelCommand = new RelayCommand(Operation.Cancel, () => Operation.CanCancel);
        ActionCommand = new RelayCommand(() =>
        {
            if (Operation.IsFinished) dismiss();
            else Operation.Cancel();
        });
        Operation.PropertyChanged += OperationChanged;
    }

    public Guid Id => Operation.Id;
    public string? IconUrl => Operation.IconUrl;
    private string AppName => Operation.AppName;
    private OperationKind Kind => Operation.Kind;
    private string StatusText => Operation.StatusText;
    public string KindLabel => Kind switch
    {
        OperationKind.Install => "Instalando",
        OperationKind.Update => "Atualizando",
        OperationKind.Uninstall => "Removendo",
        _ => "Processando"
    };

    public string OperationTitle => $"{AppName} — {Kind switch
    {
        OperationKind.Install => "Instalação",
        OperationKind.Update => "Atualização",
        OperationKind.Uninstall => "Desinstalação",
        _ => "Operação"
    }}";

    /// <summary>Título da operação (ex.: 'Atualizando Boto3...').</summary>
    public string DisplayTitle => State switch
    {
        OperationState.Running => $"{KindLabel} {AppName}...",
        OperationState.Queued => $"{KindLabel} {AppName}...",
        OperationState.Completed when Operation.RebootRequired => $"{AppName} concluído; reinicie o Windows",
        OperationState.Completed => Kind switch
        {
            OperationKind.Install => $"{AppName} instalado com sucesso",
            OperationKind.Update => $"{AppName} atualizado com sucesso",
            OperationKind.Uninstall => $"{AppName} removido com sucesso",
            _ => $"{AppName} concluído"
        },
        OperationState.Failed => $"Falha ao {KindVerb} {AppName}",
        OperationState.Canceled => $"{KindLabel} {AppName} cancelado",
        _ => $"{AppName}"
    };

    public string KindVerb => Kind switch
    {
        OperationKind.Install => "instalar",
        OperationKind.Update => "atualizar",
        OperationKind.Uninstall => "remover",
        _ => "processar"
    };

    public string ButtonText => IsFinished ? "Limpar concluídas" : "Cancelar";

    /// <summary>Resumo curto para a interface; logs detalhados permanecem em DetailText.</summary>
    public string UserFacingStatusText => State switch
    {
        OperationState.Queued => "Na fila",
        OperationState.Running => string.IsNullOrWhiteSpace(StatusText) ? $"{KindLabel}…" : StatusText,
        OperationState.Completed when Operation.RebootRequired => "Concluído; reinicie o Windows",
        OperationState.Completed => Kind switch
        {
            OperationKind.Install => "Instalado",
            OperationKind.Update => "Atualizado",
            OperationKind.Uninstall => "Removido",
            _ => "Concluído"
        },
        OperationState.Failed => Kind switch
        {
            OperationKind.Install => "Falha na instalação",
            OperationKind.Update => "Falha na atualização",
            OperationKind.Uninstall => "Falha na remoção",
            _ => "Falha na operação"
        },
        OperationState.Canceled => "Cancelado",
        _ => string.Empty
    };


    public string LiveLine => Operation.LiveLine;
    public double Progress => Operation.Progress;
    public bool IsIndeterminate => Operation.IsIndeterminate;
    public bool IsFinished => Operation.IsFinished;
    public OperationState State => Operation.State;
    public WingetMethod Method => Operation.Method;

    private void OperationChanged(object? sender, PropertyChangedEventArgs args) =>
        _dispatcher.Post(() =>
        {
            if (_disposed) return;
            OnPropertyChanged(args.PropertyName);
            if (args.PropertyName is nameof(OperationItem.State) or nameof(OperationItem.StatusText) or nameof(OperationItem.RebootRequired))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(UserFacingStatusText));
                OnPropertyChanged(nameof(ButtonText));
            }
            CancelCommand.NotifyCanExecuteChanged();
        });

    public void Dispose()
    {
        _disposed = true;
        Operation.PropertyChanged -= OperationChanged;
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class OperationsViewModel : ObservableObject, IDisposable
{
    private readonly OperationsQueueService _queue;
    private readonly IUiDispatcher _dispatcher;
    private readonly IClipboardService _clipboard;
    private bool _disposed;
    public System.Collections.ObjectModel.ObservableCollection<OperationViewModel> Items { get; } = new();
    [ObservableProperty] public partial string Summary { get; set; } = "Nenhuma operação em andamento";
    [ObservableProperty] public partial int PendingCount { get; set; }
    [ObservableProperty] public partial bool HasPending { get; set; }
    [ObservableProperty] public partial bool HasOperations { get; set; }
    [ObservableProperty] public partial bool IsQueueOpen { get; set; }
    [ObservableProperty] public partial bool HasFailedOperations { get; set; }
    [ObservableProperty] public partial bool IsQueueExpanded { get; set; } = true;
    [ObservableProperty] public partial string OutputTitle { get; set; } = "";
    [ObservableProperty] public partial string OutputText { get; set; } = "";
    public bool ShowFailureBadge => HasFailedOperations && !IsQueueExpanded;
    public string ExpanderGlyph => IsQueueExpanded ? "\uE70E" : "\uE70D";
    public string QueueDescription => HasPending ? $"Fila de operações, {PendingCount} pendente(s)" : "Fila de operações";
    public string HeaderText => _queue.TotalCount == 0 ? "Operações" : $"{_queue.CompletedCount} de {_queue.TotalCount} operações concluídas";
    public IRelayCommand CancelAllCommand { get; }
    public IRelayCommand ClearCommand { get; }
    public IRelayCommand ClearSuccessfulCommand { get; }
    public IRelayCommand<OperationViewModel> RemoveCommand { get; }
    public IRelayCommand<OperationViewModel> CopyLogCommand { get; }
    public OperationsViewModel(OperationsQueueService queue, IUiDispatcher dispatcher, IPackageBackend backend, IClipboardService clipboard)
    {
        _queue = queue; _dispatcher = dispatcher; _clipboard = clipboard;
        CancelAllCommand = new RelayCommand(() => { backend.CancelAll(); queue.CancelAll(); });
        ClearCommand = new RelayCommand(queue.ClearFinished);
        ClearSuccessfulCommand = new RelayCommand(queue.ClearSuccessful);
        RemoveCommand = new RelayCommand<OperationViewModel>(item => { if (item is not null) queue.Remove(item.Operation); });
        CopyLogCommand = new RelayCommand<OperationViewModel>(item => _clipboard.TrySetText(item?.Operation.GetFullLog() ?? OutputText));
        queue.PropertyChanged += Changed;
        Refresh();
    }
    partial void OnHasFailedOperationsChanged(bool value) => OnPropertyChanged(nameof(ShowFailureBadge));
    partial void OnIsQueueExpandedChanged(bool value) { OnPropertyChanged(nameof(ShowFailureBadge)); OnPropertyChanged(nameof(ExpanderGlyph)); }
    public void SelectOutput(OperationViewModel item)
    {
        OutputTitle = $"Saída da Operação: {item.Operation.AppName}";
        OutputText = item.Operation.GetFullLog();
    }
    private void Changed(object? sender, PropertyChangedEventArgs args) => _dispatcher.Post(() => { if (!_disposed) Refresh(); });
    private void Refresh()
    {
        var operations = _queue.Operations.Reverse().OrderBy(item => item.IsFinished).ToArray();
        int previousCount = Items.Count;
        var ids = operations.Select(item => item.Id).ToHashSet();
        foreach (var item in Items.Where(item => !ids.Contains(item.Id)).ToArray()) { Items.Remove(item); item.Dispose(); }
        foreach (var item in operations) if (!Items.Any(vm => vm.Id == item.Id)) Items.Add(new OperationViewModel(item, _dispatcher, _queue.ClearFinished));
        for (int index = 0; index < operations.Length; index++)
        {
            int current = Items.IndexOf(Items.Single(vm => vm.Id == operations[index].Id));
            if (current != index) Items.Move(current, index);
        }
        var pending = operations.Count(item => !item.IsFinished);
        PendingCount = pending; HasPending = pending > 0; HasOperations = operations.Length > 0;
        HasFailedOperations = _queue.HasFailedOperations;
        OnPropertyChanged(nameof(QueueDescription)); OnPropertyChanged(nameof(HeaderText));
        if (operations.Length > previousCount && pending > 0) IsQueueOpen = true;
        if (!HasOperations) IsQueueOpen = false;
        Summary = pending > 0 ? $"{pending} operação(ões) em andamento" : operations.Length > 0 ? $"{operations.Length} operação(ões) concluída(s)" : "Nenhuma operação em andamento";
    }
    public void Dispose() { _disposed = true; _queue.PropertyChanged -= Changed; foreach (var item in Items) item.Dispose(); Items.Clear(); }
}
