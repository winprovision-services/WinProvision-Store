using WinProvision.Core.Services;

namespace WinProvision.Cli;

internal static class CliDiagnostics
{
    private static readonly object Gate = new();
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStoreWinUI3", "logs", "cli");

    public static void Write(string message)
    {
        // Diagnostics must never interrupt a package operation or spill into JSON output.
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DirectoryPath);
                File.AppendAllText(Path.Combine(DirectoryPath, $"cli-{Environment.ProcessId}.log"),
                    $"{DateTimeOffset.Now:O} {SensitiveDataRedactor.Redact(CliApplication.Clean(message))}{Environment.NewLine}");
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
