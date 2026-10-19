using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace WinProvision.Viper;

internal sealed record ViperPiece(int Index, string Sha256);
internal sealed record ViperSession(int Format, string SourceFingerprint, string ETag, long Length, int PieceSize, List<ViperPiece> Completed);

[JsonSerializable(typeof(ViperSession))]
internal partial class ViperJson : JsonSerializerContext;

internal static class ViperSessionStore
{
    public static string Fingerprint(ViperRequest request)
    {
        // URLs and credentials never appear in the checkpoint. Headers bind authentication contexts.
        var identity = new StringBuilder(request.Source.AbsoluteUri);
        foreach (var header in request.Headers.OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase))
            identity.Append('\n').Append(header.Key.ToLowerInvariant()).Append(':').Append(header.Value);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())));
    }
    public static ViperSession? Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024) return null;
            return JsonSerializer.Deserialize(File.ReadAllBytes(path), ViperJson.Default.ViperSession);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    public static void Save(string path, ViperSession session)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(JsonSerializer.SerializeToUtf8Bytes(session, ViperJson.Default.ViperSession));
                file.Flush(true);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static async Task<string> HashPieceAsync(SafeFileHandle file, long start, int length, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int remaining = length;
            while (remaining > 0)
            {
                int count = await RandomAccess.ReadAsync(file, buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), start, token);
                if (count == 0) throw new EndOfStreamException("A parte local está incompleta.");
                hash.AppendData(buffer, 0, count); start += count; remaining -= count;
            }
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
}
