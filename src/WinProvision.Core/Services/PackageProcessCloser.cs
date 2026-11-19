using System.Diagnostics;
using System.Text.RegularExpressions;

namespace WinProvision.Core.Services;

/// <summary>Gracefully closes configured package processes and only force-kills on explicit opt-in.</summary>
public static partial class PackageProcessCloser
{
    private static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(3);
    private static readonly Regex SafeName = SafeProcessNameRegex();

    public static async Task<int> CloseAsync(IEnumerable<string>? processNames, bool force, CancellationToken cancellationToken)
    {
        if (processNames is null) return 0;
        int closed = 0;
        foreach (string candidate in processNames.Select(name => Path.GetFileNameWithoutExtension(name) ?? string.Empty)
                     .Where(name => !string.IsNullOrWhiteSpace(name) && SafeName.IsMatch(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase).Take(32))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process[] processes;
            try { processes = Process.GetProcessesByName(candidate); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { continue; }

            foreach (Process process in processes)
            {
                using (process)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (process.HasExited) continue;
                        _ = process.CloseMainWindow();
                        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(GracePeriod, cancellationToken).ConfigureAwait(false); }
                        catch (TimeoutException) { }
                        if (!process.HasExited && force)
                        {
                            process.Kill(entireProcessTree: true);
                            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                        }
                        if (process.HasExited) closed++;
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or System.Security.SecurityException) { }
                }
            }
        }
        return closed;
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeProcessNameRegex();
}
