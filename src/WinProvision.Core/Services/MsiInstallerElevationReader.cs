using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WinProvision.Core.Services;

/// <summary>
/// Database access only, never an installation session. Handles are created and closed on the same dedicated thread.
/// Official API contracts, constants and the decision table: docs/Elevation.md, Etapa 4b.
/// </summary>
internal sealed class MsiInstallerElevationReader
{
    internal const int MaximumConcurrentReaders = 2;
    private static readonly SemaphoreSlim Slots = new(MaximumConcurrentReaders, MaximumConcurrentReaders);
    private readonly Func<string, CancellationToken, InstallerElevationInspection> _read;
    private readonly TimeSpan _duration;

    internal MsiInstallerElevationReader() : this(ReadDatabase, InstallerElevationInspector.MaximumDuration) { }
    internal MsiInstallerElevationReader(Func<string, CancellationToken, InstallerElevationInspection> read, TimeSpan duration)
    { _read = read; _duration = duration; }

    internal async Task<InstallerElevationInspection> InspectAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_duration);
        CancellationToken budget = timeout.Token;
        bool acquired = false;
        try
        {
            await Slots.WaitAsync(budget).ConfigureAwait(false);
            acquired = true;
            budget.ThrowIfCancellationRequested();
            var completion = new TaskCompletionSource<InstallerElevationInspection>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                InstallerElevationInspection result;
                try { budget.ThrowIfCancellationRequested(); result = _read(path, budget); }
                catch (Exception) { result = InstallerElevationInspection.Unknown("msi-read-failed", false); }
                finally { Slots.Release(); }
                // The task never faults. A caller that timed out cannot consume or cache this late result.
                completion.TrySetResult(result);
            }) { IsBackground = true, Name = "WinProvision MSI inspection" };
            thread.Start();
            acquired = false; // ownership of the slot belongs to the native worker until it actually exits
            InstallerElevationInspection inspection = await completion.Task.WaitAsync(budget).ConfigureAwait(false);
            budget.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            return inspection;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return InstallerElevationInspection.Unknown("msi-time-limit", false); }
        finally { if (acquired) Slots.Release(); }
    }

    private static InstallerElevationInspection ReadDatabase(string path, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows()) return InstallerElevationInspection.Unknown("msi-windows-only", false);
        var signals = new List<InstallerInspectionSignal>();
        try
        {
            token.ThrowIfCancellationRequested();
            // MSIDBOPEN_READONLY = 0, confirmed in Windows SDK MsiQuery.h and MsiOpenDatabaseW docs.
            Check(Native.MsiOpenDatabase(path, IntPtr.Zero, out uint database), "open");
            try
            {
                token.ThrowIfCancellationRequested();
                Check(Native.MsiGetSummaryInformation(database, null, 0, out uint summary), "summary-open");
                int wordCount;
                try
                {
                    uint length = 0;
                    var text = new StringBuilder(1);
                    Check(Native.MsiSummaryInfoGetProperty(summary, 15, out uint type, out wordCount,
                        out _, text, ref length), "word-count");
                    if (type == (uint)VarEnum.VT_EMPTY) wordCount = 0; // documented absent Word Count default
                    else if (type != (uint)VarEnum.VT_I4) throw new InvalidDataException("msi-word-count-type");
                }
                finally { Native.MsiCloseHandle(summary); }
                signals.Add(new("msi.WordCount", wordCount.ToString(CultureInfo.InvariantCulture)));
                signals.Add(new("msi.NoElevationRequired", (wordCount & 8) != 0 ? "true" : "false"));
                token.ThrowIfCancellationRequested();
                string allUsers = ReadProperty(database, "SELECT `Value` FROM `Property` WHERE `Property`='ALLUSERS'");
                signals.Add(new("msi.ALLUSERS", allUsers == "" ? "<absent>" : allUsers));
                token.ThrowIfCancellationRequested();
                string perUser = ReadProperty(database, "SELECT `Value` FROM `Property` WHERE `Property`='MSIINSTALLPERUSER'");
                signals.Add(new("msi.MSIINSTALLPERUSER", perUser == "" ? "<absent>" : perUser));
                token.ThrowIfCancellationRequested();
                string decision = allUsers switch
                {
                    "1" => "required", // confirmed machine context, same machine policy as the common resolver
                    "2" when perUser == "1" && (wordCount & 8) != 0 => "not-required",
                    "" when (wordCount & 8) != 0 => "not-required",
                    _ => "unknown"
                };
                return new(decision, decision == "unknown" ? "msi-properties-inconclusive" : "msi-context-and-word-count", signals.ToArray());
            }
            finally { Native.MsiCloseHandle(database); }
        }
        catch (MsiReadException ex)
        {
            signals.Add(new("msi.apiError", ex.Operation + ":" + ex.Code.ToString(CultureInfo.InvariantCulture)));
            return new("unknown", "msi-api-error", signals.ToArray(), Complete: false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return new("unknown", "msi-unreadable-or-malformed", signals.ToArray(), Complete: false); }
    }

    private static string ReadProperty(uint database, string query)
    {
        Check(Native.MsiDatabaseOpenView(database, query, out uint view), "property-view");
        try
        {
            Check(Native.MsiViewExecute(view, 0), "property-query");
            uint fetch = Native.MsiViewFetch(view, out uint record);
            if (fetch == 259) return ""; // ERROR_NO_MORE_ITEMS: no authored value, not an API failure
            Check(fetch, "property-fetch");
            try
            {
                const int maximumCharacters = 128;
                var value = new StringBuilder(maximumCharacters + 1);
                uint length = maximumCharacters + 1;
                Check(Native.MsiRecordGetString(record, 1, value, ref length), "property-value");
                return value.ToString();
            }
            finally { Native.MsiCloseHandle(record); }
        }
        finally { Native.MsiCloseHandle(view); }
    }

    private static void Check(uint code, string operation)
    { if (code != 0) throw new MsiReadException(operation, code); }
    private sealed class MsiReadException(string operation, uint code) : Exception
    {
        internal string Operation { get; } = operation;
        internal uint Code { get; } = code;
    }

    private static class Native
    {
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiOpenDatabaseW")]
        internal static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiGetSummaryInformationW")]
        internal static extern uint MsiGetSummaryInformation(uint database, string? path, uint updates, out uint summary);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiSummaryInfoGetPropertyW")]
        internal static extern uint MsiSummaryInfoGetProperty(uint summary, uint property, out uint type, out int integer,
            out long fileTime, StringBuilder text, ref uint length);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiDatabaseOpenViewW")]
        internal static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiViewExecute(uint view, uint record);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiViewFetch(uint view, out uint record);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiRecordGetStringW")]
        internal static extern uint MsiRecordGetString(uint record, uint field, StringBuilder value, ref uint length);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport("msi.dll", ExactSpelling = true)]
        internal static extern uint MsiCloseHandle(uint handle);
    }
}
