using System.Diagnostics;
using System.Text;

namespace WinProvision.Elevation;

internal static class ProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(string executablePath, string? workingDirectory)
    {
        string resolvedWorkingDirectory = ResolveWorkingDirectory(executablePath, workingDirectory);
        return new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = resolvedWorkingDirectory
        };
    }

    public static async Task<ElevationResult> RunAsync(ProcessStartInfo startInfo, IProgress<string>? progress,
        CancellationToken cancellationToken, bool waitAfterStart, bool classifyHelperExitCode = false,
        Action<string>? diagnosticLog = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new(ElevationStatus.HelperError, null, string.Empty, string.Empty, "O processo não iniciou.");
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                                           or System.IO.IOException or UnauthorizedAccessException)
        {
            return new(ElevationStatus.HelperError, null, string.Empty, string.Empty, exception.Message);
        }

        try { (progress as IElevationDispatchProgress)?.OnStarted(); }
        catch { /* The dispatch observer cannot interrupt a process that has already started. */ }

        // This is the dispatch boundary: cancellation is no longer allowed to abandon a live child.
        Task<string> stdout = DrainAsync(process.StandardOutput, progress, classifyKnownHelperLines: classifyHelperExitCode);
        Task<string> stderr = DrainAsync(process.StandardError, progress, prefix: "[stderr] ",
            classifyKnownHelperLines: classifyHelperExitCode);
        if (waitAfterStart)
            await process.WaitForExitAsync().ConfigureAwait(false);
        else
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        string output = await stdout.ConfigureAwait(false);
        string errorOutput = await stderr.ConfigureAwait(false);
        int exitCode = process.ExitCode;

        if (classifyHelperExitCode && exitCode == GsudoRunner.HelperErrorExitCode)
        {
            string helperOutput = output + Environment.NewLine + errorOutput;
            ElevationStatus status = GsudoRunner.ClassifyHelperFailure(helperOutput);
            if (!string.IsNullOrWhiteSpace(helperOutput))
            {
                try
                {
                    string diagnostic = "WinProvision Elevator: " + helperOutput.Trim();
                    if (diagnosticLog is not null) diagnosticLog(diagnostic);
                    else System.Diagnostics.Trace.TraceWarning(diagnostic);
                }
                catch { }
            }
            return new(status, null, string.Empty, string.Empty,
                status == ElevationStatus.HelperError ? helperOutput.Trim() : null);
        }

        return new(exitCode == 0 ? ElevationStatus.Success : ElevationStatus.Failed,
            exitCode, output, errorOutput);
    }

    private static async Task<string> DrainAsync(StreamReader reader, IProgress<string>? progress, string prefix = "",
        bool classifyKnownHelperLines = false)
    {
        var capture = new ElevationOutputCapture();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            capture.Add(line);
            bool isHelperDiagnostic = classifyKnownHelperLines
                && GsudoRunner.ClassifyHelperFailure(line) is ElevationStatus.UacCancelled or ElevationStatus.InvalidCredentials;
            try { if (!isHelperDiagnostic) progress?.Report(prefix + line); }
            catch { /* Output observers must not stop or detach an administrative child. */ }
        }
        return capture.GetText();
    }

    private static string ResolveWorkingDirectory(string executablePath, string? requested)
    {
        string? candidate = requested;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Path.IsPathFullyQualified(executablePath) ? Path.GetDirectoryName(executablePath) : Environment.CurrentDirectory;
        try
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)
                && !candidate.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { }

        return OperatingSystem.IsWindows() ? Environment.SystemDirectory : Environment.CurrentDirectory;
    }
}
