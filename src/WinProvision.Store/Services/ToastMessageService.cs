using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using WinProvision.Core.Services;
using Windows.UI;

namespace WinProvision.Store.Services;

/// <summary>Fachada única para política, fila, histórico e roteamento de notificações.</summary>
public sealed class ToastMessageService : INotificationSink
{
    private const int MaximumHistoryEntries = 20;
    private readonly DispatcherQueue? _dispatcherQueue;
    private readonly NotificationPolicyCatalog _catalog;
    private readonly NotificationQueue _queue = new();
    private readonly WindowsAppNotificationService? _windowsChannel;
    private readonly OperationPreferencesService _operationPreferences;
    private readonly NotificationHistoryStore _historyStore;
    private readonly Func<bool> _isAppFocused;
    private readonly Func<bool> _isSystemQuietMode;
    private DispatcherQueueTimer? _activeTimer;
    private ToastMessageItem? _activeItem;

    public ToastMessageService(
        DispatcherQueue? dispatcherQueue,
        NotificationPolicyCatalog? catalog = null,
        WindowsAppNotificationService? windowsChannel = null,
        OperationPreferencesService? operationPreferences = null,
        NotificationHistoryStore? historyStore = null,
        Func<bool>? isAppFocused = null,
        Func<bool>? isSystemQuietMode = null)
    {
        _dispatcherQueue = dispatcherQueue;
        _catalog = catalog ?? LoadCatalog();
        _windowsChannel = windowsChannel;
        _operationPreferences = operationPreferences ?? new OperationPreferencesService();
        _historyStore = historyStore ?? new NotificationHistoryStore();
        _isAppFocused = isAppFocused ?? (() => dispatcherQueue is not null);
        _isSystemQuietMode = isSystemQuietMode ?? WindowsAppNotificationService.ShouldSuppressToast;
        LoadHistory();
    }

    public ObservableCollection<ToastMessageItem> Messages { get; } = [];
    public ObservableCollection<NotificationHistoryEntry> History { get; } = [];
    public event EventHandler? HistoryChanged;

