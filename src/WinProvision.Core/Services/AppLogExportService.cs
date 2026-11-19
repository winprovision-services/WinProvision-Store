using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace WinProvision.Core.Services;

public static class AppLogExportService
{
    public static async Task ExportRedactedZipAsync(
        string logDirectory,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string fullDestination = Path.GetFullPath(destinationPath);
        string sourceDirectory = Path.GetFullPath(logDirectory);
        string[] files = Directory.Exists(sourceDirectory)
            ? Directory.GetFiles(sourceDirectory, "app-*.jsonl").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToArray()
            : [];
        await using var output = new FileStream(fullDestination, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipArchiveEntry entry = archive.CreateEntry(Path.GetFileName(file), CompressionLevel.Optimal);
            await using Stream entryStream = entry.Open();
            await using var writer = new StreamWriter(entryStream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await using var input = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppLogEvent? logEvent;
                try
                {
                    logEvent = JsonSerializer.Deserialize(line, AppLogJsonContext.Default.AppLogEvent);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (logEvent is null)
                    continue;
                AppLogEvent redacted = SensitiveDataRedactor.RedactEvent(logEvent);
                await writer.WriteLineAsync(JsonSerializer.Serialize(redacted, AppLogJsonContext.Default.AppLogEvent))
                    .ConfigureAwait(false);
            }
        }

        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
