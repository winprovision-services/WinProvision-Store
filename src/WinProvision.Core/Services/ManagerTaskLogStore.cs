using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>Arquivo JSONL compatível que acrescenta tarefas estruturadas sem remover eventos legados.</summary>
public sealed class ManagerTaskLogStore : IManagerTaskLogStore
{
    public const string FileName = "manager-tasks.jsonl";
    private const int MaximumRetainedTasks = 100;
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly ManagerTaskWriteJsonContext WriteContext = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });

    public ManagerTaskLogStore(IAppLog log, string? path = null)
    {
        _path = path ?? GetPath(log.LogDirectory);
    }

    public static string GetPath(string logDirectory) => Path.Combine(logDirectory, FileName);
    public string FilePath => _path;

    public async Task AppendAsync(ManagerTaskLogRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var stream = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            await writer.WriteLineAsync(JsonSerializer.Serialize(record, WriteContext.ManagerTaskLogRecord).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ManagerTaskLogRecord>> ReadAsync(string? manager = null,
        CancellationToken cancellationToken = default, int maximumTasks = MaximumRetainedTasks)
    {
        maximumTasks = Math.Clamp(maximumTasks, 1, 100_000);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return [];
            var tasks = new Queue<ManagerTaskLogRecord>(Math.Min(maximumTasks, MaximumRetainedTasks));
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream, Encoding.UTF8, true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    ManagerTaskLogRecord? record = JsonSerializer.Deserialize(line, ManagerTaskJsonContext.Default.ManagerTaskLogRecord);
                    if (record is null || manager is not null && !record.Manager.Equals(manager, StringComparison.OrdinalIgnoreCase)) continue;
                    tasks.Enqueue(record);
                    while (tasks.Count > maximumTasks) tasks.Dequeue();
                }
                catch (JsonException) { }
            }
            return tasks.ToArray();
        }
        finally { _gate.Release(); }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ManagerTaskLogRecord))]
internal partial class ManagerTaskJsonContext : JsonSerializerContext;

internal sealed class ManagerTaskWriteJsonContext(JsonSerializerOptions options) : ManagerTaskJsonContext(options);

