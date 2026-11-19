namespace WinProvision.Core.Models;

/// <summary>Representação persistida de uma chamada a um gerenciador de pacotes.</summary>
public sealed record ManagerTaskLogRecord
{
    public int SchemaVersion { get; init; } = 1;
    public Guid TaskId { get; init; }
    public string Manager { get; init; } = string.Empty;
    public string TaskType { get; init; } = "Outra tarefa";
    public string Kind { get; init; } = "Subprocess";
    public string? Executable { get; init; }
    public string? Arguments { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public string TaskInformation { get; init; } = string.Empty;
    public int? ExitCode { get; init; }
    public string Result { get; init; } = "warning";
}
