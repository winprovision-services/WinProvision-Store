using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public enum AppLogLevel
{
    Trace,
    Debug,
    Info,
    Warning,
    Error
}

public enum AppLogTaskKind
{
    Native,
    Subprocess
}

public sealed record AppLogException(string Type, string Message, string? StackTrace);

public sealed record AppLogEvent(
    int SchemaVersion,
    DateTimeOffset Timestamp,
    string Level,
    string Category,
    string Source,
    Guid? TaskId,
    string Event,
    string Message,
    JsonElement Data,
    AppLogException? Exception,
    string SessionId,
    int ProcessId);

public sealed record AppLogTaskDescriptor(
    string Category,
    string Source,
    string TaskType,
    string? ManagerName = null);

public interface IManagerTaskLogStore
{
    string FilePath { get; }
    Task AppendAsync(WinProvision.Core.Models.ManagerTaskLogRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WinProvision.Core.Models.ManagerTaskLogRecord>> ReadAsync(string? manager = null,
        CancellationToken cancellationToken = default, int maximumTasks = 100);
}

public static class PackageManagerLogCategories
{
    public const string Winget = "PackageManager.WinGet";
    public const string Chocolatey = "PackageManager.Chocolatey";

    public static string? ForSource(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        "winget" or "msstore" => Winget,
        "chocolatey" => Chocolatey,
        _ => null
    };
}

public sealed record AppLogOptions(
    AppLogLevel MinimumLevel = AppLogLevel.Info,
    int RetentionDays = 30,
    int MaximumTotalMegabytes = 100,
    long MaximumFileBytes = 10L * 1024 * 1024)
{
    public static AppLogOptions Default { get; } = new();
}

public interface ILogSink : IAsyncDisposable
{
    bool TryWrite(AppLogEvent logEvent);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task CleanupAsync(AppLogOptions options, CancellationToken cancellationToken = default);
}

public interface IAppLog
{
    string SessionId { get; }
    string LogDirectory { get; }
    AppLogLevel MinimumLevel { get; set; }
    string CurrentLogFilePath { get; }
    long DroppedEventCount { get; }

    void Write(AppLogLevel level, string category, string source, string message,
        Guid? taskId = null, string eventName = "Message", JsonElement? data = null, Exception? exception = null,
        IReadOnlyList<string>? knownSecrets = null);
    Task FlushAsync(CancellationToken cancellationToken = default);
    Task CleanupAsync(CancellationToken cancellationToken = default);
}

public sealed class AppLogService : IAppLog
{
    private static readonly JsonElement EmptyData = JsonDocument.Parse("{}").RootElement.Clone();
    private readonly ILogSink _sink;
    private readonly Func<AppLogOptions> _options;
    private readonly Guid _sessionId = Guid.NewGuid();
    private int _minimumLevel;

    public AppLogService(ILogSink sink, Func<AppLogOptions> options)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _minimumLevel = (int)_options().MinimumLevel;
    }

    public string SessionId => _sessionId.ToString("N");
    public string LogDirectory => AppLogPaths.GetLogDirectory();
    public AppLogLevel MinimumLevel
    {
        get => (AppLogLevel)Volatile.Read(ref _minimumLevel);
        set => Volatile.Write(ref _minimumLevel, (int)value);
    }
    public string CurrentLogFilePath => AppLogPaths.GetLogFilePath(DateTimeOffset.UtcNow);
    public long DroppedEventCount => _sink is JsonLinesFileLogSink fileSink ? fileSink.DroppedEventCount : 0;

    public void Write(AppLogLevel level, string category, string source, string message,
        Guid? taskId = null, string eventName = "Message", JsonElement? data = null, Exception? exception = null,
        IReadOnlyList<string>? knownSecrets = null)
    {
        if (level < MinimumLevel)
            return;

        try
        {
            string cleanMessage = SensitiveDataRedactor.Redact(message ?? string.Empty, knownSecrets);
            JsonElement cleanData = SensitiveDataRedactor.RedactJson(data ?? EmptyData, knownSecrets);
            AppLogException? cleanException = exception is null ? null : new(
                exception.GetType().FullName ?? exception.GetType().Name,
                SensitiveDataRedactor.Redact(exception.Message, knownSecrets),
                exception.StackTrace is { } stack ? SensitiveDataRedactor.Redact(stack, knownSecrets) : null);
            var logEvent = new AppLogEvent(
                SchemaVersion: 1,
                Timestamp: DateTimeOffset.UtcNow,
                Level: level.ToString(),
                Category: SensitiveDataRedactor.Redact(category, knownSecrets),
                Source: SensitiveDataRedactor.Redact(source, knownSecrets),
                TaskId: taskId,
                Event: SensitiveDataRedactor.Redact(eventName, knownSecrets),
                Message: cleanMessage,
                Data: cleanData,
                Exception: cleanException,
                SessionId: SessionId,
                ProcessId: Environment.ProcessId);
            _sink.TryWrite(logEvent);
        }
        catch
        {
            // Diagnostics must never alter the behavior of the operation being observed.
        }
    }

    public Task FlushAsync(CancellationToken cancellationToken = default) =>
        _sink.FlushAsync(cancellationToken);

    public Task CleanupAsync(CancellationToken cancellationToken = default) =>
        _sink.CleanupAsync(_options(), cancellationToken);
}

public static class AppLogPaths
{
    public const string ApplicationDirectoryName = "WinProvisionStore";
    public const string LogDirectoryName = "logs";
    public const string DiagnosticLogFilePattern = "app-*.jsonl";

    public static string GetLogDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ApplicationDirectoryName, LogDirectoryName);

    public static string GetLogFilePath(DateTimeOffset timestamp) =>
        Path.Combine(GetLogDirectory(), $"app-{timestamp.UtcDateTime:yyyy-MM-dd}.jsonl");
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(AppLogEvent))]
[JsonSerializable(typeof(AppLogException))]
public partial class AppLogJsonContext : JsonSerializerContext
{
}

