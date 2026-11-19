using System.Text.Json.Serialization;

namespace WinProvision.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<OperationHistoryType>))]
public enum OperationHistoryType
{
    [JsonStringEnumMemberName("install")] Install,
    [JsonStringEnumMemberName("uninstall")] Uninstall,
    [JsonStringEnumMemberName("update")] Update,
    [JsonStringEnumMemberName("download")] Download
}

[JsonConverter(typeof(JsonStringEnumConverter<OperationHistoryStatus>))]
public enum OperationHistoryStatus
{
    [JsonStringEnumMemberName("succeeded")] Succeeded,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled
}

public static class OperationHistoryEnumExtensions
{
    public static string ToValue(this OperationHistoryType value) => value switch
    { OperationHistoryType.Install => "install", OperationHistoryType.Uninstall => "uninstall", OperationHistoryType.Update => "update", _ => "download" };
    public static string ToValue(this OperationHistoryStatus value) => value switch
    { OperationHistoryStatus.Succeeded => "succeeded", OperationHistoryStatus.Failed => "failed", _ => "cancelled" };
}

/// <summary>Registro persistido de uma operação; a saída completa fica no arquivo apontado por OutputFile.</summary>
public sealed record OperationHistoryRecord
{
    public int SchemaVersion { get; init; } = 1;
    public OperationSnapshot? Snapshot { get; init; }
    public Guid Id { get; init; }
    public OperationHistoryType Type { get; init; } = OperationHistoryType.Install;
    public string? PackageName { get; init; }
    public string? PackageId { get; init; }
    public string? Manager { get; init; }
    public string? SourceName { get; init; }
    public string? VersionFrom { get; init; }
    public string? VersionTo { get; init; }
    public OperationHistoryStatus Status { get; init; } = OperationHistoryStatus.Failed;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public int? ExitCode { get; init; }
    public string OutputFile { get; init; } = string.Empty;
}
