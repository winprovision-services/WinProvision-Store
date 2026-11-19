namespace WinProvision.Core.Services;

public static class AppLogLegacyImporter
{
    public static async Task ImportOnceAsync(
        IAppLog log,
        string legacyFilePath,
        string completionMarkerPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        string marker = Path.GetFullPath(completionMarkerPath);
        if (File.Exists(marker) || !File.Exists(legacyFilePath))
            return;

        try
        {
            await using var stream = new FileStream(legacyFilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length > 16 * 1024)
                    line = line[..(16 * 1024)] + " [truncated]";
                log.Write(AppLogLevel.Info, "App", "LegacyLogImport", line);
            }

            await log.FlushAsync(cancellationToken).ConfigureAwait(false);
            string directory = Path.GetDirectoryName(marker)
                ?? throw new InvalidOperationException("O marcador de migração não possui pasta.");
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("O"), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.Write(AppLogLevel.Warning, "App", "LegacyLogImport",
                "Não foi possível importar o log técnico anterior.", exception: exception);
        }
    }

    public static string DefaultLegacyFilePath => Path.Combine(Path.GetTempPath(), "WinProvision-Log.log");

    public static string DefaultCompletionMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore", "log-migration-v1.done");
}
