using System.Text.Json;
using System.IO.Compression;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class AppLogTests
{
    [Fact]
    public async Task ReaderSkipsCorruptLinesAndIgnoresUnknownFieldsAndFutureSchema()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "app-2026-01-01.jsonl");
        try
        {
            AppLogEvent item = CreateEvent();
            await File.WriteAllLinesAsync(path,
            new[]
            {
                JsonSerializer.Serialize(item, AppLogJsonContext.Default.AppLogEvent),
                "{invalid json",
                """{"schemaVersion":999,"timestamp":"2026-01-01T00:00:00Z","level":"Info","category":"App","source":"test","taskId":null,"event":"Message","message":"future schema","data":{},"exception":null,"sessionId":"s","processId":1,"newField":"ignored"}"""
            });

            List<AppLogEvent> read = [];
            await foreach (AppLogEvent logEvent in AppLogJsonLinesReader.ReadAsync(path))
                read.Add(logEvent);

            Assert.Equal(2, read.Count);
            Assert.Equal(999, read[1].SchemaVersion);
            Assert.Equal("future schema", read[1].Message);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void CorrelatorPairsTaskEventsAndMarksPriorSessionWithoutFinishInterrupted()
    {
        AppLogEvent started = CreateEvent(taskId: Guid.NewGuid(), eventName: "Started", sessionId: "old");
        AppLogEvent finished = CreateEvent(taskId: started.TaskId, eventName: "Finished", sessionId: "old");
        AppLogEvent interrupted = CreateEvent(taskId: Guid.NewGuid(), eventName: "Started", sessionId: "old");

        IReadOnlyList<AppLogTaskView> tasks = AppLogTaskCorrelator.Correlate(
            new[] { started, finished, interrupted }, activeSessionId: "current");

        Assert.Equal(2, tasks.Count);
        Assert.False(tasks[0].IsInterrupted);
        Assert.NotNull(tasks[0].Finished);
        Assert.True(tasks[1].IsInterrupted);
        Assert.Null(tasks[1].Finished);
    }

    [Fact]
    public void RedactorRemovesSentinelsFromMessagesArgumentsExceptionsAndCapturedOutput()
    {
        const string token = "connection-secret-sentinel";
        const string bearer = "bearer-secret-sentinel";
        const string code = "pairing-code-sentinel";
        IReadOnlyList<string> safeArguments = SensitiveDataRedactor.RedactArguments(
            new[] { "--password", token, "--connection-code=" + code, "--header", "Authorization: Bearer " + bearer },
            out IReadOnlyList<string> secrets);
        string safeArgumentText = string.Join(' ', safeArguments);
        Assert.DoesNotContain(token, safeArgumentText, StringComparison.Ordinal);
        Assert.DoesNotContain(code, safeArgumentText, StringComparison.Ordinal);
        Assert.DoesNotContain(bearer, safeArgumentText, StringComparison.Ordinal);
        using JsonDocument data = JsonDocument.Parse(
            $"{{\"message\":\"password={token}; Authorization: Bearer {bearer}; connection code: {code}\",\"output\":\"{SensitiveDataRedactor.Redact($"stdout {token} {bearer} {code}", secrets)}\"}}");
        var sink = new RecordingLogSink();
        var log = new AppLogService(sink, () => AppLogOptions.Default);
        log.Write(AppLogLevel.Error, "Manager", "test", $"password={token}; token={bearer}; connection code: {code}",
            data: data.RootElement, exception: new InvalidOperationException($"failed with {token} {bearer} {code}"),
            knownSecrets: secrets);

        AppLogEvent recorded = Assert.Single(sink.Events);
        string serialized = JsonSerializer.Serialize(recorded, AppLogJsonContext.Default.AppLogEvent);
        Assert.DoesNotContain(token, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(bearer, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(code, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingNameInSecurityDataIsNotTreatedAsASecretField()
    {
        using JsonDocument data = JsonDocument.Parse("""{"settingName":"allowElevation"}""");
        var sink = new RecordingLogSink();
        var log = new AppLogService(sink, () => AppLogOptions.Default);

        log.Write(AppLogLevel.Info, "Security", "test", "Preference changed", data: data.RootElement);

        AppLogEvent recorded = Assert.Single(sink.Events);
        Assert.Equal("allowElevation", recorded.Data.GetProperty("settingName").GetString());
    }

    [Fact]
    public async Task NativeTasksUseSameShapeRegardlessOfManagerName()
    {
        var sink = new RecordingLogSink();
        var executor = new AppLogTaskExecutor(new AppLogService(sink, () => AppLogOptions.Default));
        var firstTask = new AppLogTaskDescriptor("Manager", "test", "Install", "InventedManagerA");
        var secondTask = firstTask with { ManagerName = "InventedManagerB" };

        await executor.RunNativeAsync(firstTask, _ => Task.FromResult("done"));
        await executor.RunNativeAsync(secondTask, _ => Task.FromResult("done"));

        Assert.Equal(4, sink.Events.Count);
        foreach (IGrouping<Guid?, AppLogEvent> taskEvents in sink.Events.GroupBy(item => item.TaskId))
        {
            Assert.Equal(2, taskEvents.Count());
            Assert.Contains(taskEvents, item => item.Event == "Started");
            Assert.Contains(taskEvents, item => item.Event == "Finished");
            foreach (AppLogEvent item in taskEvents)
            {
                Assert.Equal("Manager", item.Category);
                Assert.Equal("Native", item.Data.GetProperty("taskKind").GetString());
                Assert.Equal("Install", item.Data.GetProperty("taskType").GetString());
            }
        }
    }

    [Fact]
    public async Task ProcessTaskRedactsSensitiveArgumentsAndCapturedOutput()
    {
        const string sentinel = "process-secret-sentinel";
        var sink = new RecordingLogSink();
        var executor = new AppLogTaskExecutor(new AppLogService(sink, () => AppLogOptions.Default));
        string commandProcessor = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var startInfo = new System.Diagnostics.ProcessStartInfo(commandProcessor);
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("echo");
        startInfo.ArgumentList.Add("--password");
        startInfo.ArgumentList.Add(sentinel);

        AppLogProcessResult result = await executor.RunProcessAsync(
            new AppLogTaskDescriptor("Manager", "test", "Query", "InventedManager"),
            startInfo);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(sentinel, result.StandardOutput, StringComparison.Ordinal);
        string serialized = string.Join(Environment.NewLine, sink.Events.Select(
            item => JsonSerializer.Serialize(item, AppLogJsonContext.Default.AppLogEvent)));
        Assert.DoesNotContain(sentinel, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsStorePersistsValidatedOptions()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "settings.json");
        try
        {
            var store = new AppLogSettingsStore(path);
            store.Save(new AppLogSettings(AppLogLevel.Warning, 60, 250));
            var reloaded = new AppLogSettingsStore(path);

            Assert.Equal(AppLogLevel.Warning, reloaded.Current.MinimumLevel);
            Assert.Equal(60, reloaded.Current.RetentionDays);
            Assert.Equal(250, reloaded.Current.MaximumTotalMegabytes);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ZipExportRedactsSensitiveFields()
    {
        string directory = CreateTempDirectory();
        string outputPath = Path.Combine(directory, "logs.zip");
        string logPath = Path.Combine(directory, "app-2026-01-01.jsonl");
        const string sentinel = "zip-secret-sentinel";
        try
        {
            using JsonDocument data = JsonDocument.Parse($"{{\"password\":\"{sentinel}\",\"message\":\"ok\"}}");
            AppLogEvent item = CreateEvent(message: "export", sessionId: "s") with { Data = data.RootElement.Clone() };
            await File.WriteAllTextAsync(logPath, JsonSerializer.Serialize(item, AppLogJsonContext.Default.AppLogEvent));
            await AppLogExportService.ExportRedactedZipAsync(directory, outputPath);

            using ZipArchive archive = ZipFile.OpenRead(outputPath);
            using StreamReader reader = new(Assert.Single(archive.Entries).Open());
            string exported = await reader.ReadToEndAsync();
            Assert.DoesNotContain(sentinel, exported, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", exported, StringComparison.Ordinal);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SinkWritesConcurrentEventsAndRotatesFiles()
    {
        string directory = CreateTempDirectory();
        try
        {
            var sink = new JsonLinesFileLogSink(directory,
                () => AppLogOptions.Default with { MaximumFileBytes = 1024 });
            await Task.WhenAll(Enumerable.Range(0, 4).Select(async producer =>
            {
                for (int index = 0; index < 40; index++)
                {
                    Assert.True(sink.TryWrite(CreateEvent(message: $"{producer}:{index} " + new string('x', 80))));
                    await Task.Yield();
                }
            }));
            await sink.FlushAsync();

            string[] files = Directory.GetFiles(directory, "app-*.jsonl");
            int count = 0;
            foreach (string file in files)
                await foreach (AppLogEvent _ in AppLogJsonLinesReader.ReadAsync(file))
                    count++;

            Assert.Equal(160, count);
            Assert.True(files.Length > 1);
            await sink.DisposeAsync();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task LockedLogFileDoesNotThrowToWriter()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = AppLogPaths.GetLogFilePath(DateTimeOffset.UtcNow);
            path = Path.Combine(directory, Path.GetFileName(path));
            await File.WriteAllTextAsync(path, string.Empty);
            using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var sink = new JsonLinesFileLogSink(directory, () => AppLogOptions.Default);

            Assert.True(sink.TryWrite(CreateEvent()));
            await sink.FlushAsync();
            Assert.Equal(1, sink.DroppedEventCount);
            await sink.DisposeAsync();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RetentionRemovesOldFilesAndRespectsTotalLimit()
    {
        string directory = CreateTempDirectory();
        try
        {
            string oldPath = Path.Combine(directory, "app-2020-01-01.jsonl");
            string firstPath = Path.Combine(directory, "app-2026-01-01.jsonl");
            string secondPath = Path.Combine(directory, "app-2026-01-02.jsonl");
            await File.WriteAllBytesAsync(oldPath, new byte[32]);
            await File.WriteAllBytesAsync(firstPath, new byte[600 * 1024]);
            await File.WriteAllBytesAsync(secondPath, new byte[600 * 1024]);
            File.SetLastWriteTimeUtc(firstPath, DateTime.UtcNow.AddMinutes(-2));
            File.SetLastWriteTimeUtc(secondPath, DateTime.UtcNow.AddMinutes(-1));
            File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-40));
            var sink = new JsonLinesFileLogSink(directory, () => AppLogOptions.Default);

            await sink.CleanupAsync(AppLogOptions.Default with { RetentionDays = 30, MaximumTotalMegabytes = 1 });

            Assert.False(File.Exists(oldPath));
            Assert.True(Directory.GetFiles(directory, "app-*.jsonl").Sum(file => new FileInfo(file).Length) <= 1024 * 1024);
            await sink.DisposeAsync();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinProvision-AppLog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static AppLogEvent CreateEvent(Guid? taskId = null, string eventName = "Message",
        string sessionId = "session", string message = "test") =>
        new(1, DateTimeOffset.UtcNow, "Info", "App", "test", taskId, eventName, message,
            JsonDocument.Parse("{}").RootElement.Clone(), null, sessionId, Environment.ProcessId);

    private sealed class RecordingLogSink : ILogSink
    {
        public List<AppLogEvent> Events { get; } = [];
        public bool TryWrite(AppLogEvent logEvent) { Events.Add(logEvent); return true; }
        public Task FlushAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CleanupAsync(AppLogOptions options, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
