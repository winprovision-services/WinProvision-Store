using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.Encodings.Web;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>Único repositório de histórico. Cada registro é uma linha JSON e a saída fica em logs/operations.</summary>
public class HistoryStore
{
    private const int MaximumEntries = 1000;
    private static readonly HistoryWriteJsonContext HistoryWriteContext = CreateHistoryWriteContext();
    private readonly string _filePath;
    private readonly string _outputDirectory;
    private readonly string _legacyTaskLogPath;
    private readonly System.Threading.Lock _gate = new();
    private readonly List<OperationHistoryRecord> _records = [];
    public event Action? Changed;

    public string FilePath => _filePath;
    public string LogsDirectory => Path.Combine(Path.GetDirectoryName(_filePath)!, "logs");
    public string TaskLogPath => _legacyTaskLogPath;
    public IReadOnlyList<OperationHistoryRecord> Records { get { lock (_gate) return _records.ToArray(); } }
    // Compatibility projection for callers from older versions; the UI uses Query exclusively.
    public IReadOnlyList<OperationHistoryEntry> Entries => Records.Select(ToLegacyEntry).ToArray();

    public HistoryStore(string? filePath = null)
    {
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore");
        _filePath = filePath ?? Path.Combine(directory, "operation-history.jsonl");
        _outputDirectory = Path.Combine(Path.GetDirectoryName(_filePath)!, "logs", "operations");
        _legacyTaskLogPath = filePath is null
            ? Path.Combine(directory, "operation-tasks.log")
            : Path.ChangeExtension(_filePath, ".log");
        Load();
    }

