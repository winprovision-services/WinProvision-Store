using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public sealed class OperationHistoryService
{
    private const int MaximumEntries = 200;
    private const int MaximumDetailsLength = 16_384;
    private readonly string _filePath;
    private readonly string _taskLogPath;

    private readonly System.Threading.Lock _gate = new();
    private readonly List<OperationHistoryEntry> _entries = [];
    public event Action? Changed;
    public IReadOnlyList<OperationHistoryEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public OperationHistoryService(string? filePath = null)
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore");
        _filePath = filePath ?? Path.Combine(directory, "operation-history.json");
        _taskLogPath = filePath is null
            ? Path.Combine(directory, "operation-tasks.log")
            : Path.ChangeExtension(_filePath, ".log");
        Load();
    }

    public string TaskLogPath => _taskLogPath;

    public void Record(OperationItem item)
    {
        lock (_gate)
        {
            if (!item.IsFinished || _entries.Any(entry => entry.OperationId == item.Id)) return;
            string details = item.GetFullLog();
            if (details.Length > MaximumDetailsLength)
                details = "… saída anterior omitida …" + Environment.NewLine + details[^MaximumDetailsLength..];
            var entry = new OperationHistoryEntry(
                item.Id, item.AppName, item.Kind, item.State, DateTimeOffset.Now, item.Method,
                item.StartedAt, details);
            _entries.Insert(0, entry);
            if (_entries.Count > MaximumEntries)
                _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
            Save();
            AppendTaskLog(entry);
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
            try
            {
                if (File.Exists(_taskLogPath)) File.Delete(_taskLogPath);
                if (File.Exists(_taskLogPath + ".previous")) File.Delete(_taskLogPath + ".previous");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLog.Warning(ex, "OperationHistoryService.ClearLog"); }
        }
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var entries = WinProvision.Core.Services.AotJson.Deserialize<OperationHistoryEntry[]>(
                File.ReadAllText(_filePath), WinProvisionJsonOptions.Compact) ?? [];
            foreach (OperationHistoryEntry entry in entries.Take(MaximumEntries))
            {
                _entries.Add(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            CoreLog.Warning(ex, "OperationHistoryService");
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            AtomicFile.WriteAllText(_filePath, WinProvision.Core.Services.AotJson.Serialize(_entries, WinProvisionJsonOptions.Compact));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(ex, "OperationHistoryService");
        }
    }

    private void AppendTaskLog(OperationHistoryEntry entry)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_taskLogPath)!);
            string details = SensitiveDataRedactor.Redact(entry.LogDetails);
            string text = $"{entry.KindLabel} · {SensitiveDataRedactor.Redact(entry.AppName)} — {entry.StateLabel}{Environment.NewLine}" +
                $"{entry.StartLine}{Environment.NewLine}{entry.EndLine}{Environment.NewLine}{entry.DurationLine}{Environment.NewLine}{entry.MethodLine}{Environment.NewLine}" +
                $"{Environment.NewLine}-- Saída da operação{Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}" +
                "——————————————————————————————————————————" + Environment.NewLine + Environment.NewLine;
            if (File.Exists(_taskLogPath) && new FileInfo(_taskLogPath).Length >= 10L * 1024 * 1024)
                File.Move(_taskLogPath, _taskLogPath + ".previous", overwrite: true);
            File.AppendAllText(_taskLogPath, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            CoreLog.Warning(ex, "OperationHistoryService.TaskLog");
        }
    }
}
