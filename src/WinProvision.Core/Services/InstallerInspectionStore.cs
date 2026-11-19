using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public sealed record StoredInstallerInspection(string Sha256, string InspectorVersion, DateTimeOffset InspectedAt,
    InstallerElevationInspection Result);

internal sealed record InstallerElevationLogData(string Id, string Version, string? InstallerId,
    string Source, string Decision, string Reason, InstallerInspectionSignal[] Signals, bool FromStorage,
    bool Prohibited, bool WillElevate, bool CallerRequestedElevation);

public interface IInstallerInspectionStore
{
    Task<StoredInstallerInspection?> ReadAsync(string sha256, string inspectorVersion, CancellationToken token);
    Task WriteAsync(StoredInstallerInspection entry, CancellationToken token);
}

/// <summary>Disposable local evidence, not learned installation outcomes or catalog metadata.</summary>
public sealed class InstallerInspectionStore(string directory) : IInstallerInspectionStore
{
    public const int MaximumEntryBytes = 64 * 1024;

    private string EntryPath(string sha256, string version)
    {
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit)
            || version.Length is < 1 or > 64 || !version.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
            || version is "." or "..") throw new InvalidDataException("Chave de inspeção inválida.");
        return Path.Combine(directory, version, sha256.ToLowerInvariant() + ".json");
    }

    public async Task<StoredInstallerInspection?> ReadAsync(string sha256, string inspectorVersion, CancellationToken token)
    {
        try
        {
            await using var stream = new FileStream(EntryPath(sha256, inspectorVersion), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (stream.Length > MaximumEntryBytes) return null;
            // Bounded read even if another writer grows/replaces the entry.
            byte[] data = new byte[MaximumEntryBytes + 1];
            int length = 0, read;
            while (length < data.Length && (read = await stream.ReadAsync(data.AsMemory(length), token).ConfigureAwait(false)) != 0)
                length += read;
            if (length > MaximumEntryBytes) return null;
            var entry = JsonSerializer.Deserialize(data.AsSpan(0, length), InstallerInspectionJsonContext.Default.StoredInstallerInspection);
            return entry is not null && string.Equals(entry.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
                && entry.InspectorVersion == inspectorVersion && entry.InspectedAt != default
                && IsValid(entry.Result) ? entry : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or ArgumentException or NotSupportedException) { return null; }
    }

    internal static bool IsValid(InstallerElevationInspection? result) => result is not null && result.Complete
        && result.Decision is "required" or "not-required" or "unknown"
        && !string.IsNullOrWhiteSpace(result.Reason) && result.Reason.Length <= 128
        && result.Signals is { Length: <= 256 } && result.Signals.All(s => s is not null
            && s.Name is { Length: > 0 and <= 128 } && s.Value is { Length: <= 128 });

    public async Task WriteAsync(StoredInstallerInspection entry, CancellationToken token)
    {
        string? temporary = null;
        try
        {
            if (!IsValid(entry.Result)) return;
            string path = EntryPath(entry.Sha256, entry.InspectorVersion);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry with { Result = entry.Result with { FromStorage = false } },
                InstallerInspectionJsonContext.Default.StoredInstallerInspection);
            if (bytes.Length > MaximumEntryBytes) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or ArgumentException or NotSupportedException) { }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

public sealed class CachedInstallerElevationInspector(IInstallerElevationInspector inspector, IInstallerInspectionStore store)
{
    public static readonly TimeSpan StorageTimeout = TimeSpan.FromMilliseconds(250);

    public async Task<InstallerElevationInspection> InspectAsync(string path, string sha256, CancellationToken token,
        string? installerType = null)
    {
        token.ThrowIfCancellationRequested();
        string version = inspector.Version;
        StoredInstallerInspection? entry = await BestEffortAsync(ct => store.ReadAsync(sha256, version, ct), token).ConfigureAwait(false);
        if (entry is not null && string.Equals(entry.Sha256, sha256, StringComparison.OrdinalIgnoreCase)
            && entry.InspectorVersion == version && InstallerInspectionStore.IsValid(entry.Result))
            return InstallerElevationInspector.ForInstallerType(entry.Result with { FromStorage = true }, installerType);

        InstallerElevationInspection result = await inspector.InspectAsync(path, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (InstallerInspectionStore.IsValid(result))
            await BestEffortAsync(async ct =>
            {
                await store.WriteAsync(new(sha256.ToLowerInvariant(), version, DateTimeOffset.UtcNow,
                    result with { FromStorage = false }), ct).ConfigureAwait(false);
                return true;
            }, token).ConfigureAwait(false);
        return InstallerElevationInspector.ForInstallerType(result with { FromStorage = false }, installerType);
    }

    private static async Task<T?> BestEffortAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(StorageTimeout);
        CancellationToken budget = timeout.Token;
        Task<T> pending = Task.Run(() => action(budget), CancellationToken.None);
        try { return await pending.WaitAsync(budget).ConfigureAwait(false); }
        catch (Exception) when (!token.IsCancellationRequested)
        {
            _ = pending.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return default;
        }
        catch (OperationCanceledException)
        {
            _ = pending.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(StoredInstallerInspection))]
[JsonSerializable(typeof(InstallerElevationLogData))]
internal partial class InstallerInspectionJsonContext : JsonSerializerContext;
