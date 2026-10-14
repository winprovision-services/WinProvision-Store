using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed partial class AsyncImage : DependencyObject
{
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan DiskCacheTtl = TimeSpan.FromDays(14);
    private const int MaxEncodedImageBytes = 16 * 1024 * 1024;
    private const uint MaxDecodedImageEdge = 2048;
    private const long MaxDiskCacheBytes = 512L * 1024 * 1024;
    private static readonly HttpClient Client = new(new RequestRateLimitHandler(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }));
    private static readonly SemaphoreSlim DownloadGate = new(
        Environment.ProcessorCount <= 2 ? 2 : 4);
    private static readonly ConcurrentDictionary<string, Lazy<Task<byte[]?>>> InFlight = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, DateTime> NegativeCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<long, CancellationTokenSource> ActiveImageCancellations = new();
    private static readonly ConcurrentDictionary<long, Task> ActiveImageLoads = new();
    private static readonly string DiskCacheFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore",
        "Cache",
        "RenderedIcons");
    private static readonly BitmapMemoryCache MemoryCache = new(GetMemoryBudget());
    private static readonly Lock MemoryBudgetGate = new();
    private static bool _isInBackground;
    private static long _cacheGeneration;
    private static long _nextImageLoadId;
    private static long _nextDiskTrimUtcTicks;
    private static int _diskTrimScheduled;

    public static readonly DependencyProperty SourceUrlProperty = DependencyProperty.RegisterAttached(
        "SourceUrl", typeof(string), typeof(AsyncImage), new PropertyMetadata(null, SourceChanged));
    public static readonly DependencyProperty DecodePixelWidthProperty = DependencyProperty.RegisterAttached(
        "DecodePixelWidth", typeof(int), typeof(AsyncImage), new PropertyMetadata(0, DecodeChanged));
    public static readonly DependencyProperty HasContentProperty = DependencyProperty.RegisterAttached(
        "HasContent", typeof(bool), typeof(AsyncImage), new PropertyMetadata(false));
    private static readonly DependencyProperty ObservedProperty = DependencyProperty.RegisterAttached(
        "Observed", typeof(bool), typeof(AsyncImage), new PropertyMetadata(false));
    private static readonly DependencyProperty LoadCancellationProperty = DependencyProperty.RegisterAttached(
        "LoadCancellation", typeof(CancellationTokenSource), typeof(AsyncImage), new PropertyMetadata(null));

    public static string? GetSourceUrl(DependencyObject element) => (string?)element.GetValue(SourceUrlProperty);
    public static void SetSourceUrl(DependencyObject element, string? value) => element.SetValue(SourceUrlProperty, value);
    public static int GetDecodePixelWidth(DependencyObject element) => (int)element.GetValue(DecodePixelWidthProperty);
    public static void SetDecodePixelWidth(DependencyObject element, int value) => element.SetValue(DecodePixelWidthProperty, value);
    public static bool GetHasContent(DependencyObject element) => (bool)element.GetValue(HasContentProperty);
    public static void SetHasContent(DependencyObject element, bool value) => element.SetValue(HasContentProperty, value);

    internal static (int Count, long Bytes, int Pending, long BudgetBytes) GetMemoryStatistics()
    {
        var statistics = MemoryCache.TrimAndGetStatistics();
        return (statistics.Count, statistics.Bytes, InFlight.Count, statistics.BudgetBytes);
    }

    internal static void TrimForBackground()
    {
        lock (MemoryBudgetGate)
            _isInBackground = true;
        UpdateMemoryBudget();
    }

    internal static void RestoreForegroundBudget()
    {
        lock (MemoryBudgetGate)
            _isInBackground = false;
        UpdateMemoryBudget();
    }

    internal static void RefreshMemoryBudget() => UpdateMemoryBudget();

    private static void UpdateMemoryBudget()
    {
        MemoryCache.SetBudget(GetEffectiveMemoryBudget());
    }

    private static long GetEffectiveMemoryBudget()
    {
        lock (MemoryBudgetGate)
        {
            long budget = GetMemoryBudget();
            if (_isInBackground)
                budget = Math.Min(budget, 8L * 1024 * 1024);
            return budget;
        }
    }

    internal static async Task ShutdownAsync()
    {
        foreach (CancellationTokenSource cancellation in ActiveImageCancellations.Values)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        Task[] activeImageLoads = ActiveImageLoads.Values.ToArray();
        if (activeImageLoads.Length > 0)
        {
            try { await Task.WhenAll(activeImageLoads); }
            catch (Exception exception) { WinProvisionLog.Warning(exception, "AsyncImage.Shutdown"); }
        }

        Task<byte[]?>[] pending = InFlight.Values
            .Where(flight => flight.IsValueCreated)
            .Select(flight => flight.Value)
            .ToArray();
        if (pending.Length == 0)
            return;

        try
        {
            await Task.WhenAll(pending);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "AsyncImage.Shutdown");
        }
    }

    public static void ClearCache()
    {
        Interlocked.Increment(ref _cacheGeneration);
        MemoryCache.Clear();
        NegativeCache.Clear();
        InFlight.Clear();
        Interlocked.Exchange(ref _nextDiskTrimUtcTicks, 0);
        try
        {
            if (Directory.Exists(DiskCacheFolder))
                Directory.Delete(DiskCacheFolder, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WinProvisionLog.Warning(exception, "AsyncImage.ClearCache");
            throw;
        }
    }

    public static Task<BitmapImage?> LoadBitmapAsync(string url, int decodePixelWidth = 0, CancellationToken cancellationToken = default) =>
        LoadBitmapCoreAsync(url, decodePixelWidth, cancellationToken);

    private static void SourceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not Image image)
            return;

        ObserveImage(image);
        CancelImageLoad(image);
        image.Source = null;
        SetHasContent(image, false);
        if (image.IsLoaded)
            BeginLoad(image, args.NewValue as string, GetDecodePixelWidth(image));
    }

    private static void ObserveImage(Image image)
    {
        if (image.GetValue(ObservedProperty) is true)
            return;

        image.ImageOpened += Image_Opened;
        image.ImageFailed += Image_Failed;
        image.Loaded += Image_Loaded;
        image.Unloaded += Image_Unloaded;
        image.SetValue(ObservedProperty, true);
    }

    private static void Image_Opened(object sender, RoutedEventArgs args)
    {
        if (sender is Image image)
            SetHasContent(image, true);
    }

    private static void Image_Failed(object sender, ExceptionRoutedEventArgs args)
    {
        if (sender is Image image)
        {
            SetHasContent(image, false);
            WinProvisionLog.Write($"[AsyncImage] Falha ao exibir a imagem: {args.ErrorMessage}");
        }
    }

    private static void Image_Loaded(object sender, RoutedEventArgs args)
    {
        if (sender is Image { Source: null } image)
            BeginLoad(image, GetSourceUrl(image), GetDecodePixelWidth(image));
    }

    private static void Image_Unloaded(object sender, RoutedEventArgs args)
    {
        if (sender is not Image image)
            return;

        CancelImageLoad(image);
        image.Source = null;
        SetHasContent(image, false);
    }

    private static void DecodeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is Image image && image.IsLoaded)
            BeginLoad(image, GetSourceUrl(image), (int)args.NewValue);
    }

    private static void BeginLoad(Image image, string? url, int decodePixelWidth)
    {
        CancelImageLoad(image);
        if (!image.IsLoaded)
            return;

        var cancellation = new CancellationTokenSource();
        image.SetValue(LoadCancellationProperty, cancellation);
        var target = new WeakReference<Image>(image);
        long loadId = Interlocked.Increment(ref _nextImageLoadId);
        ActiveImageCancellations[loadId] = cancellation;
        Task load = ObserveImageLoadAsync(target, url, Math.Max(0, decodePixelWidth), cancellation, loadId);
        ActiveImageLoads[loadId] = load;
    }

    private static async Task ObserveImageLoadAsync(
        WeakReference<Image> target,
        string? url,
        int width,
        CancellationTokenSource cancellation,
        long loadId)
    {
        await Task.Yield();
        try
        {
            await LoadAndAssignAsync(target, url, width, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "AsyncImage.ObserveImageLoad");
        }
        finally
        {
            if (target.TryGetTarget(out Image? image) &&
                ReferenceEquals(image.GetValue(LoadCancellationProperty), cancellation))
            {
                image.ClearValue(LoadCancellationProperty);
            }
            ActiveImageCancellations.TryRemove(loadId, out _);
            ActiveImageLoads.TryRemove(loadId, out _);
            cancellation.Dispose();
        }
    }

    private static async Task LoadAndAssignAsync(
        WeakReference<Image> target,
        string? url,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        if (!target.TryGetTarget(out Image? image))
            return;

        image.Source = null;
        SetHasContent(image, false);
        if (string.IsNullOrWhiteSpace(url) ||
            url.Equals(IconService.DefaultIconPackUri, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            uri.Scheme is "ms-appx" or "ms-appdata")
        {
            var localBitmap = new BitmapImage
            {
                DecodePixelWidth = ClampDecodeWidth(decodePixelWidth)
            };
            localBitmap.UriSource = uri;
            if (target.TryGetTarget(out image) &&
                string.Equals(GetSourceUrl(image), url, StringComparison.Ordinal) &&
                image.IsLoaded)
            {
                image.Source = localBitmap;
            }
            return;
        }

        BitmapImage? bitmap = await LoadBitmapCoreAsync(url, decodePixelWidth, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (bitmap is not null &&
            target.TryGetTarget(out image) &&
            string.Equals(GetSourceUrl(image), url, StringComparison.Ordinal) &&
            GetDecodePixelWidth(image) == decodePixelWidth &&
            image.IsLoaded)
        {
            image.Source = bitmap;
            SetHasContent(image, true);
        }
    }

    private static async Task<BitmapImage?> LoadBitmapCoreAsync(
        string url,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(url) ||
            url.Equals(IconService.DefaultIconPackUri, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int width = Math.Clamp(decodePixelWidth, 0, (int)MaxDecodedImageEdge);
        string requestKey = $"{url}|width={width}";
        if (MemoryCache.TryGet(requestKey, out BitmapImage? cached))
            return cached;
        if (IsNegativeCached(requestKey))
            return null;

        long generation = Interlocked.Read(ref _cacheGeneration);
        try
        {
            bool isRemote = IsHttpUri(url);
            if (isRemote)
            {
                string cachePath = GetDiskPath(url);
                byte[]? cachedBytes = await TryReadDiskCacheAsync(cachePath, cancellationToken);
                if (cachedBytes is not null)
                {
                    BitmapImage? cachedBitmap = await DecodeAsync(cachedBytes, width);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (cachedBitmap is not null)
                    {
                        MemoryCache.Set(requestKey, cachedBitmap);
                        TouchDiskCacheEntry(cachePath);
                        ScheduleDiskCacheTrim();
                        return cachedBitmap;
                    }

                    try
                    {
                        File.Delete(cachePath);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        WinProvisionLog.Warning(exception, "AsyncImage.RemoveCorruptCache");
                    }
                }
            }

            byte[]? bytes = isRemote
                ? await LoadSharedRemoteBytesAsync(url, generation, cancellationToken)
                : await LoadLocalBytesAsync(url, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is null)
            {
                RememberFailure(requestKey);
                return null;
            }

            BitmapImage? bitmap = await DecodeAsync(bytes, width);
            cancellationToken.ThrowIfCancellationRequested();
            if (bitmap is null)
            {
                RememberFailure(requestKey);
                return null;
            }

            NegativeCache.TryRemove(requestKey, out _);
            MemoryCache.Set(requestKey, bitmap);
            if (isRemote && generation == Interlocked.Read(ref _cacheGeneration))
            {
                try
                {
                    Directory.CreateDirectory(DiskCacheFolder);
                    await AtomicFile.WriteAllBytesAsync(GetDiskPath(url), bytes, cancellationToken);
                    ScheduleDiskCacheTrim();
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    WinProvisionLog.Warning(exception, "AsyncImage.WriteDiskCache");
                }
            }
            return bitmap;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "AsyncImage.LoadBitmap");
            RememberFailure(requestKey);
            return null;
        }
    }

    private static async Task<byte[]?> TryReadDiskCacheAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) >= DiskCacheTtl)
            {
                File.Delete(path);
                return null;
            }
            return await ReadBoundedFileAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            WinProvisionLog.Warning(exception, "AsyncImage.ReadDiskCache");
            try { File.Delete(path); }
            catch (Exception deleteException) when (deleteException is IOException or UnauthorizedAccessException)
            {
                WinProvisionLog.Warning(deleteException, "AsyncImage.DeleteDiskCache");
            }
            return null;
        }
    }

    private static async Task<byte[]?> LoadSharedRemoteBytesAsync(
        string url,
        long generation,
        CancellationToken cancellationToken)
    {
        var flight = InFlight.GetOrAdd(url, key =>
        new Lazy<Task<byte[]?>>(() => DownloadRemoteBytesAsync(key), LazyThreadSafetyMode.ExecutionAndPublication));
        Task<byte[]?> task = flight.Value;
        _ = RemoveCompletedFlightAsync(url, flight, task);
        return await task.WaitAsync(cancellationToken);
    }

    private static async Task RemoveCompletedFlightAsync(
        string url,
        Lazy<Task<byte[]?>> flight,
        Task<byte[]?> task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "AsyncImage.RemoteFlight");
        }
        finally
        {
            if (InFlight.TryGetValue(url, out var current) && ReferenceEquals(current, flight))
                InFlight.TryRemove(url, out _);
        }
    }

    private static async Task<byte[]?> DownloadRemoteBytesAsync(string url)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await DownloadGate.WaitAsync(timeout.Token);
            try
            {
                using var response = await Client.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > MaxEncodedImageBytes)
                    throw new InvalidDataException("A imagem excede o limite do carregador.");

                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                return await ReadBoundedAsync(stream, timeout.Token);
            }
            finally
            {
                DownloadGate.Release();
            }
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "AsyncImage.Download");
            return null;
        }
    }

    private static async Task<byte[]?> LoadLocalBytesAsync(string source, CancellationToken cancellationToken)
    {
        string path = source;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
            path = uri.LocalPath;
        if (!File.Exists(path))
            return null;
        return await ReadBoundedFileAsync(path, cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await ReadBoundedAsync(stream, cancellationToken);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[32 * 1024];
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            if (output.Length + read > MaxEncodedImageBytes)
                throw new InvalidDataException("A imagem excede o limite do carregador.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static async Task<BitmapImage?> DecodeAsync(byte[] bytes, int requestedWidth)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using IRandomAccessStream random = stream.AsRandomAccessStream();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(random);
            uint sourceWidth = decoder.OrientedPixelWidth;
            uint sourceHeight = decoder.OrientedPixelHeight;
            if (sourceWidth == 0 || sourceHeight == 0)
                return null;

            uint maxWidth = requestedWidth > 0
                ? Math.Min((uint)requestedWidth, MaxDecodedImageEdge)
                : MaxDecodedImageEdge;
            double scale = Math.Min(1d, Math.Min(
                maxWidth / (double)sourceWidth,
                MaxDecodedImageEdge / (double)sourceHeight));
            uint width = Math.Max(1, (uint)Math.Round(sourceWidth * scale));
            uint height = Math.Max(1, (uint)Math.Round(sourceHeight * scale));
            using var encoded = new InMemoryRandomAccessStream();
            if (decoder.DecoderInformation.CodecId == BitmapDecoder.PngDecoderId &&
                sourceWidth == decoder.PixelWidth && sourceHeight == decoder.PixelHeight)
            {
                // Let WIC resize PNGs natively, without a managed pixel array and SetPixelData copy.
                BitmapEncoder encoder = await BitmapEncoder.CreateForTranscodingAsync(encoded, decoder);
                encoder.BitmapTransform.ScaledWidth = width;
                encoder.BitmapTransform.ScaledHeight = height;
                await encoder.FlushAsync();
            }
            else
            {
                var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height };
                var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, encoded);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                    width, height, decoder.DpiX, decoder.DpiY, pixels.DetachPixelData());
                await encoder.FlushAsync();
            }
            encoded.Seek(0);
            var bitmap = new BitmapImage { DecodePixelWidth = (int)width };
            await bitmap.SetSourceAsync(encoded);
            return bitmap;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            WinProvisionLog.Warning(exception, "AsyncImage.Decode");
            return null;
        }
    }

    private static bool IsHttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static int ClampDecodeWidth(int width) =>
        width <= 0 ? (int)MaxDecodedImageEdge : Math.Min(width, (int)MaxDecodedImageEdge);

    private static string GetDiskPath(string url)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Path.Combine(DiskCacheFolder, Convert.ToHexString(hash).ToLowerInvariant() + ".img");
    }

    private static bool IsNegativeCached(string key)
    {
        if (!NegativeCache.TryGetValue(key, out DateTime retryAfter))
            return false;
        if (retryAfter > DateTime.UtcNow)
            return true;
        NegativeCache.TryRemove(key, out _);
        return false;
    }

    private static void RememberFailure(string key)
    {
        if (NegativeCache.Count >= 2048)
        {
            DateTime now = DateTime.UtcNow;
            foreach (var item in NegativeCache.Where(item => item.Value <= now).Take(512))
                NegativeCache.TryRemove(item.Key, out _);
            foreach (string expiredKey in NegativeCache.Keys.Take(Math.Max(0, NegativeCache.Count - 1536)))
                NegativeCache.TryRemove(expiredKey, out _);
        }
        NegativeCache[key] = DateTime.UtcNow + NegativeCacheTtl;
    }

    private static void TouchDiskCacheEntry(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (DateTime.UtcNow - file.LastAccessTimeUtc > TimeSpan.FromHours(1))
                file.LastAccessTimeUtc = DateTime.UtcNow;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            WinProvisionLog.Warning(exception, "AsyncImage.TouchDiskCache");
        }
    }

    private static void ScheduleDiskCacheTrim()
    {
        if (DateTime.UtcNow.Ticks < Interlocked.Read(ref _nextDiskTrimUtcTicks) ||
            Interlocked.Exchange(ref _diskTrimScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                TrimDiskCache();
                Interlocked.Exchange(ref _nextDiskTrimUtcTicks, DateTime.UtcNow.AddHours(1).Ticks);
            }
            catch (Exception exception)
            {
                WinProvisionLog.Warning(exception, "AsyncImage.TrimDiskCache");
                Interlocked.Exchange(ref _nextDiskTrimUtcTicks, DateTime.UtcNow.AddMinutes(5).Ticks);
            }
            finally
            {
                Volatile.Write(ref _diskTrimScheduled, 0);
            }
        });
    }

    private static void TrimDiskCache()
    {
        if (!Directory.Exists(DiskCacheFolder))
            return;

        DateTime expiration = DateTime.UtcNow - DiskCacheTtl;
        var files = new List<FileInfo>();
        long totalBytes = 0;
        foreach (string path in Directory.EnumerateFiles(DiskCacheFolder, "*.img", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var file = new FileInfo(path);
                if (file.LastWriteTimeUtc < expiration)
                {
                    file.Delete();
                    continue;
                }
                files.Add(file);
                totalBytes += file.Length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WinProvisionLog.Warning(exception, "AsyncImage.TrimDiskEntry");
            }
        }

        foreach (FileInfo file in files.OrderBy(item => item.LastAccessTimeUtc))
        {
            if (totalBytes <= MaxDiskCacheBytes)
                break;
            try
            {
                long length = file.Length;
                file.Delete();
                totalBytes -= length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                WinProvisionLog.Warning(exception, "AsyncImage.TrimDiskEntry");
            }
        }
    }

    private static long GetMemoryBudget()
    {
        GCMemoryInfo memory = GC.GetGCMemoryInfo();
        long available = memory.TotalAvailableMemoryBytes;
        long budget = available is > 0 and <= 4L * 1024 * 1024 * 1024
            ? 8L * 1024 * 1024
            : available is > 0 and <= 8L * 1024 * 1024 * 1024
                ? 16L * 1024 * 1024
                : 24L * 1024 * 1024;
        if (memory.HighMemoryLoadThresholdBytes > 0 &&
            memory.MemoryLoadBytes >= memory.HighMemoryLoadThresholdBytes * 0.9)
        {
            budget = Math.Min(budget, 2L * 1024 * 1024);
        }
        return budget;
    }

    private static void CancelImageLoad(Image image)
    {
        if (image.GetValue(LoadCancellationProperty) is CancellationTokenSource cancellation)
            cancellation.Cancel();
        image.ClearValue(LoadCancellationProperty);
    }

    private sealed partial class BitmapMemoryCache(long maxBytes)
    {
        private readonly Lock _gate = new();
        private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
        private readonly LinkedList<Entry> _leastRecentlyUsed = new();
        private long _currentBytes;
        private long _maxBytes = maxBytes;

        public bool TryGet(string key, out BitmapImage? bitmap)
        {
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out LinkedListNode<Entry>? node))
                {
                    _leastRecentlyUsed.Remove(node);
                    _leastRecentlyUsed.AddFirst(node);
                    node.Value.LastAccessUtc = DateTime.UtcNow;
                    bitmap = node.Value.Bitmap;
                    return true;
                }
            }

            bitmap = null;
            return false;
        }

        public void Set(string key, BitmapImage bitmap)
        {
            long size = Math.Max(1L, (long)bitmap.PixelWidth * bitmap.PixelHeight * 4);
            SetBudget(GetEffectiveMemoryBudget());
            lock (_gate)
            {
                if (size > _maxBytes)
                    return;
                if (_entries.Remove(key, out LinkedListNode<Entry>? existing))
                {
                    _leastRecentlyUsed.Remove(existing);
                    _currentBytes -= existing.Value.SizeBytes;
                }

                var entry = new Entry(key, bitmap, size);
                _entries[key] = _leastRecentlyUsed.AddFirst(entry);
                _currentBytes += size;
                while (_currentBytes > _maxBytes && _leastRecentlyUsed.Last is { } last)
                    Remove(last);
            }
        }

        public void SetBudget(long budget)
        {
            lock (_gate)
            {
                _maxBytes = Math.Max(0, budget);
                while (_currentBytes > _maxBytes && _leastRecentlyUsed.Last is { } last)
                    Remove(last);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _entries.Clear();
                _leastRecentlyUsed.Clear();
                _currentBytes = 0;
            }
        }

        public void TrimTo(long budget)
        {
            lock (_gate)
            {
                while (_currentBytes > budget && _leastRecentlyUsed.Last is { } last)
                    Remove(last);
            }
        }

        public (int Count, long Bytes, long BudgetBytes) TrimAndGetStatistics()
        {
            lock (_gate)
            {
                DateTime expiredBefore = DateTime.UtcNow.AddMinutes(-5);
                while (_leastRecentlyUsed.Last is { } last && last.Value.LastAccessUtc < expiredBefore)
                    Remove(last);
                return (_entries.Count, _currentBytes, _maxBytes);
            }
        }

        private void Remove(LinkedListNode<Entry> node)
        {
            _leastRecentlyUsed.Remove(node);
            _entries.Remove(node.Value.Key);
            _currentBytes -= node.Value.SizeBytes;
        }

        internal sealed partial class Entry(string key, BitmapImage bitmap, long sizeBytes)
        {
            public string Key { get; } = key;
            public BitmapImage Bitmap { get; } = bitmap;
            public long SizeBytes { get; } = sizeBytes;
            public DateTime LastAccessUtc { get; set; } = DateTime.UtcNow;
        }
    }
}
