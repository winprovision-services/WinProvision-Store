using System.Text.Json;
using WinProvision.Core.Services;
using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

internal static class PackageManagerLogReader
{
    private const int MaximumEvents = 100;
    private const int MaximumVisibleCharacters = ManagerTaskLogFormatter.DefaultMaximumCharacters;

    public static async Task<string> ReadAsync(string? source, IAppLog log, CancellationToken cancellationToken = default,
        string? logDirectoryOverride = null)
    {
        string? category = PackageManagerLogCategories.ForSource(source);
        if (category is null) return "Nenhum registro ainda";

        await log.FlushAsync(cancellationToken).ConfigureAwait(false);
        string directory = logDirectoryOverride ?? log.LogDirectory;
        if (!Directory.Exists(directory)) return "Nenhum registro ainda";

        var events = new Queue<AppLogEvent>(MaximumEvents);
        foreach (string path in Directory.EnumerateFiles(directory, AppLogPaths.DiagnosticLogFilePattern)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await foreach (AppLogEvent logEvent in AppLogJsonLinesReader.ReadForDisplayAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (!logEvent.Category.Equals(category, StringComparison.OrdinalIgnoreCase)
                    && !logEvent.Event.Equals("UnparsedLine", StringComparison.OrdinalIgnoreCase)) continue;
                if (events.Count == MaximumEvents) events.Dequeue();
                events.Enqueue(logEvent);
            }
        }

        if (events.Count == 0) return "Nenhum registro ainda";
        return string.Join(Environment.NewLine, events.Select(entry => entry.Event.Equals("UnparsedLine", StringComparison.OrdinalIgnoreCase)
            ? entry.Message
            : $"[{entry.Timestamp.ToLocalTime():dd/MM/yyyy HH:mm:ss}] {entry.Message}"));
    }

    public static async Task<IReadOnlyList<(string Text, AppLogLevel Level)>> ReadApplicationEventsAsync(
        IAppLog log, CancellationToken cancellationToken = default)
    {
        await log.FlushAsync(cancellationToken).ConfigureAwait(false);
        string directory = log.LogDirectory;
        if (!Directory.Exists(directory)) return [];
        var events = new Queue<(string Text, AppLogLevel Level)>(MaximumEvents);
        long totalCharacters = 0;
        foreach (string path in Directory.EnumerateFiles(directory, AppLogPaths.DiagnosticLogFilePattern)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await foreach (AppLogEvent entry in AppLogJsonLinesReader.ReadForDisplayAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (events.Count == MaximumEvents) events.Dequeue();
                string text = entry.Event.Equals("UnparsedLine", StringComparison.OrdinalIgnoreCase)
                    ? entry.Message
                    : $"[{entry.Timestamp.ToLocalTime():dd/MM/yyyy HH:mm:ss}] [{LocalizeLevel(entry.Level)}] {entry.Category}: {entry.Message}";
                totalCharacters += text.Length;
                events.Enqueue((text, ParseLevel(entry.Level)));
                while (events.Count > 1 && totalCharacters > MaximumVisibleCharacters)
                {
                    totalCharacters -= events.Dequeue().Text.Length;
                }
            }
        }
        return events.ToArray();
    }

    public static async Task<IReadOnlyList<(string Text, AppLogLevel Level)>> ReadLegacyManagerEventsAsync(
        string manager, string directory, CancellationToken cancellationToken = default, int maximumEvents = MaximumEvents)
    {
        string? category = PackageManagerLogCategories.ForSource(manager);
        if (category is null || !Directory.Exists(directory)) return [];
        var events = new Queue<(string Text, AppLogLevel Level)>(maximumEvents);
        long totalCharacters = 0;
        foreach (string path in Directory.EnumerateFiles(directory, AppLogPaths.DiagnosticLogFilePattern)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            await foreach (AppLogEvent entry in AppLogJsonLinesReader.ReadAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (!entry.Category.Equals(category, StringComparison.OrdinalIgnoreCase)
                    && !entry.Event.Equals("UnparsedLine", StringComparison.OrdinalIgnoreCase)) continue;
                if (events.Count == maximumEvents) events.Dequeue();
                string text = entry.Event.Equals("UnparsedLine", StringComparison.OrdinalIgnoreCase)
                    ? entry.Message
                    : $"[{entry.Timestamp.ToLocalTime():dd/MM/yyyy HH:mm:ss}] {entry.Message}";
                totalCharacters += text.Length;
                events.Enqueue((text, ParseLevel(entry.Level)));
                while (events.Count > 1 && totalCharacters > MaximumVisibleCharacters)
                    totalCharacters -= events.Dequeue().Text.Length;
            }
        }
        return events.ToArray();
    }

