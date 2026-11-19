using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinProvision.Core.Services;

public sealed record AppLogProcessResult(
    Guid TaskId,
    int ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    TimeSpan Duration,
    string StandardOutput,
    bool StandardOutputTruncated,
    long StandardOutputLength,
    string StandardError,
    bool StandardErrorTruncated,
    long StandardErrorLength);

public sealed class AppLogTaskExecutor
{
    private const int MaximumCapturedCharacters = 64 * 1024;
    private readonly IAppLog _log;
    private readonly IManagerTaskLogStore? _managerTasks;

    public AppLogTaskExecutor(IAppLog log, IManagerTaskLogStore? managerTasks = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _managerTasks = managerTasks;
    }

    public async Task<AppLogProcessResult> RunProcessAsync(
        AppLogTaskDescriptor task,
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(startInfo);

        Guid taskId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Stopwatch stopwatch = Stopwatch.StartNew();
        string executable = startInfo.FileName;
        string[] arguments = startInfo.ArgumentList.ToArray();
        bool hasUnstructuredArguments = !string.IsNullOrWhiteSpace(startInfo.Arguments);
        IReadOnlyList<string> secrets = Array.Empty<string>();
        IReadOnlyList<string> safeArguments = hasUnstructuredArguments
            ? ["[REDACTED: argumentos não estruturados]"]
            : SensitiveDataRedactor.RedactArguments(arguments, out secrets);
        var process = new Process { StartInfo = startInfo };
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;

        _log.Write(AppLogLevel.Info, task.Category, task.Source, "Tarefa iniciada.", taskId, "Started",
            CreateData(
                ("taskKind", AppLogTaskKind.Subprocess),
                ("taskType", task.TaskType),
                ("manager", task.ManagerName),
                ("executable", executable),
                ("arguments", safeArguments),
                ("startedAt", startedAt)));

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("O processo não foi iniciado.");

            Task<CapturedOutput> stdoutTask = CaptureAsync(process.StandardOutput, MaximumCapturedCharacters, cancellationToken);
            Task<CapturedOutput> stderrTask = CaptureAsync(process.StandardError, MaximumCapturedCharacters, cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            CapturedOutput stdout = await stdoutTask.ConfigureAwait(false);
            CapturedOutput stderr = await stderrTask.ConfigureAwait(false);
            stopwatch.Stop();
            DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
            int exitCode = process.ExitCode;

            string cleanStdout = hasUnstructuredArguments
                ? "[REDACTED: saída omitida por argumentos não estruturados]"
                : SensitiveDataRedactor.Redact(stdout.Text, secrets);
            string cleanStderr = hasUnstructuredArguments
                ? "[REDACTED: saída omitida por argumentos não estruturados]"
                : SensitiveDataRedactor.Redact(stderr.Text, secrets);
            _log.Write(exitCode == 0 ? AppLogLevel.Info : AppLogLevel.Error, task.Category, task.Source,
                exitCode == 0 ? "Tarefa concluída." : "Tarefa falhou.", taskId, "Finished",
                CreateData(
                    ("taskKind", AppLogTaskKind.Subprocess),
                    ("taskType", task.TaskType),
                    ("manager", task.ManagerName),
                    ("executable", executable),
                    ("arguments", safeArguments),
                    ("startedAt", startedAt),
                    ("finishedAt", finishedAt),
                    ("durationMilliseconds", stopwatch.Elapsed.TotalMilliseconds),
                    ("exitCode", exitCode),
                    ("standardOutput", cleanStdout),
                    ("standardOutputTruncated", stdout.Truncated),
                    ("standardOutputLength", stdout.Length),
                    ("standardError", cleanStderr),
                    ("standardErrorTruncated", stderr.Truncated),
                    ("standardErrorLength", stderr.Length)), knownSecrets: secrets);

            if (_managerTasks is not null)
                await TryAppendManagerTaskAsync(new WinProvision.Core.Models.ManagerTaskLogRecord
                {
                    TaskId = taskId,
                    Manager = task.ManagerName ?? task.Source,
                    TaskType = LocalizeTaskType(task.TaskType),
                    Kind = "Subprocess",
                    Executable = SensitiveDataRedactor.Redact(executable),
                    Arguments = CommandLineDisplayFormatter.FormatPowerShellArguments(safeArguments),
                    StartedAt = startedAt,
                    FinishedAt = finishedAt,
                    StandardOutput = ClipPersistedOutput(cleanStdout),
                    StandardError = ClipPersistedOutput(cleanStderr),
                    ExitCode = exitCode,
                    Result = exitCode == 0 ? "success" : "error"
                }, cancellationToken).ConfigureAwait(false);

            return new AppLogProcessResult(taskId, exitCode, startedAt, finishedAt, stopwatch.Elapsed,
                stdout.Text, stdout.Truncated, stdout.Length, stderr.Text, stderr.Truncated, stderr.Length);
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            _log.Write(AppLogLevel.Error, task.Category, task.Source, "A tarefa foi interrompida antes da conclusão.",
                taskId, "Finished",
                CreateData(
                    ("taskKind", AppLogTaskKind.Subprocess),
                    ("taskType", task.TaskType),
                    ("manager", task.ManagerName),
                    ("executable", executable),
                    ("arguments", safeArguments),
                    ("startedAt", startedAt),
                    ("finishedAt", DateTimeOffset.UtcNow),
                    ("durationMilliseconds", stopwatch.Elapsed.TotalMilliseconds),
                    ("exitCode", TryGetExitCode(process)),
                    ("standardError", string.Empty),
                    ("standardErrorTruncated", false),
                    ("standardErrorLength", 0)), exception, secrets);
            if (_managerTasks is not null)
                await TryAppendManagerTaskAsync(new WinProvision.Core.Models.ManagerTaskLogRecord
                {
                    TaskId = taskId, Manager = task.ManagerName ?? task.Source,
                    TaskType = LocalizeTaskType(task.TaskType), Kind = "Subprocess",
                    Executable = SensitiveDataRedactor.Redact(executable), Arguments = CommandLineDisplayFormatter.FormatPowerShellArguments(safeArguments),
                    StartedAt = startedAt, FinishedAt = DateTimeOffset.UtcNow,
                    StandardError = SensitiveDataRedactor.Redact(exception.Message, secrets),
                    ExitCode = TryGetExitCode(process), Result = "error"
                }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            process.Dispose();
        }
    }

    private static string LocalizeTaskType(string type) => type switch
    {
        "UpdateSources" or "UpdateIndexes" => "Atualizar índices",
        "ListUpdates" => "Listar atualizações",
        "ListSources" => "Listar fontes",
        "ListInstalled" or "ListInstalledPackages" => "Listar instalados",
        "Search" => "Buscar",
        "Install" or "Instalar" => "Instalar",
        "Update" or "Atualizar" => "Atualizar",
        "Uninstall" or "Desinstalar" => "Desinstalar",
        "RefreshIndexes" => "Atualizar índices",
        "Outra tarefa" => "Outra tarefa",
        _ => "Outra tarefa"
    };

    private static string ClipPersistedOutput(string value)
    {
        const int maximumCharacters = 50 * 1024;
        return value.Length <= maximumCharacters
            ? value
            : value[..maximumCharacters] + Environment.NewLine + "[saída truncada — excedeu 50 KB]";
    }

    private async Task TryAppendManagerTaskAsync(WinProvision.Core.Models.ManagerTaskLogRecord record,
        CancellationToken cancellationToken)
    {
        try { if (_managerTasks is not null) await _managerTasks.AppendAsync(record, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
        { CoreLog.Warning(exception, "AppLogTaskExecutor.PersistManagerTask"); }
    }

    public async Task<TResult> RunNativeAsync<TResult>(
        AppLogTaskDescriptor task,
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(operation);

        Guid taskId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Stopwatch stopwatch = Stopwatch.StartNew();
        _log.Write(AppLogLevel.Info, task.Category, task.Source, "Tarefa iniciada.", taskId, "Started",
            CreateData(
                ("taskKind", AppLogTaskKind.Native),
                ("taskType", task.TaskType),
                ("manager", task.ManagerName),
                ("startedAt", startedAt)));

        try
        {
            TResult result = await operation(cancellationToken).ConfigureAwait(false);
            stopwatch.Stop();
            DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
            string taskInformation = SensitiveDataRedactor.Redact(result is System.Collections.ICollection collection
                ? $"Itens retornados: {collection.Count}"
                : result?.ToString() ?? string.Empty);
            _log.Write(AppLogLevel.Info, task.Category, task.Source, "Tarefa concluída.", taskId, "Finished",
                CreateData(
                    ("taskKind", AppLogTaskKind.Native),
                    ("taskType", task.TaskType),
                    ("manager", task.ManagerName),
                    ("startedAt", startedAt),
                    ("finishedAt", finishedAt),
                    ("durationMilliseconds", stopwatch.Elapsed.TotalMilliseconds),
                    ("result", result is null ? null : result.ToString())));
            if (_managerTasks is not null)
                await TryAppendManagerTaskAsync(new WinProvision.Core.Models.ManagerTaskLogRecord
                {
                    TaskId = taskId, Manager = task.ManagerName ?? task.Source,
                    TaskType = LocalizeTaskType(task.TaskType), Kind = "Native",
                    StartedAt = startedAt, FinishedAt = finishedAt,
                    TaskInformation = ClipPersistedOutput(taskInformation), ExitCode = 0, Result = "success"
                }, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            DateTimeOffset finishedAt = DateTimeOffset.UtcNow;
            _log.Write(AppLogLevel.Error, task.Category, task.Source, "A tarefa falhou.", taskId, "Finished",
                CreateData(
                    ("taskKind", AppLogTaskKind.Native),
                    ("taskType", task.TaskType),
                    ("manager", task.ManagerName),
                    ("startedAt", startedAt),
                    ("finishedAt", finishedAt),
                    ("durationMilliseconds", stopwatch.Elapsed.TotalMilliseconds)), exception);
            if (_managerTasks is not null)
                await TryAppendManagerTaskAsync(new WinProvision.Core.Models.ManagerTaskLogRecord
                {
                    TaskId = taskId, Manager = task.ManagerName ?? task.Source,
                    TaskType = LocalizeTaskType(task.TaskType), Kind = "Native",
                    StartedAt = startedAt, FinishedAt = finishedAt,
                    StandardError = SensitiveDataRedactor.Redact(exception.Message),
                    TaskInformation = ClipPersistedOutput(SensitiveDataRedactor.Redact(exception.ToString())),
                    Result = "error"
                }, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task RecordExternalProcessResultAsync(AppLogTaskDescriptor task, string executable, string arguments,
        DateTimeOffset startedAt, DateTimeOffset finishedAt, int exitCode, string output,
        CancellationToken cancellationToken = default) => await RecordExternalProcessResultAsync(task, executable,
            arguments, startedAt, finishedAt, exitCode, output, string.Empty, cancellationToken).ConfigureAwait(false);

    public async Task RecordExternalProcessResultAsync(AppLogTaskDescriptor task, string executable, string arguments,
        DateTimeOffset startedAt, DateTimeOffset finishedAt, int exitCode, string standardOutput, string standardError,
        CancellationToken cancellationToken = default)
    {
        if (_managerTasks is null) return;
        string cleanOutput = SensitiveDataRedactor.Redact(standardOutput);
        string cleanError = SensitiveDataRedactor.Redact(standardError);
        await TryAppendManagerTaskAsync(new WinProvision.Core.Models.ManagerTaskLogRecord
        {
            TaskId = Guid.NewGuid(), Manager = task.ManagerName ?? task.Source,
            TaskType = LocalizeTaskType(task.TaskType), Kind = "Subprocess",
            Executable = SensitiveDataRedactor.Redact(executable), Arguments = SensitiveDataRedactor.Redact(arguments),
            StartedAt = startedAt, FinishedAt = finishedAt,
            StandardOutput = ClipPersistedOutput(cleanOutput), StandardError = ClipPersistedOutput(cleanError), ExitCode = exitCode,
            Result = exitCode == 0 || exitCode is 3010 or 1641
                || task.TaskType.Equals("Desinstalar", StringComparison.OrdinalIgnoreCase) && exitCode is 1605 or 1614
                ? "success" : "error"
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<CapturedOutput> CaptureAsync(
        StreamReader reader,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(Math.Min(maximumCharacters, 4096));
        char[] buffer = new char[4096];
        long length = 0;
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;

            int remaining = Math.Max(0, maximumCharacters - output.Length);
            if (remaining > 0)
                output.Append(buffer, 0, Math.Min(read, remaining));
            length += read;
        }

        return new CapturedOutput(output.ToString(), length > output.Length, length);
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static JsonElement CreateData(params (string Name, object? Value)[] properties)
    {
        var data = new JsonObject();
        foreach ((string name, object? value) in properties)
            data[name] = ToNode(value);

        using JsonDocument document = JsonDocument.Parse(data.ToJsonString());
        return document.RootElement.Clone();
    }

    private static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string text => JsonValue.Create(text),
        AppLogTaskKind kind => JsonValue.Create(kind.ToString()),
        DateTimeOffset timestamp => JsonValue.Create(timestamp),
        bool boolean => JsonValue.Create(boolean),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        double number => JsonValue.Create(number),
        IEnumerable<string> values => new JsonArray(values.Select(item => (JsonNode?)JsonValue.Create(item)).ToArray()),
        _ => JsonValue.Create(value.ToString())
    };

    private sealed record CapturedOutput(string Text, bool Truncated, long Length);
}