    public ValueTask PublishAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        cancellationToken.ThrowIfCancellationRequested();
        if (_dispatcherQueue is null || _dispatcherQueue.HasThreadAccess)
        {
            Route(notification);
            return ValueTask.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                Route(notification);
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        }))
            throw new InvalidOperationException("Não foi possível encaminhar a notificação para a thread da interface.");

        return new ValueTask(completion.Task);
    }

    private void Route(Notification notification)
    {
        NotificationPolicy policy = _catalog.Resolve(notification.Category);
        OperationPreferences preferences = _operationPreferences.Load();
        if (!NotificationPolicyCatalog.AllowsSeverity(preferences.NotificationLevel, notification.Severity))
            return;

        if (policy.PersistInHistory)
            AddHistory(notification);

        bool appFocused = _isAppFocused();
        NotificationChannel? channel = NotificationPolicyCatalog.SelectChannel(
            policy,
            appFocused,
            preferences.BackgroundWindowsNotificationsEnabled,
            systemQuietMode: !appFocused && _isSystemQuietMode());
        if (channel == NotificationChannel.WindowsToast)
        {
            if (_windowsChannel is null)
                throw new InvalidOperationException("O canal de notificações do Windows não está registrado.");
            _windowsChannel.ShowAsync(notification).GetAwaiter().GetResult();
            return;
        }

        if (channel != NotificationChannel.InApp || _dispatcherQueue is null)
            return;

        if (!_queue.Enqueue(notification, policy, DateTimeOffset.UtcNow))
            return;

        SyncActive(_queue.Active?.Key);
    }

    private void AddHistory(Notification notification)
    {
        var entry = new NotificationHistoryEntry(
            notification.Id,
            notification.Category,
            notification.Severity,
            notification.Title,
            notification.Message,
            DateTimeOffset.UtcNow,
            notification.Origin);
        try
        {
            IReadOnlyList<NotificationHistoryEntry> entries = _historyStore.Add(entry, MaximumHistoryEntries);
            History.Clear();
            foreach (NotificationHistoryEntry historyEntry in entries)
                History.Add(historyEntry);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(exception, "Notifications.History.Save");
            History.Insert(0, entry);
            while (History.Count > MaximumHistoryEntries)
                History.RemoveAt(History.Count - 1);
        }
    }

    private void SyncActive(string? key)
    {
        if (_queue.Active is null)
        {
            ClearActive();
            return;
        }

        if (_activeItem is not null && string.Equals(_activeItem.DedupeKey, key, StringComparison.OrdinalIgnoreCase)
            && string.Equals(_queue.Active.Key, key, StringComparison.OrdinalIgnoreCase))
        {
            _activeItem.Count = _queue.Active.Count;
            _activeItem.PendingCount = _queue.PendingNotificationCount;
            return;
        }

        ClearActive();
        Notification notification = _queue.Active.Notification;
        _activeItem = CreateItem(notification, _queue.Active.Key, _queue.Active.Count, _queue.PendingNotificationCount);
        Messages.Add(_activeItem);

        NotificationPolicy policy = _catalog.Resolve(notification.Category);
        if (notification.Action is null && !policy.RequiresAction && policy.AutoDismissSeconds is int seconds)
        {
            _activeTimer = _dispatcherQueue?.CreateTimer();
            if (_activeTimer is not null)
            {
                _activeTimer.Interval = TimeSpan.FromSeconds(seconds);
                _activeTimer.Tick += (_, _) => DismissActive();
                _activeTimer.Start();
            }
        }
    }

    private void ClearActive()
    {
        _activeTimer?.Stop();
        _activeTimer = null;
        if (_activeItem is not null)
            Messages.Remove(_activeItem);
        _activeItem = null;
    }

    private ToastMessageItem CreateItem(Notification notification, string key, int count, int pendingCount) =>
        new(notification.Id, key, notification.Title, notification.Message,
            SeverityGlyph(notification.Severity),
            new SolidColorBrush(SeverityColor(notification.Severity)))
        { Count = count, PendingCount = pendingCount };

    public void Dismiss(ToastMessageItem item)
    {
        if (!ReferenceEquals(item, _activeItem))
            return;
        DismissActive();
    }

    private void DismissActive()
    {
        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            if (!_dispatcherQueue.TryEnqueue(DismissActive))
                throw new InvalidOperationException("Não foi possível dispensar a notificação na thread da interface.");
            return;
        }

        ClearActive();
        NotificationEnvelope? next = _queue.DismissActive();
        if (next is not null)
            SyncActive(next.Key);
    }

    public void ClearHistory()
    {
        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            if (!_dispatcherQueue.TryEnqueue(ClearHistory))
                throw new InvalidOperationException("Não foi possível limpar o histórico na thread da interface.");
            return;
        }

        _historyStore.Clear();
        History.Clear();
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshHistory()
    {
        if (_dispatcherQueue is not null && !_dispatcherQueue.HasThreadAccess)
        {
            if (!_dispatcherQueue.TryEnqueue(RefreshHistory))
                throw new InvalidOperationException("Não foi possível atualizar o histórico na thread da interface.");
            return;
        }

        IReadOnlyList<NotificationHistoryEntry> entries = LoadHistory();
        History.Clear();
        foreach (NotificationHistoryEntry entry in entries)
            History.Add(entry);
        HistoryChanged?.Invoke(this, EventArgs.Empty);
    }

    private IReadOnlyList<NotificationHistoryEntry> LoadHistory()
    {
        try { return _historyStore.Load(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(exception, "Notifications.History.Load");
            return [];
        }
    }

    public void Pause(ToastMessageItem item)
    {
        if (ReferenceEquals(item, _activeItem))
            _activeTimer?.Stop();
    }

    public void Resume(ToastMessageItem item)
    {
        if (ReferenceEquals(item, _activeItem) && _activeTimer is { IsRunning: false })
            _activeTimer.Start();
    }

    private static NotificationPolicyCatalog LoadCatalog()
    {
        const string resourceName = "WinProvision.Store.Assets.Notifications.notification-policy.json";
        try
        {
            using Stream? stream = typeof(ToastMessageService).Assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
                throw new FileNotFoundException("O recurso da política de notificações não foi encontrado.", resourceName);
            using var reader = new StreamReader(stream);
            return NotificationPolicyCatalog.Load(reader.ReadToEnd(), LogCatalog);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(exception, "Notifications.Policy.Load");
            return NotificationPolicyCatalog.CreateDefault();
        }
    }

    private static void LogCatalog(string message) => CoreLog.Debug($"NOTIFICATION POLICY {message}");

    private static string SeverityGlyph(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => "\uEA39",
        NotificationSeverity.Warning => "\uE7BA",
        NotificationSeverity.Success => "\uE73E",
        _ => "\uE946",
    };

    private static Color SeverityColor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Error => Color.FromArgb(255, 209, 52, 56),
        NotificationSeverity.Warning => Color.FromArgb(255, 255, 185, 0),
        NotificationSeverity.Success => Color.FromArgb(255, 16, 124, 16),
        _ => Color.FromArgb(255, 0, 120, 212),
    };
}
