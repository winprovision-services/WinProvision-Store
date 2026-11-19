namespace WinProvision.Core.Services;

public sealed class NotificationHistoryStore
{
    private const int DefaultMaximumEntries = 20;
    private readonly string _path;

    public NotificationHistoryStore(string? path = null) =>
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStoreWinUI3",
            "notification-history.json");

    public IReadOnlyList<NotificationHistoryEntry> Load() => WithLock(Read);

    public IReadOnlyList<NotificationHistoryEntry> Add(NotificationHistoryEntry entry, int maximumEntries = DefaultMaximumEntries) =>
        WithLock(() =>
        {
            NotificationHistoryEntry[] updated = Read()
                .Prepend(entry)
                .OrderByDescending(item => item.CreatedAt)
                .Take(Math.Clamp(maximumEntries, 1, 100))
                .ToArray();
            Write(updated);
            return updated;
        });

    public void Clear() => WithLock(() =>
    {
        Write([]);
        return true;
    });

    private IReadOnlyList<NotificationHistoryEntry> Read()
    {
        if (!File.Exists(_path))
            return [];

        try
        {
            return AotJson.Deserialize<NotificationHistoryEntry[]>(File.ReadAllText(_path))
                ?.Where(entry => entry is not null)
                .OrderByDescending(entry => entry.CreatedAt)
                .Take(DefaultMaximumEntries)
                .ToArray() ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            CoreLog.Warning(exception, "Notifications.History.Load");
            return [];
        }
    }

    private void Write(IReadOnlyList<NotificationHistoryEntry> entries)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        AtomicFile.WriteAllText(_path, AotJson.Serialize(entries.ToArray()));
    }

    private T WithLock<T>(Func<T> action)
    {
        using var mutex = new Mutex(initiallyOwned: false, @"Local\WinProvision.Store.NotificationHistory");
        bool acquired;
        try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
            throw new IOException("O histórico de notificações está sendo atualizado por outro processo.");

        try { return action(); }
        finally { mutex.ReleaseMutex(); }
    }
}