    private static AppLogLevel ParseLevel(string level) => Enum.TryParse(level, true, out AppLogLevel parsed) ? parsed : AppLogLevel.Debug;
    private static string LocalizeLevel(string level) => ParseLevel(level) switch
    {
        AppLogLevel.Warning => "Aviso",
        AppLogLevel.Error => "Erro",
        AppLogLevel.Info => "Info",
        AppLogLevel.Debug => "Depuração",
        _ => level
    };

    public static async Task<IReadOnlyList<ManagerTaskLogRecord>> ReadLegacyTasksAsync(
        string manager, string directory, CancellationToken cancellationToken = default, int maximumTasks = MaximumEvents)
    {
        string? category = PackageManagerLogCategories.ForSource(manager);
        if (category is null || !Directory.Exists(directory)) return [];
        var started = new Dictionary<Guid, AppLogEvent>();
        var finished = new Queue<ManagerTaskLogRecord>();
        foreach (string path in Directory.EnumerateFiles(directory, AppLogPaths.DiagnosticLogFilePattern)
                     .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            await foreach (AppLogEvent entry in AppLogJsonLinesReader.ReadAsync(path, cancellationToken).ConfigureAwait(false))
            {
                if (entry.Event.Equals("UnparsedLine", StringComparison.OrdinalIgnoreCase))
                {
                    finished.Enqueue(new ManagerTaskLogRecord
                    {
                        Manager = manager, TaskType = "Outra tarefa", Kind = "Native",
                        StartedAt = entry.Timestamp == DateTimeOffset.MinValue ? DateTimeOffset.UtcNow : entry.Timestamp,
                        TaskInformation = entry.Message, Result = "warning"
                    });
                    while (finished.Count > maximumTasks) finished.Dequeue();
                    continue;
                }
                if (!entry.Category.Equals(category, StringComparison.OrdinalIgnoreCase) || entry.TaskId is not Guid id
                    || !TryGetString(entry.Data, "taskKind", out string kind)) continue;
                if (entry.Event.Equals("Started", StringComparison.OrdinalIgnoreCase))
                {
                    started[id] = entry;
                    continue;
                }
                if (!entry.Event.Equals("Finished", StringComparison.OrdinalIgnoreCase)) continue;
                started.TryGetValue(id, out AppLogEvent? start);
                started.Remove(id);
                JsonElement data = entry.Data;
                int? exitCode = data.TryGetProperty("exitCode", out JsonElement code) && code.TryGetInt32(out int parsedCode) ? parsedCode : null;
                string? managerName = TryGetString(data, "manager", out string managerValue) ? managerValue : manager;
                string taskType = TryGetString(data, "taskType", out string typeValue) ? typeValue : "Outra tarefa";
                string? executable = TryGetString(data, "executable", out string exe) ? exe : null;
                string? arguments = TryGetArguments(data);
                string output = TryGetString(data, "standardOutput", out string stdout) ? stdout : string.Empty;
                string error = TryGetString(data, "standardError", out string stderr) ? stderr : string.Empty;
                string information = TryGetString(data, "result", out string resultText) ? resultText : string.Empty;
                DateTimeOffset startedAt = TryGetDate(data, "startedAt") ?? start?.Timestamp ?? entry.Timestamp;
                DateTimeOffset? finishedAt = TryGetDate(data, "finishedAt") ?? entry.Timestamp;
                finished.Enqueue(new ManagerTaskLogRecord
                {
                    TaskId = id, Manager = managerName, TaskType = LocalizeTaskType(taskType), Kind = kind,
                    Executable = executable, Arguments = arguments, StartedAt = startedAt, FinishedAt = finishedAt,
                    StandardOutput = output, StandardError = error, TaskInformation = information, ExitCode = exitCode,
                    Result = exitCode is null ? "warning" : exitCode == 0 ? "success" : "error"
                });
                while (finished.Count > maximumTasks) finished.Dequeue();
            }
        }
        return finished.ToArray();
    }

    private static bool TryGetString(JsonElement data, string name, out string value)
    {
        value = string.Empty;
        if (!data.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static string? TryGetArguments(JsonElement data)
    {
        if (!data.TryGetProperty("arguments", out JsonElement value)) return null;
        return value.ValueKind == JsonValueKind.Array ? string.Join(" ", value.EnumerateArray().Select(item => item.GetString() ?? string.Empty))
            : value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static DateTimeOffset? TryGetDate(JsonElement data, string name) =>
        data.TryGetProperty(name, out JsonElement value) && value.TryGetDateTimeOffset(out DateTimeOffset date) ? date : null;

    private static string LocalizeTaskType(string type) => type switch
    {
        "UpdateSources" or "UpdateIndexes" or "RefreshIndexes" => "Atualizar índices",
        "ListUpdates" => "Listar atualizações",
        "ListSources" => "Listar fontes",
        "ListInstalled" or "ListInstalledPackages" => "Listar instalados",
        "Search" or "FindPackages" => "Buscar",
        "Install" => "Instalar",
        "Update" => "Atualizar",
        "Uninstall" => "Desinstalar",
        _ => "Outra tarefa"
    };

}
