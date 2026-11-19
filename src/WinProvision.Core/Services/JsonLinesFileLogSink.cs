using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace WinProvision.Core.Services;

public sealed class JsonLinesFileLogSink : ILogSink
{
    private const int QueueCapacity = 2048;
    private sealed record QueueItem(AppLogEvent? Event, TaskCompletionSource? Barrier);
    private readonly string _directory;
    private readonly Func<AppLogOptions> _options;
    private readonly Channel<QueueItem> _queue;
    private readonly Task _writer;
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private long _droppedEventCount;
    private string? _currentPath;
    private int _currentSequence;
    private int _disposed;

    public JsonLinesFileLogSink(string directory, Func<AppLogOptions> options)
    {
        _directory = Path.GetFullPath(directory);
        _options = options;
        _queue = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _writer = Task.Run(WriteLoopAsync);
    }

    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    public bool TryWrite(AppLogEvent logEvent)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_queue.Writer.TryWrite(new QueueItem(logEvent, null)))
        {
            Interlocked.Increment(ref _droppedEventCount);
            return false;
        }
        return true;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            await _writer.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new QueueItem(null, barrier);
        if (!_queue.Writer.TryWrite(item))
        {
            await _queue.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }
        await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task CleanupAsync(AppLogOptions options, CancellationToken cancellationToken = default)
    {
        await FlushAsync(cancellationToken).ConfigureAwait(false);
        await _cleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await CleanupFilesAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Interlocked.Increment(ref _droppedEventCount);
        }
        finally
        {
            _cleanupGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _queue.Writer.TryComplete();
        try { await _writer.ConfigureAwait(false); }
        catch (Exception) { Interlocked.Increment(ref _droppedEventCount); }
    }

    private async Task WriteLoopAsync()
    {
        await foreach (QueueItem item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (item.Barrier is { } barrier)
            {
                barrier.TrySetResult();
                continue;
            }

            if (item.Event is not { } logEvent)
                continue;

            try
            {
                await WriteEventAsync(logEvent).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                Interlocked.Increment(ref _droppedEventCount);
            }
            catch (Exception)
            {
                Interlocked.Increment(ref _droppedEventCount);
            }
        }
    }

    private async Task WriteEventAsync(AppLogEvent logEvent)
    {
        Directory.CreateDirectory(_directory);
        string path = GetNextPath(logEvent.Timestamp, Math.Max(1024, _options().MaximumFileBytes));
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(logEvent, AppLogJsonContext.Default.AppLogEvent) + "\n");
        await using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await stream.WriteAsync(bytes).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }

        if (DroppedEventCount > 0)
        {
            // The count remains observable for the settings card instead of recursively logging queue pressure.
        }
        if (Random.Shared.Next(64) == 0)
        {
            await _cleanupGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await CleanupFilesAsync(_options(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            finally
            {
                _cleanupGate.Release();
            }
        }
    }

    private string GetNextPath(DateTimeOffset timestamp, long maximumFileBytes)
    {
        string day = timestamp.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        string stem = $"app-{day}";
        if (_currentPath is not null && Path.GetFileName(_currentPath).StartsWith(stem, StringComparison.Ordinal))
        {
            if (!File.Exists(_currentPath) || new FileInfo(_currentPath).Length < maximumFileBytes)
                return _currentPath;
        }

        int sequence = _currentPath is not null && Path.GetFileName(_currentPath).StartsWith(stem, StringComparison.Ordinal)
            ? _currentSequence + 1
            : 0;
        string candidate;
        do
        {
            candidate = Path.Combine(_directory, sequence == 0
                ? $"{stem}.jsonl"
                : $"{stem}-{sequence:D3}.jsonl");
            if (!File.Exists(candidate) || new FileInfo(candidate).Length < maximumFileBytes)
                break;
            sequence++;
        } while (true);

        _currentPath = candidate;
        _currentSequence = sequence;
        return candidate;
    }

    private async Task CleanupFilesAsync(AppLogOptions options, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
            return;

        DateTime cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(options.RetentionDays, 1, 3650));
        FileInfo[] files = new DirectoryInfo(_directory).GetFiles(AppLogPaths.DiagnosticLogFilePattern)
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        foreach (FileInfo file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.LastWriteTimeUtc < cutoff)
                TryDelete(file.FullName);
        }

        long maximumBytes = Math.Clamp((long)options.MaximumTotalMegabytes, 1, 10_240) * 1024 * 1024;
        files = new DirectoryInfo(_directory).GetFiles(AppLogPaths.DiagnosticLogFilePattern)
            .OrderBy(file => file.LastWriteTimeUtc).ToArray();
        long total = files.Sum(file => file.Length);
        foreach (FileInfo file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total <= maximumBytes)
                break;
            long length = file.Length;
            if (TryDelete(file.FullName))
                total -= length;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

}

public static class AppLogJsonLinesReader
{
    public static async IAsyncEnumerable<AppLogEvent> ReadForDisplayAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) yield break;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppLogEvent? entry = null;
            try { entry = JsonSerializer.Deserialize(line, AppLogJsonContext.Default.AppLogEvent); }
            catch (JsonException) { }
            yield return entry ?? CreateUnparsedLineEvent(line);
        }
    }

    public static async IAsyncEnumerable<AppLogEvent> ReadAsync(
        string path,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            yield break;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppLogEvent? logEvent;
            try
            {
                logEvent = JsonSerializer.Deserialize(line, AppLogJsonContext.Default.AppLogEvent);
            }
            catch (JsonException)
            {
                // A damaged line does not make the remaining JSONL events unreadable.
                continue;
            }

            if (logEvent is not null)
                yield return logEvent;
        }
    }

    private static AppLogEvent CreateUnparsedLineEvent(string line) => new(
        SchemaVersion: 0,
        Timestamp: DateTimeOffset.MinValue,
        Level: "Debug",
        Category: "Unparsed",
        Source: string.Empty,
        TaskId: null,
        Event: "UnparsedLine",
        Message: line,
        Data: JsonDocument.Parse("{}").RootElement.Clone(),
        Exception: null,
        SessionId: string.Empty,
        ProcessId: 0);
}
