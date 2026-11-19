using System.Diagnostics;
using System.Runtime.CompilerServices;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

/// <summary>Instrumentação temporária de desempenho, ativa somente em builds Debug.</summary>
internal static class DebugPerformanceTelemetry
{
#if DEBUG
    private static long StartedAt = Stopwatch.GetTimestamp();
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), $"WinProvisionStore-performance-{Environment.ProcessId}.log");
    private static int _imageActive;
    private static int _imagePeak;
    private static long _imageRequests;
    private static long _imageRepeated;
    private static long _imageCacheHits;
    private static long _catalogJsonReads;
    private static long _catalogJsonDeserializations;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> SeenImageUrls = new(StringComparer.Ordinal);

    public static void BeginSession() => Interlocked.Exchange(ref StartedAt, Stopwatch.GetTimestamp());

    public static void Mark(string name)
    {
        long elapsed = Stopwatch.GetElapsedTime(StartedAt).Ticks / TimeSpan.TicksPerMillisecond;
        Write($"mark={name} elapsedMs={elapsed}");
    }

    public static void Duration(string name, long startedAt, string? detail = null)
    {
        long elapsed = Stopwatch.GetElapsedTime(startedAt).Ticks / TimeSpan.TicksPerMillisecond;
        Write($"duration={name} elapsedMs={elapsed}{(detail is null ? string.Empty : " " + detail)}");
    }

    public static long Start() => Stopwatch.GetTimestamp();
    public static void CatalogJsonRead() => Interlocked.Increment(ref _catalogJsonReads);
    public static void CatalogJsonDeserialized(long startedAt, string source)
    {
        long count = Interlocked.Increment(ref _catalogJsonDeserializations);
        Duration("catalog-json-deserialize", startedAt, $"source={source} count={count} reads={Interlocked.Read(ref _catalogJsonReads)}");
    }
    public static bool ImageCacheHit()
    {
        Interlocked.Increment(ref _imageCacheHits);
        return true;
    }
    public static void ImageRequestJoining(string url)
    {
        Interlocked.Increment(ref _imageRepeated);
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)))[..12];
        Write($"image-request-joined urlHash={hash}");
    }
    public static void ImageDownloadStarted(string url)
    {
        Interlocked.Increment(ref _imageRequests);
        if (!SeenImageUrls.TryAdd(url, 0)) Interlocked.Increment(ref _imageRepeated);
        int active = Interlocked.Increment(ref _imageActive);
        int peak;
        do { peak = Volatile.Read(ref _imagePeak); if (active <= peak) break; }
        while (Interlocked.CompareExchange(ref _imagePeak, active, peak) != peak);
    }
    public static void ImageDownloadFinished() => Interlocked.Decrement(ref _imageActive);
    public static void ImageSnapshot(string point) => Write(
        $"image-snapshot={point} requests={Interlocked.Read(ref _imageRequests)} repeats={Interlocked.Read(ref _imageRepeated)} active={Volatile.Read(ref _imageActive)} peak={Volatile.Read(ref _imagePeak)} cacheHits={Interlocked.Read(ref _imageCacheHits)} inFlight={AsyncImage.GetMemoryStatistics().Pending}");
    public static void CatalogSnapshot(string point)
    {
        (long reads, long deserializations) = FeaturedCatalogService.GetJsonDiagnostics();
        (long packageReads, long packageDeserializations) = StoreService.GetCatalogJsonDiagnostics();
        Write($"catalog-json-snapshot={point} storeBannerReads={Interlocked.Read(ref _catalogJsonReads)} storeBannerDeserializations={Interlocked.Read(ref _catalogJsonDeserializations)} featuredReads={reads} featuredDeserializations={deserializations} packageCatalogReads={packageReads} packageCatalogDeserializations={packageDeserializations}");
    }
    private static void Write(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}"); }
        catch (IOException) { }
        CoreLog.Debug("[Performance]", message);
    }
#else
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void BeginSession() { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Mark(string name) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void Duration(string name, long startedAt, string? detail = null) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static long Start() => 0;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void CatalogJsonRead() { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void CatalogJsonDeserialized(long startedAt, string source) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static bool ImageCacheHit() => true;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void ImageRequestJoining(string url) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void ImageDownloadStarted(string url) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void ImageDownloadFinished() { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void ImageSnapshot(string point) { }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static void CatalogSnapshot(string point) { }
#endif
}
