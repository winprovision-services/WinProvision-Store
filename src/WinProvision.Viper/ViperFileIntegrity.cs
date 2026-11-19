using System.Buffers;
using System.Security.Cryptography;

namespace WinProvision.Viper;

/// <summary>SHA-256 por streaming com I/O assíncrona e blocos adequados a arquivos grandes.</summary>
public static class ViperFileIntegrity
{
    public static async Task<string> ComputeSha256Async(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        await using var stream = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
            FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            int count;
            while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                hash.AppendData(buffer, 0, count);
            token.ThrowIfCancellationRequested();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
}
