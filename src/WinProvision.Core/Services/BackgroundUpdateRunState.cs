using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public sealed record BackgroundUpdateRunState(DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc = null,
    int? ExitCode = null, int Available = 0, string Fingerprint = "");

public static class BackgroundUpdateRunStore
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStoreWinUI3", "background-update-state.json");

    public static BackgroundUpdateRunState? Read(string? path = null)
    {
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(path ?? DefaultPath), BackgroundUpdateStateJsonContext.Default.BackgroundUpdateRunState);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public static void Write(BackgroundUpdateRunState state, string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(state, BackgroundUpdateStateJsonContext.Default.BackgroundUpdateRunState));
    }
}

[JsonSerializable(typeof(BackgroundUpdateRunState))]
internal partial class BackgroundUpdateStateJsonContext : JsonSerializerContext;
