using System.ComponentModel;
using System.Linq;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Fila global de operações (instalar/atualizar/remover) em andamento, consumida pelo
/// painel flutuante (OperationsQueuePanel). É singleton via DI para que
/// qualquer página/janela (HomePage, AppDetailsOverlay, PackagesPage...) possa enfileirar
/// uma operação e o mesmo painel reflita tudo em tempo real.
/// </summary>
public class OperationsQueueService : INotifyPropertyChanged, IDisposable
{
    private readonly OperationHistoryService? _historyService;

    private readonly System.Threading.Lock _gate = new();
    private readonly List<OperationItem> _operations = [];
    private readonly List<OperationItem> _batch = [];
    private int _batchDepth;
    public event Action<IReadOnlyList<OperationItem>>? BatchCompleted;
    public IDisposable BeginBatch()
    {
        lock (_gate) _batchDepth++;
        return new BatchScope(this);
    }
    private void EndBatch()
    {
        OperationItem[]? completed = null;
        lock (_gate)
        {
            _batchDepth--;
            if (_batchDepth == 0 && _batch.Count > 0 && _operations.All(item => item.IsFinished))
            { completed = _batch.ToArray(); _batch.Clear(); }
        }
        PublishBatch(completed);
    }
    private void PublishBatch(OperationItem[]? completed)
    {
        if (completed is null) return;
        try { BatchCompleted?.Invoke(completed); }
        catch (Exception ex) { CoreLog.Warning(ex, "Operations.BatchNotification"); }
    }
    private sealed class BatchScope(OperationsQueueService queue) : IDisposable
    {
        private OperationsQueueService? _queue = queue;
        public void Dispose() => Interlocked.Exchange(ref _queue, null)?.EndBatch();
    }
    private TaskCompletionSource _idle = CompletedSource();

    public IReadOnlyList<OperationItem> Operations
    {
        get { lock (_gate) return _operations.ToArray(); }
    }

    public Task WaitForIdleAsync()
    {
        lock (_gate) return _idle.Task;
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public OperationsQueueService(OperationHistoryService? historyService = null)
    {
        _historyService = historyService;
    }

    public int TotalCount => Operations.Count;

    public int CompletedCount => Operations.Count(o => o.IsFinished);

    public bool HasOperations => Operations.Count > 0;

    public bool HasFailedOperations => Operations.Any(o => o.State == OperationState.Failed);

    /// <summary>Progresso agregado da fila, contando operações concluídas como 100%.</summary>
    public double OverallProgress
    {
        get
        {
            var snapshot = Operations;
            return snapshot.Count == 0 ? 0 : snapshot.Sum(operation => operation.IsFinished
                ? 100
                : operation.State == OperationState.Running && !operation.IsIndeterminate
                    ? Math.Clamp(operation.Progress, 0, 100)
                    : 0) / snapshot.Count;
        }
    }

    public bool IsProgressIndeterminate
    {
        get
        {
            var snapshot = Operations;
            return snapshot.Count > 0 && snapshot.Any(operation => !operation.IsFinished)
                && !snapshot.Any(operation => operation.State == OperationState.Running && !operation.IsIndeterminate);
        }
    }

    public OperationItem Enqueue(string appName, OperationKind kind, string? iconUrl = null)
    {
        var item = new OperationItem(appName, kind, iconUrl);
        lock (_gate)
        {
            if (_idle.Task.IsCompleted)
            {
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_batchDepth == 0) _batch.Clear();
            }
            _batch.Add(item);
            _operations.Add(item);
            item.PropertyChanged += Item_PropertyChanged;
        }
        NotifyQueueChanged();
        return item;
    }

    public void Remove(OperationItem item)
    {
        // Never dispose a token source while a worker may still be using it.
        if (!item.IsFinished) return;
        lock (_gate)
        {
            if (!_operations.Remove(item)) return;
            item.PropertyChanged -= Item_PropertyChanged;
        }
        item.Dispose();
        OnPropertyChanged(nameof(Operations));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(HasOperations));
        OnPropertyChanged(nameof(HasFailedOperations));
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OperationItem.Progress)
            or nameof(OperationItem.IsIndeterminate)
            or nameof(OperationItem.State)
            or nameof(OperationItem.IsFinished))
        {
            OnPropertyChanged(nameof(OverallProgress));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
        }

        if (e.PropertyName == nameof(OperationItem.IsFinished) && sender is OperationItem item)
        {
            OnPropertyChanged(nameof(CompletedCount));
            OnPropertyChanged(nameof(HasFailedOperations));
            _historyService?.Record(item);
            OperationItem[]? completedBatch = null;
            lock (_gate)
            {
                if (_operations.All(operation => operation.IsFinished))
                {
                    _idle.TrySetResult();
                    if (_batchDepth == 0) { completedBatch = _batch.ToArray(); _batch.Clear(); }
                }
            }
            PublishBatch(completedBatch);
        }
        else if (e.PropertyName == nameof(OperationItem.State))
        {
            OnPropertyChanged(nameof(HasFailedOperations));
        }
    }

    private void NotifyQueueChanged()
    {
        OnPropertyChanged(nameof(Operations));
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(HasOperations));
        OnPropertyChanged(nameof(HasFailedOperations));
        OnPropertyChanged(nameof(OverallProgress));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    /// <summary>Remove da lista as operações já finalizadas (concluídas, com falha ou canceladas).</summary>
    public void ClearFinished()
    {
        foreach (var item in Operations.Where(o => o.IsFinished).ToList())
        {
            Remove(item);
        }
    }

    /// <summary>Remove da lista apenas as operações concluídas com êxito.</summary>
    public void ClearSuccessful()
    {
        foreach (var item in Operations.Where(o => o.State == OperationState.Completed).ToList())
        {
            Remove(item);
        }
    }

    /// <summary>Cancela todas as operações em andamento ou na fila.</summary>
    public void CancelAll()
    {
        foreach (var item in Operations.Where(o => !o.IsFinished).ToList())
        {
            if (item.CanCancel)
            {
                item.Cancel();
            }
        }
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    public void Dispose()
    {
        foreach (var operation in Operations)
        {
            operation.PropertyChanged -= Item_PropertyChanged;
            if (operation.IsFinished) operation.Dispose();
        }
    }

}