    public IReadOnlyList<OperationHistoryRecord> Query(string? text = null, string? status = null,
        string? type = null, string? manager = null)
    {
        string[] terms = (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return Records.Where(record =>
            (string.IsNullOrWhiteSpace(status) || record.Status.ToValue().Equals(status, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(type) || record.Type.ToValue().Equals(type, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(manager) || string.Equals(record.Manager, manager, StringComparison.OrdinalIgnoreCase))
            && terms.All(term => string.Join(" ", record.PackageName, record.PackageId, record.Type.ToValue(), record.Status.ToValue(),
                record.Status == OperationHistoryStatus.Failed ? "Falhou" : record.Status == OperationHistoryStatus.Succeeded ? "Bem-sucedida" : "Cancelada",
                record.Manager, record.SourceName, ReadOutput(record)).Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public IReadOnlyList<OperationHistoryEntry> Search(string? query) => Query(query).Select(ToLegacyEntry).ToArray();

    public void Add(OperationItem item)
    {
        lock (_gate)
        {
            if (!item.IsFinished || _records.Any(record => record.Id == item.Id)) return;
            DateTimeOffset finished = DateTimeOffset.UtcNow;
            DateTimeOffset started = item.StartedAt?.ToUniversalTime() ?? finished;
            string outputRelative = Path.Combine("logs", "operations", item.Id.ToString("D") + ".log").Replace('\\', '/');
            string outputPath = Path.Combine(Path.GetDirectoryName(_filePath)!, outputRelative.Replace('/', Path.DirectorySeparatorChar));
            string methodLabel = item.Method switch
            {
                WingetMethod.ComApi => "API do WinGet",
                WingetMethod.OwnApi => "API da Store",
                WingetMethod.WingetExe => "WinGet CLI",
                _ => "Método não identificado"
            };
            string output = SensitiveDataRedactor.Redact($"Método: {methodLabel}{Environment.NewLine}{item.GetFullLog()}");
            try
            {
                Directory.CreateDirectory(_outputDirectory);
                AtomicFile.WriteAllText(outputPath, output);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { CoreLog.Warning(ex, "HistoryStore.WriteOutput"); }

            string effectiveManager = (item.ManagerOverride ?? item.Manager).Equals("chocolatey", StringComparison.OrdinalIgnoreCase)
                ? "chocolatey" : "winget";
            string? sourceName = NullIfBlank(item.SourceNameOverride ?? item.SourceName);
            sourceName ??= effectiveManager == "chocolatey" ? "community" : "winget";
            var record = new OperationHistoryRecord
            {
                Id = item.Id,
                Type = item.Kind switch { OperationKind.Install => OperationHistoryType.Install, OperationKind.Update => OperationHistoryType.Update, OperationKind.Uninstall => OperationHistoryType.Uninstall, _ => OperationHistoryType.Download },
                SchemaVersion = 3,
                Snapshot = item.Snapshot,
                PackageName = NullIfBlank(item.AppName),
                PackageId = NullIfBlank(item.PackageId),
                Manager = (item.ManagerOverride ?? item.Manager).Equals("office", StringComparison.OrdinalIgnoreCase) ? null : effectiveManager,
                SourceName = (item.ManagerOverride ?? item.Manager).Equals("office", StringComparison.OrdinalIgnoreCase) ? null : sourceName,
                VersionFrom = NullIfBlank(item.VersionFrom),
                VersionTo = NullIfBlank(item.VersionTo),
                Status = item.State switch { OperationState.Completed => OperationHistoryStatus.Succeeded, OperationState.Canceled => OperationHistoryStatus.Cancelled, _ => OperationHistoryStatus.Failed },
                StartedAt = started,
                FinishedAt = finished,
                ExitCode = item.ExitCode,
                OutputFile = outputRelative
            };
            _records.Insert(0, record);
            TrimRetention();
            Save();
            AppendLegacyTaskLog(record, output);
        }
        Changed?.Invoke();
    }

    public void Record(OperationItem item) => Add(item);

    public void UpdateSnapshot(Guid id, OperationSnapshot snapshot)
    {
        lock (_gate)
        {
            int index = _records.FindIndex(record => record.Id == id);
            if (index < 0) return;
            _records[index] = _records[index] with { Snapshot = snapshot, SchemaVersion = 3 };
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            OperationHistoryRecord? record = _records.FirstOrDefault(candidate => candidate.Id == id);
            removed = record is not null && _records.Remove(record);
            if (!removed) return;
            Save();
            DeleteOutput(record!);
        }
        Changed?.Invoke();
    }

    public void Remove(OperationHistoryRecord record) => Remove(record.Id);

    public void Clear()
    {
        lock (_gate)
        {
            OperationHistoryRecord[] old = _records.ToArray();
            _records.Clear();
            Save();
            foreach (OperationHistoryRecord record in old) DeleteOutput(record);
            try { if (File.Exists(_legacyTaskLogPath)) File.Delete(_legacyTaskLogPath); } catch (IOException ex) { CoreLog.Warning(ex, "HistoryStore.ClearLog"); }
        }
        Changed?.Invoke();
    }

    public string GetOutputPath(OperationHistoryRecord record)
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_filePath)!, record.OutputFile.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.GetFullPath(_outputDirectory) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("O caminho de saída do histórico está fora da pasta permitida.");
        return path;
    }

    public string ReadOutput(OperationHistoryRecord record)
    {
        try
        {
            string path = GetOutputPath(record);
            if (string.IsNullOrWhiteSpace(record.OutputFile) || !File.Exists(path)) return string.Empty;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true);
            return DecodeEscapedUnicode(reader.ReadToEnd());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { CoreLog.Warning(ex, "HistoryStore.ReadOutput"); return string.Empty; }
    }

    private static string DecodeEscapedUnicode(string text)
    {
        if (!text.Contains("\\u", StringComparison.OrdinalIgnoreCase)) return text;
        return System.Text.RegularExpressions.Regex.Replace(text, "\\\\u([0-9a-fA-F]{4})", match =>
        {
            int code = Convert.ToInt32(match.Groups[1].Value, 16);
            if (char.IsHighSurrogate((char)code) && match.NextMatch() is { Success: true } low
                && low.Index == match.Index + match.Length && low.Value.StartsWith("\\u", StringComparison.OrdinalIgnoreCase))
            {
                int lowCode = Convert.ToInt32(low.Groups[1].Value, 16);
                if (char.IsLowSurrogate((char)lowCode)) return new string([(char)code, (char)lowCode]);
            }
            return char.ConvertFromUtf32(code);
        });
    }

    private void Load()
    {
        bool migratedSchema = false;
        try
        {
            Dictionary<Guid, (string? Method, string? Details)> legacyMetadata = ReadLegacyMetadata(Path.ChangeExtension(_filePath, ".json"));
            if (File.Exists(_filePath))
            {
                foreach (string line in File.ReadLines(_filePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        OperationHistoryRecord? record = JsonSerializer.Deserialize(line, HistoryJsonContext.Default.OperationHistoryRecord);
                        if (record is not null && record.Id != Guid.Empty)
                        {
                            bool wasLegacySchema = record.SchemaVersion < 2;
                            if (record.SchemaVersion < 2)
                            {
                                record = record with { SchemaVersion = 2 };
                                migratedSchema = true;
                            }
                            legacyMetadata.TryGetValue(record.Id, out var legacy);
                            string output = ReadOutput(record);
                            (string? manager, string? sourceName, bool isOffice) = RecoverOrigin(record, output, legacy.Method, legacy.Details);
                            string? recoveredPackageId = isOffice ? "Office" : NullIfBlank(record.PackageId);
                            if (wasLegacySchema && string.Equals(recoveredPackageId, record.PackageName, StringComparison.OrdinalIgnoreCase))
                                recoveredPackageId = null;
                            string? recoveredVersionFrom = NullIfBlank(record.VersionFrom);
                            string? recoveredVersionTo = NullIfBlank(record.VersionTo);
                            string? recoveredName = NullIfBlank(record.PackageName);
                            if (record.PackageName != recoveredName || record.Manager != manager || record.SourceName != sourceName
                                || record.PackageId != recoveredPackageId || record.VersionFrom != recoveredVersionFrom || record.VersionTo != recoveredVersionTo)
                            {
                                record = record with { PackageName = recoveredName, Manager = manager, SourceName = sourceName,
                                    PackageId = recoveredPackageId, VersionFrom = recoveredVersionFrom, VersionTo = recoveredVersionTo };
                                migratedSchema = true;
                            }
                            if (!string.IsNullOrWhiteSpace(record.OutputFile))
                            {
                                try { _ = GetOutputPath(record); }
                                catch (InvalidDataException exception) { CoreLog.Warning(exception, "HistoryStore.InvalidOutputPath"); record = record with { OutputFile = string.Empty }; }
                            }
                            _records.Add(record);
                        }
                    }
                    catch (JsonException exception) { CoreLog.Warning(exception, "HistoryStore.CorruptLine"); }
                }
            }
            else
            {
                string legacyPath = Path.ChangeExtension(_filePath, ".json");
                if (File.Exists(legacyPath)) MigrateLegacy(legacyPath);
            }
            _records.Sort((left, right) => right.FinishedAt.CompareTo(left.FinishedAt));
            if (migratedSchema) Save();
            if (_records.Count > MaximumEntries) { var removed = _records.Skip(MaximumEntries).ToArray(); _records.RemoveRange(MaximumEntries, _records.Count - MaximumEntries); foreach (var record in removed) DeleteOutput(record); Save(); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { CoreLog.Warning(ex, "HistoryStore.Load"); }
    }

    private static Dictionary<Guid, (string? Method, string? Details)> ReadLegacyMetadata(string path)
    {
        var result = new Dictionary<Guid, (string? Method, string? Details)>();
        try
        {
            if (!File.Exists(path)) return result;
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return result;
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                if (!TryGetString(entry, "operationId", out string? idText) || !Guid.TryParse(idText, out Guid id)) continue;
                TryGetString(entry, "method", out string? method);
                if (string.IsNullOrWhiteSpace(method)) TryGetString(entry, "methodLabel", out method);
                if (!TryGetString(entry, "logDetails", out string? details)) TryGetString(entry, "details", out details);
                result[id] = (method, details);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { CoreLog.Warning(exception, "HistoryStore.ReadLegacyMetadata"); }
        return result;
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        value = null;
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.String && (value = property.GetString()) is not null;
    }

    private static (string? Manager, string? SourceName, bool IsOffice) RecoverOrigin(
        OperationHistoryRecord record, string output, string? legacyMethod, string? legacyDetails)
    {
        string text = string.Join(Environment.NewLine, output, legacyDetails, legacyMethod);
        bool isOffice = string.Equals(record.PackageId, "Office", StringComparison.OrdinalIgnoreCase)
            || (record.PackageName?.Contains("Microsoft 365", StringComparison.OrdinalIgnoreCase) == true
                && text.Contains("Office", StringComparison.OrdinalIgnoreCase))
            || (record.PackageName?.Contains("Remoção completa do Office", StringComparison.OrdinalIgnoreCase) == true);
        if (isOffice) return (null, null, true);

        string? manager = NormalizeManager(record.Manager) ?? NormalizeManager(legacyMethod);
        string? sourceName = NullIfBlank(record.SourceName);
        string lower = text.ToLowerInvariant();
        string? reportedSource = ExtractReportedSource(lower);
        if (manager is null)
        {
            if (reportedSource is "chocolatey" or "choco" || lower.Contains("chocolatey", StringComparison.Ordinal)
                || lower.Contains("community.chocolatey.org", StringComparison.Ordinal))
                manager = "chocolatey";
            else if (reportedSource is "winget" or "msstore" || legacyMethod is not null && IsWingetMethod(legacyMethod)
                || lower.Contains("winget", StringComparison.Ordinal) || lower.Contains("fonte=automática", StringComparison.Ordinal)
                || lower.Contains("fonte=automatica", StringComparison.Ordinal))
                manager = "winget";
        }
        if (manager == "chocolatey") sourceName = "community";
        else if (manager == "winget")
            sourceName = reportedSource == "msstore" ? "Microsoft Store" : sourceName ?? "winget";
        return (manager, sourceName, false);
    }

    private static string? NormalizeManager(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim().ToLowerInvariant();
        if (normalized is "office") return null;
        if (normalized is "chocolatey" or "choco" || normalized.Contains("chocolatey", StringComparison.Ordinal)) return "chocolatey";
        if (normalized is "winget" or "msstore" || IsWingetMethod(normalized)) return "winget";
        return null;
    }

    private static bool IsWingetMethod(string value)
    {
        string normalized = value.Trim();
        return normalized.Equals("ComApi", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("OwnApi", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("WingetExe", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("WinGet", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("API da Store", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ExtractReportedSource(string text)
    {
        const string marker = "fonte=";
        int start = text.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        int end = text.IndexOfAny([';', ',', ' ', '\r', '\n'], start);
        return (end < 0 ? text[start..] : text[start..end]).Trim().TrimEnd('.');
    }

    private void MigrateLegacy(string path)
    {
        try
        {
            OperationHistoryEntry[]? legacy = AotJson.Deserialize<OperationHistoryEntry[]>(File.ReadAllText(path), WinProvisionJsonOptions.Compact);
            if (legacy is null) return;
            Directory.CreateDirectory(_outputDirectory);
            foreach (OperationHistoryEntry old in legacy.Take(MaximumEntries))
            {
                string outputRelative = Path.Combine("logs", "operations", old.OperationId.ToString("D") + ".log").Replace('\\', '/');
                string outputPath = Path.Combine(Path.GetDirectoryName(_filePath)!, outputRelative.Replace('/', Path.DirectorySeparatorChar));
                string output = SensitiveDataRedactor.Redact(old.LogDetails);
                AtomicFile.WriteAllText(outputPath, output);
                OperationHistoryType type = old.Kind switch { OperationKind.Update => OperationHistoryType.Update, OperationKind.Uninstall => OperationHistoryType.Uninstall, _ => OperationHistoryType.Install };
                var provisional = new OperationHistoryRecord { Id = old.OperationId, Type = type, PackageName = NullIfBlank(old.AppName) };
                (string? manager, string? sourceName, bool isOffice) = RecoverOrigin(provisional, output, old.Method.ToString(), old.LogDetails);
                _records.Add(new OperationHistoryRecord
                {
                    SchemaVersion = 2,
                    Id = old.OperationId, Type = type,
                    PackageName = NullIfBlank(old.AppName), PackageId = isOffice ? "Office" : null, Manager = manager, SourceName = sourceName, Status = old.State switch
                    { OperationState.Completed => OperationHistoryStatus.Succeeded, OperationState.Canceled => OperationHistoryStatus.Cancelled, _ => OperationHistoryStatus.Failed },
                    StartedAt = old.StartedAt?.ToUniversalTime() ?? old.FinishedAt.ToUniversalTime(), FinishedAt = old.FinishedAt.ToUniversalTime(),
                    OutputFile = outputRelative
                });
            }
            Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { CoreLog.Warning(ex, "HistoryStore.MigrateLegacy"); }
    }

    private void TrimRetention()
    {
        if (_records.Count <= MaximumEntries) return;
        var discarded = _records.Skip(MaximumEntries).ToArray();
        _records.RemoveRange(MaximumEntries, _records.Count - MaximumEntries);
        foreach (OperationHistoryRecord record in discarded) DeleteOutput(record);
    }

    private void DeleteOutput(OperationHistoryRecord record)
    {
        try { string path = GetOutputPath(record); if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { CoreLog.Warning(ex, "HistoryStore.DeleteOutput"); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            string jsonl = string.Join(Environment.NewLine, _records.Select(record => JsonSerializer.Serialize(record, HistoryWriteContext.OperationHistoryRecord)));
            AtomicFile.WriteAllText(_filePath, jsonl.Length == 0 ? string.Empty : jsonl + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLog.Warning(ex, "HistoryStore.Save"); }
    }

    private void AppendLegacyTaskLog(OperationHistoryRecord record, string output)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_legacyTaskLogPath)!);
            OperationHistoryEntry legacy = ToLegacyEntry(record);
            string text = $"{legacy.KindLabel} · {SensitiveDataRedactor.Redact(legacy.AppName)} — {legacy.StateLabel}{Environment.NewLine}" +
                $"{legacy.StartLine}{Environment.NewLine}{legacy.EndLine}{Environment.NewLine}{legacy.DurationLine}{Environment.NewLine}{legacy.MethodLine}{Environment.NewLine}" +
                $"{Environment.NewLine}-- Saída da operação{Environment.NewLine}{output}{Environment.NewLine}{Environment.NewLine}" +
                "——————————————————————————————————————————" + Environment.NewLine + Environment.NewLine;
            File.AppendAllText(_legacyTaskLogPath, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { CoreLog.Warning(ex, "HistoryStore.LegacyTaskLog"); }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static HistoryWriteJsonContext CreateHistoryWriteContext() => new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    private OperationHistoryEntry ToLegacyEntry(OperationHistoryRecord record)
    {
        string output = ReadOutput(record);
        WingetMethod method = output.Contains("API do WinGet", StringComparison.OrdinalIgnoreCase) ? WingetMethod.ComApi
            : output.Contains("WinGet CLI", StringComparison.OrdinalIgnoreCase) ? WingetMethod.WingetExe
            : output.Contains("API da Store", StringComparison.OrdinalIgnoreCase) ? WingetMethod.OwnApi
            : WingetMethod.Unknown;
        return new OperationHistoryEntry(record.Id,
        record.PackageName ?? string.Empty, record.Type switch { OperationHistoryType.Update => OperationKind.Update, OperationHistoryType.Uninstall => OperationKind.Uninstall, _ => OperationKind.Install },
        record.Status switch { OperationHistoryStatus.Succeeded => OperationState.Completed, OperationHistoryStatus.Cancelled => OperationState.Canceled, _ => OperationState.Failed },
        record.FinishedAt, method, record.StartedAt, output);
    }
}

/// <summary>Compatibility name for existing Core consumers; new code should use HistoryStore.</summary>
public sealed class OperationHistoryService(string? filePath = null) : HistoryStore(filePath);
