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
    private readonly string _filePath;

    private readonly System.Threading.Lock _gate = new();
    private readonly List<OperationHistoryEntry> _entries = [];
    public event Action? Changed;
    public IReadOnlyList<OperationHistoryEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public OperationHistoryService()
    {
        string directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore");
        _filePath = Path.Combine(directory, "operation-history.json");
        Load();
    }

    public void Record(OperationItem item)
    {
        lock (_gate)
        {
            if (!item.IsFinished || _entries.Any(entry => entry.OperationId == item.Id)) return;
            _entries.Insert(0, new OperationHistoryEntry(
                item.Id, item.AppName, item.Kind, item.State, DateTimeOffset.Now, item.Method));
            if (_entries.Count > MaximumEntries)
                _entries.RemoveRange(MaximumEntries, _entries.Count - MaximumEntries);
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            Save();
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
}
