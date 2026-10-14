using Microsoft.UI.Dispatching;
namespace WinProvision.Store.Services;
public interface IUiDispatcher
{
    void Post(Action action);
    bool CheckAccess();
    Task InvokeAsync(Action action, CancellationToken cancellationToken = default);
}
public sealed partial class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool CheckAccess() => queue.HasThreadAccess;
    public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (queue.HasThreadAccess) { action(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
        {
            try { cancellationToken.ThrowIfCancellationRequested(); action(); completion.SetResult(); }
            catch (OperationCanceledException) { completion.SetCanceled(cancellationToken); }
            catch (Exception exception) { completion.SetException(exception); }
        })) completion.SetException(new InvalidOperationException("O dispatcher está sendo encerrado."));
        return completion.Task;
    }
    public void Post(Action action)
    {
        if (queue.HasThreadAccess) action();
        else if (!queue.TryEnqueue(() => action()))
            System.Diagnostics.Debug.WriteLine("Dispatcher indisponível durante o encerramento.");
    }
}
