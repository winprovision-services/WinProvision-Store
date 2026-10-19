#if DEBUG
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WinProvision.Store.Services;

/// <summary>Captura métricas de memória somente em builds Debug, sem tocar no estado da interface.</summary>
internal static class DebugMemoryTelemetry
{
    private static readonly string LogPath = Path.Combine(
        Path.GetTempPath(), $"WinProvisionStore-memory-debug-{Environment.ProcessId}.csv");
    private static int _started;

    public static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try
        {
            File.WriteAllText(LogPath,
                "TimestampUtc,ProcessId,PrivateMemorySize64Bytes,GcTotalMemoryFalseBytes,GcHeapSizeBytes" + Environment.NewLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Memory telemetry log unavailable: {ex.Message}");
            return;
        }

        _ = Task.Run(RecordAsync);
    }

    private static async Task RecordAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var gc = GC.GetGCMemoryInfo();
                string line = string.Join(',',
                    DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
                    process.PrivateMemorySize64.ToString(CultureInfo.InvariantCulture),
                    GC.GetTotalMemory(forceFullCollection: false).ToString(CultureInfo.InvariantCulture),
                    gc.HeapSizeBytes.ToString(CultureInfo.InvariantCulture));
                await File.AppendAllTextAsync(LogPath, line + Environment.NewLine, Encoding.UTF8).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Debug.WriteLine($"Memory telemetry sample failed: {ex.Message}");
            }
        }
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false));
    }
}
#endif
