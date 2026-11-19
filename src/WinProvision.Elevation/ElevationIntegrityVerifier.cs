using System.Security.Cryptography;

namespace WinProvision.Elevation;

public static class ElevationIntegrityVerifier
{
    public static void VerifySha256(string path, string expectedSha256)
    {
        using var stream = File.OpenRead(path);
        byte[] expected = Convert.FromHexString(expectedSha256);
        if (expected.Length != SHA256.HashSizeInBytes || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), expected))
            throw new InvalidDataException("O SHA-256 do arquivo diverge do valor esperado.");
    }

    public static async Task<bool> VerifyAsync(string path, string? expectedSha256, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return false;
        byte[] expected;
        try { expected = Convert.FromHexString(expectedSha256); }
        catch (FormatException) { return false; }
        if (expected.Length != SHA256.HashSizeInBytes) return false;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] actual = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
