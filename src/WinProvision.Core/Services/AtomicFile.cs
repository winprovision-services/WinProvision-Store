using System.Text;

namespace WinProvision.Core.Services;

/// <summary>Publica um arquivo completo por renomeação no mesmo diretório e volume.</summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents, Encoding? encoding = null) =>
        WriteAllBytes(path, Encode(contents, encoding));

    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteAllBytesAsync(path, Encode(contents, null), cancellationToken);

    public static Task WriteAllTextAsync(string path, string contents, Encoding encoding, CancellationToken cancellationToken = default) =>
        WriteAllBytesAsync(path, Encode(contents, encoding), cancellationToken);

    public static void WriteAllBytes(string path, byte[] contents)
        => Write(path, stream => stream.Write(contents));

    public static void Write(string path, Action<Stream> write)
    {
        string destination = Path.GetFullPath(path);
        string temporary = CreateTemporaryPath(destination);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.WriteThrough))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { CleanUp(temporary); }
    }

    public static async Task WriteAllBytesAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
        => await WriteAsync(path, (stream, token) => stream.WriteAsync(contents, token).AsTask(), cancellationToken).ConfigureAwait(false);

    public static async Task WriteAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken = default)
    {
        string destination = Path.GetFullPath(path);
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = CreateTemporaryPath(destination);
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { CleanUp(temporary); }
    }

    private static byte[] Encode(string contents, Encoding? encoding)
    {
        encoding ??= new UTF8Encoding(false);
        byte[] preamble = encoding.GetPreamble();
        return [.. preamble, .. encoding.GetBytes(contents)];
    }

    private static string CreateTemporaryPath(string destination)
    {
        string directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
    }

    private static void CleanUp(string temporary)
    {
        try { File.Delete(temporary); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { CoreLog.Warning(ex, "AtomicFile.CleanUp"); }
    }
}
