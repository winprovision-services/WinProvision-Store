namespace WinProvision.Core.Services;

public sealed record AppLogTaskView(AppLogEvent Started, AppLogEvent? Finished, bool IsInterrupted)
{
    public Guid TaskId => Started.TaskId!.Value;
}

public static class AppLogTaskCorrelator
{
    public static IReadOnlyList<AppLogTaskView> Correlate(
        IEnumerable<AppLogEvent> events,
        string? activeSessionId = null)
    {
        var tasks = new Dictionary<Guid, (AppLogEvent? Started, AppLogEvent? Finished)>();
        foreach (AppLogEvent logEvent in events)
        {
            if (logEvent.TaskId is not { } taskId)
                continue;

            tasks.TryGetValue(taskId, out var current);
            if (logEvent.Event.Equals("Started", StringComparison.OrdinalIgnoreCase))
                current.Started = logEvent;
            else if (logEvent.Event.Equals("Finished", StringComparison.OrdinalIgnoreCase))
                current.Finished = logEvent;
            else
                continue;
            tasks[taskId] = current;
        }

        return tasks.Values
            .Where(task => task.Started is not null)
            .Select(task => new AppLogTaskView(
                task.Started!,
                task.Finished,
                task.Finished is null
                    && (activeSessionId is null
                        || !task.Started!.SessionId.Equals(activeSessionId, StringComparison.Ordinal))))
            .OrderBy(task => task.Started.Timestamp)
            .ToArray();
    }
}
