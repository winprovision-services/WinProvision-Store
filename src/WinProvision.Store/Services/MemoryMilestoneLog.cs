using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WinProvision.Store.Services;

/// <summary>Captura métricas somente quando WINPROVISION_MEMLOG é 1 ou 2.</summary>
internal sealed partial class MemoryMilestoneLog
{
    private readonly int _mode;
    private readonly string _path;
    private readonly HashSet<string> _recorded = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    private MemoryMilestoneLog(int mode)
    {
        _mode = mode;
        _path = Path.Combine(Path.GetTempPath(), $"WinProvisionStore-memory-{Environment.ProcessId}-mode{mode}.csv");
        File.WriteAllText(_path,
            "TimestampUtc,Milestone,PrivateWorkingSetBytes,GcTotalMemoryFalseBytes,GcHeapSizeBytes" + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    public static MemoryMilestoneLog? TryCreate()
    {
        string? value = Environment.GetEnvironmentVariable("WINPROVISION_MEMLOG");
        if (value is not ("1" or "2")) return null;
        try { return new MemoryMilestoneLog(value == "2" ? 2 : 1); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WinProvision.Core.Services.CoreLog.Debug($"Memory milestone log unavailable: {exception.Message}");
            return null;
        }
    }

    public void RecordOnce(string milestone)
    {
        lock (_gate)
        {
            if (!_recorded.Add(milestone)) return;

            try
            {
                if (_mode == 2)
                {
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                }

                using var process = System.Diagnostics.Process.GetCurrentProcess();
                process.Refresh();
                ulong privateWorkingSet = ReadPrivateWorkingSet(process.Handle);
                GCMemoryInfo gc = GC.GetGCMemoryInfo();
                string line = string.Join(',',
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    milestone,
                    privateWorkingSet.ToString(CultureInfo.InvariantCulture),
                    GC.GetTotalMemory(forceFullCollection: false).ToString(CultureInfo.InvariantCulture),
                    gc.HeapSizeBytes.ToString(CultureInfo.InvariantCulture));
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
            {
                WinProvision.Core.Services.CoreLog.Debug($"Memory milestone sample failed: {exception.Message}");
            }
        }
    }

    private static ulong ReadPrivateWorkingSet(nint process)
    {
        var counters = new ProcessMemoryCountersEx2 { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        if (!GetProcessMemoryInfo(process, ref counters, counters.Cb))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return counters.PrivateWorkingSetSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [LibraryImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCountersEx2 counters, uint size);
}
