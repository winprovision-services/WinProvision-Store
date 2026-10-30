using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace WinProvision.Viper;

/// <summary>Independent asynchronous HTTP downloader. No dependency on Store/Core or implementações externas binaries.</summary>
public sealed class ViperEngine : IAsyncDisposable
{
    private readonly ViperOptions _options;
    private readonly ViperTransport _transport;
    private readonly SemaphoreSlim _downloads;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _lifecycle = new();
    private TaskCompletionSource _idle = CompletedSignal();
    private int _active;
    private bool _stopping;
    private Task? _dispose;
    public ViperEngine(ViperOptions? options = null)
    {
        _options = options ?? new(); _options.Validate();
        _transport = new(_options); _downloads = new(_options.MaxConcurrentDownloads);
    }
    private static TaskCompletionSource CompletedSignal()
    { var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); signal.SetResult(); return signal; }

    public async Task<ViperResult> DownloadAsync(ViperRequest request, IProgress<ViperProgress>? progress = null, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Source);
        ArgumentNullException.ThrowIfNull(request.Headers);
        _transport.ValidateUri(request.Source);
        if (!Path.IsPathFullyQualified(request.Destination)) throw new ArgumentException("Informe um destino absoluto.");
        if (request.Sha256 is { } sha && (sha.Length != 64 || sha.Any(c => !char.IsAsciiHexDigit(c))))
            throw new ArgumentException("Informe um SHA-256 com 64 caracteres hexadecimais.");
        if (!Enum.IsDefined(request.ExistingFilePolicy))
            throw new ArgumentException("A política para arquivo existente é inválida.");
        foreach (var header in request.Headers)
        {
            if (header.Key.ToLowerInvariant() is "range" or "if-range" or "host" or "content-length" or "accept-encoding" or "connection" or "transfer-encoding" or "proxy-authorization" or "user-agent"
                || string.IsNullOrEmpty(header.Key) || header.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
                || header.Value is null || header.Value.Contains('\r') || header.Value.Contains('\n'))
                throw new ArgumentException("O cabeçalho solicitado não pode ser usado no Viper.");
        }
        request = request with { Destination = Path.GetFullPath(request.Destination), Headers = new Dictionary<string, string>(request.Headers, StringComparer.OrdinalIgnoreCase) };
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            if (_active++ == 0) _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            Report(progress, new(ViperPhase.Queued, 0, null, 0, 0));
            await _downloads.WaitAsync(linked.Token).ConfigureAwait(false);
            try { return await DownloadCoreAsync(request, progress, linked.Token).ConfigureAwait(false); }
            finally { _downloads.Release(); }
        }
        finally { lock (_lifecycle) if (--_active == 0) _idle.TrySetResult(); }
    }

    public async Task<IReadOnlyList<ViperResult>> DownloadManyAsync(IEnumerable<ViperRequest> requests,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        // Collapse identical destinations before scheduling so duplicate batch entries share one transfer.
        // Conflicting identities are rejected deterministically rather than racing for the destination lock.
        var entries = requests.Select((request, index) => (request, index)).ToArray();
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var groups = entries.GroupBy(entry => Path.GetFullPath(entry.request.Destination), pathComparer).ToArray();
        foreach (var group in groups)
        {
            var first = group.First().request;
            if (group.Any(entry => entry.request.Source != first.Source
                || !string.Equals(entry.request.Sha256, first.Sha256, StringComparison.OrdinalIgnoreCase)
                || entry.request.ExistingFilePolicy != first.ExistingFilePolicy
                || !HeadersEqual(entry.request.Headers, first.Headers)))
                throw new ArgumentException("O lote contém destinos repetidos com origens ou integridades diferentes.", nameof(requests));
        }
        var results = new ViperResult[entries.Length];
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? failure = null;
        try
        {
            await Parallel.ForEachAsync(groups, new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.MaxConcurrentDownloads,
                CancellationToken = batch.Token
            }, async (group, ct) =>
            {
                try
                {
                    ViperResult result = await DownloadAsync(group.First().request, token: ct).ConfigureAwait(false);
                    foreach (var entry in group) results[entry.index] = result;
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    Interlocked.CompareExchange(ref failure, exception, null);
                    await batch.CancelAsync().ConfigureAwait(false);
                    throw;
                }
            }).ConfigureAwait(false);
        }
        catch when (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        return results;
    }

    private static bool HeadersEqual(IReadOnlyDictionary<string, string> left, IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count && left.All(entry => right.TryGetValue(entry.Key, out string? value) && value == entry.Value);

    private async Task<ViperResult> DownloadCoreAsync(ViperRequest request, IProgress<ViperProgress>? progress, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        string target = request.Destination, part = target + ".viper.part", checkpoint = target + ".viper.json";
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        foreach (string path in new[] { target, part, checkpoint, target + ".viper.lock" })
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("O destino ou os arquivos de trabalho são links de sistema.");
        using var ownership = new FileStream(target + ".viper.lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        if (Directory.Exists(target)) throw new IOException("O destino já existe como pasta; nenhum arquivo será substituído.");
        if (File.Exists(target))
        {
            if (request.ExistingFilePolicy != ViperExistingFilePolicy.ReuseIfVerified)
                throw new IOException("O destino já existe; nenhum arquivo será substituído.");
            if (request.Sha256 is null)
                throw new InvalidOperationException("Reutilizar um arquivo existente exige SHA-256 esperado.");

            long existingLength = new FileInfo(target).Length;
            if (existingLength > _options.MaxFileSize)
                throw new ViperProtocolException("O arquivo existente excede o tamanho máximo configurado.");
            string existingHash = await HashFileAsync(target, token).ConfigureAwait(false);
            if (!existingHash.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ViperIntegrityException("O SHA-256 do arquivo existente não corresponde ao esperado.");

            Report(progress, new(ViperPhase.Verifying, existingLength, existingLength,
                existingLength / Math.Max(.001, watch.Elapsed.TotalSeconds), 0));
            Report(progress, new(ViperPhase.Completed, existingLength, existingLength,
                existingLength / Math.Max(.001, watch.Elapsed.TotalSeconds), 0));
            return new(target, existingLength, existingHash, Resumed: true, Segmented: false,
                Elapsed: watch.Elapsed, ReusedExisting: true);
        }
        Report(progress, new(ViperPhase.Probing, 0, null, 0, 0));
        Probe probe = await _transport.RetryAsync(() => ProbeAsync(request, token), token).ConfigureAwait(false);
        if (probe.Length > _options.MaxFileSize) throw new ViperProtocolException("O arquivo excede o tamanho máximo configurado.");
        bool segmented = probe.Ranges && probe.ETag is not null && probe.Length >= 2L * _options.PieceSize && _options.ConnectionsPerFile > 1;
        bool resumed = false;
        try
        {
            if (segmented)
            {
                try { resumed = await DownloadSegmentsAsync(request, probe, part, checkpoint, progress, watch, token).ConfigureAwait(false); }
                catch (RangeUnavailableException)
                {
                    // All segment workers have stopped before truncating/restarting the file.
                    DeleteWork(part, checkpoint);
                    segmented = false;
                    await DownloadSingleAsync(request, probe, part, progress, watch, token).ConfigureAwait(false);
                }
            }
            else
            {
                DeleteWork(part, checkpoint);
                await DownloadSingleAsync(request, probe, part, progress, watch, token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            long length = new FileInfo(part).Length;
            Report(progress, new(ViperPhase.Verifying, length, length, length / Math.Max(.001, watch.Elapsed.TotalSeconds), 0));
            string hash = await HashFileAsync(part, token).ConfigureAwait(false);
            if (request.Sha256 is not null && !hash.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new ViperIntegrityException("O SHA-256 do arquivo não corresponde ao esperado.");
            token.ThrowIfCancellationRequested();
            File.Move(part, target, false);
            TryDelete(checkpoint);
            Report(progress, new(ViperPhase.Completed, length, length, length / Math.Max(.001, watch.Elapsed.TotalSeconds), 0));
            return new(target, length, hash, resumed, segmented, watch.Elapsed);
        }
        catch (ViperIntegrityException) { DeleteWork(part, checkpoint); throw; }
        catch
        {
            if (!segmented || !_options.Resume) DeleteWork(part, checkpoint);
            throw;
        }
    }

    private sealed record Probe(long? Length, bool Ranges, string? ETag, Uri FinalUri);
    private async Task<Probe> ProbeAsync(ViperRequest request, CancellationToken token)
    {
        using var lease = await _transport.SendAsync(request, 0, 0, null, token).ConfigureAwait(false);
        var response = lease.Response;
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && response.Content.Headers.ContentRange?.Length == 0)
            return new(0, false, StrongETag(response), lease.Uri);
        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            var range = response.Content.Headers.ContentRange;
            if (range is null || range.Unit != "bytes" || range.From != 0 || range.To != 0 || range.Length is null || range.Length <= 0)
                throw new ViperProtocolException("A sondagem retornou um intervalo inválido.");
            await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] one = new byte[2];
            int count = await ReadAsync(body, one.AsMemory(0, 1), token).ConfigureAwait(false);
            if (count != 1 || await ReadAsync(body, one.AsMemory(1, 1), token).ConfigureAwait(false) != 0)
                throw new ViperProtocolException("A sondagem retornou bytes inesperados.");
            return new(range.Length, true, StrongETag(response), lease.Uri);
        }
        if (response.StatusCode != HttpStatusCode.OK) throw new ViperProtocolException("Resposta inesperada ao consultar o arquivo.");
        return new(response.Content.Headers.ContentLength, false, StrongETag(response), lease.Uri);
    }
    // URLs assinadas (servidores de origem) mudam a query a cada requisição; a identidade do arquivo
    // vem do ETag forte e do tamanho. Sem ETag, exige o mesmo esquema/host/caminho, ignorando só a query.
    private static bool SameResource(Uri left, Uri right) =>
        left.Scheme == right.Scheme && left.IdnHost == right.IdnHost && left.Port == right.Port && left.AbsolutePath == right.AbsolutePath;
    private static string? StrongETag(HttpResponseMessage response) => response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null;

    private async Task<bool> DownloadSegmentsAsync(ViperRequest request, Probe probe, string part, string checkpoint,
        IProgress<ViperProgress>? progress, Stopwatch watch, CancellationToken token)
    {
        long length = probe.Length!.Value;
        long pieceCountLong = (length - 1) / _options.PieceSize + 1;
        if (pieceCountLong > 100000) throw new ViperProtocolException("O arquivo exige partes demais; aumente o tamanho das partes.");
        int pieceCount = (int)pieceCountLong;
        string fingerprint = ViperSessionStore.Fingerprint(request);
        ViperSession? saved = _options.Resume && File.Exists(part) ? ViperSessionStore.Read(checkpoint) : null;
        bool valid = saved is not null && saved.Format == 1 && saved.SourceFingerprint == fingerprint
            && saved.Length == length && saved.PieceSize == _options.PieceSize && saved.ETag == probe.ETag
            && saved.Completed is not null && saved.Completed.Count <= pieceCount && new FileInfo(part).Length == length;
        if (!valid) { DeleteWork(part, checkpoint); saved = new(1, fingerprint, probe.ETag!, length, _options.PieceSize, []); }
        using SafeFileHandle file = File.OpenHandle(part, valid ? FileMode.Open : FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
        if (!valid) RandomAccess.SetLength(file, length);
        var completed = new Dictionary<int, string>();
        foreach (var piece in saved!.Completed!)
        {
            token.ThrowIfCancellationRequested();
            if (piece is null || piece.Index < 0 || piece.Index >= pieceCount || piece.Sha256 is null) continue;
            int size = (int)Math.Min(_options.PieceSize, length - (long)piece.Index * _options.PieceSize);
            if (await ViperSessionStore.HashPieceAsync(file, (long)piece.Index * _options.PieceSize, size, token).ConfigureAwait(false) == piece.Sha256)
                completed[piece.Index] = piece.Sha256;
        }
        bool resumed = completed.Count > 0;
        int completedCount = completed.Count;
        long confirmed = completed.Keys.Sum(index => Math.Min(_options.PieceSize, length - (long)index * _options.PieceSize));
        long received = confirmed;
        long lastByteReport = 0;
        int active = 0;
        var checkpointGate = new SemaphoreSlim(1);
        int checkpointPending = 0;
        long lastCheckpoint = watch.ElapsedMilliseconds;
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(token);
        Exception? failure = null;
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, pieceCount).Where(index => !completed.ContainsKey(index)).ToArray(),
                new ParallelOptions { MaxDegreeOfParallelism = _options.ConnectionsPerFile, CancellationToken = workers.Token }, async (index, ct) =>
                {
                    try
                    {
                        Interlocked.Increment(ref active);
                        long start = (long)index * _options.PieceSize;
                        int size = (int)Math.Min(_options.PieceSize, length - start);
                        string hash = await _transport.RetryAsync(async () =>
                        {
                            long attemptBytes = 0;
                            try
                            {
                                return await DownloadPieceAsync(request, probe, file, start, size, count =>
                                {
                                    attemptBytes += count;
                                    long bytes = Interlocked.Add(ref received, count);
                                    long now = Environment.TickCount64;
                                    long last = Volatile.Read(ref lastByteReport);
                                    if (now - last >= 200 && Interlocked.CompareExchange(ref lastByteReport, now, last) == last)
                                        Report(progress, new(ViperPhase.Downloading, bytes, length,
                                            bytes / Math.Max(.001, watch.Elapsed.TotalSeconds), Volatile.Read(ref active), Volatile.Read(ref completedCount), pieceCount));
                                }, ct).ConfigureAwait(false);
                            }
                            catch
                            {
                                Interlocked.Add(ref received, -attemptBytes);
                                throw;
                            }
                        }, ct).ConfigureAwait(false);
                        Interlocked.Add(ref confirmed, size);
                        await checkpointGate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            completed[index] = hash;
                            int done = Interlocked.Increment(ref completedCount);
                            long bytes = Volatile.Read(ref received);
                            Report(progress, new(ViperPhase.Downloading, bytes, length, bytes / Math.Max(.001, watch.Elapsed.TotalSeconds), Volatile.Read(ref active), done, pieceCount));
                            // Amortize durable checkpoint I/O. If the process stops before a checkpoint,
                            // only uncheckpointed pieces are fetched again; published checkpoints stay sound.
                            checkpointPending++;
                            if (_options.Resume && checkpointPending >= Math.Max(2, _options.ConnectionsPerFile)
                                && (checkpointPending * (long)_options.PieceSize >= 64L * 1024 * 1024
                                    || watch.ElapsedMilliseconds - lastCheckpoint >= 2000))
                            {
                                RandomAccess.FlushToDisk(file);
                                ViperSessionStore.Save(checkpoint, saved with { Completed = completed.Select(entry => new ViperPiece(entry.Key, entry.Value)).ToList() });
                                checkpointPending = 0;
                                lastCheckpoint = watch.ElapsedMilliseconds;
                            }
                        }
                        finally { checkpointGate.Release(); }
                    }
                    catch (Exception ex)
                    {
                        if (ex is not OperationCanceledException) Interlocked.CompareExchange(ref failure, ex, null);
                        await workers.CancelAsync().ConfigureAwait(false); throw;
                    }
                    finally { Interlocked.Decrement(ref active); }
                }).ConfigureAwait(false);
        }
        catch when (failure is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw(); throw; }
        finally
        {
            await checkpointGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (_options.Resume && checkpointPending > 0)
                {
                    RandomAccess.FlushToDisk(file);
                    ViperSessionStore.Save(checkpoint, saved with { Completed = completed.Select(entry => new ViperPiece(entry.Key, entry.Value)).ToList() });
                }
            }
            finally { checkpointGate.Release(); checkpointGate.Dispose(); }
        }
        token.ThrowIfCancellationRequested();
        return resumed;
    }

    private sealed class RangeUnavailableException : Exception;
    private async Task<string> DownloadPieceAsync(ViperRequest request, Probe probe, SafeFileHandle file, long start, int size, Action<int> onBytes, CancellationToken token)
    {
        using var lease = await _transport.SendAsync(request, start, start + size - 1, probe.ETag, token, probe.FinalUri).ConfigureAwait(false);
        if (lease.Response.StatusCode == HttpStatusCode.OK) throw new RangeUnavailableException();
        var range = lease.Response.Content.Headers.ContentRange;
        if (lease.Response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != start || range.To != start + size - 1
            || range.Length != probe.Length || StrongETag(lease.Response) != probe.ETag
            || lease.Response.Content.Headers.ContentLength is { } contentLength && contentLength != size)
            throw new ViperProtocolException("O servidor mudou o arquivo ou retornou uma parte incompatível.");
        await using var stream = await lease.Response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        try
        {
            int remaining = size;
            while (remaining > 0)
            {
                int count = await ReadAsync(stream, buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("A conexão terminou antes de concluir a parte.");
                await RandomAccess.WriteAsync(file, buffer.AsMemory(0, count), start, token).ConfigureAwait(false);
                onBytes(count);
                hash.AppendData(buffer, 0, count); start += count; remaining -= count;
            }
            if (await ReadAsync(stream, buffer.AsMemory(0, 1), token).ConfigureAwait(false) != 0)
                throw new ViperProtocolException("O servidor enviou bytes além do intervalo solicitado.");
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task DownloadSingleAsync(ViperRequest request, Probe probe, string part, IProgress<ViperProgress>? progress, Stopwatch watch, CancellationToken token)
        => await _transport.RetryAsync(async () =>
        {
            using var lease = await _transport.SendAsync(request, null, null, null, token, probe.FinalUri).ConfigureAwait(false);
            if (lease.Response.StatusCode != HttpStatusCode.OK) throw new ViperProtocolException("O servidor retornou uma resposta parcial inesperada.");
            long? expected = lease.Response.Content.Headers.ContentLength;
            if (expected > _options.MaxFileSize || probe.Length is { } originalLength && expected is { } currentLength && currentLength != originalLength
                || probe.ETag is not null && StrongETag(lease.Response) != probe.ETag
                || probe.ETag is null && !SameResource(lease.Uri, probe.FinalUri))
                throw new ViperProtocolException("O arquivo mudou durante o download ou excede o limite configurado.");
            await using var body = await lease.Response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                long downloaded = 0, lastReport = 0;
                while (true)
                {
                    int count = await ReadAsync(body, buffer, token).ConfigureAwait(false);
                    if (count == 0) break;
                    downloaded += count;
                    if (downloaded > _options.MaxFileSize || expected is { } length && downloaded > length)
                        throw new ViperProtocolException("O servidor excedeu o tamanho permitido.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    long tick = Environment.TickCount64;
                    if (tick - lastReport >= 200)
                    {
                        lastReport = tick;
                        Report(progress, new(ViperPhase.Downloading, downloaded, expected, downloaded / Math.Max(.001, watch.Elapsed.TotalSeconds), 1));
                    }
                }
                if (expected is { } total && downloaded != total || probe.Length is { } probed && downloaded != probed)
                    throw new EndOfStreamException("O download terminou incompleto.");
                output.Flush(true);
                return true;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }, token).ConfigureAwait(false);

    private async ValueTask<int> ReadAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_options.ReadTimeout);
        try { return await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested) { throw new TimeoutException("A transferência ficou sem receber dados.", ex); }
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken token)
        => await ViperFileIntegrity.ComputeSha256Async(path, token).ConfigureAwait(false);
    private static void Report(IProgress<ViperProgress>? progress, ViperProgress value)
    { try { progress?.Report(value); } catch { /* An observer cannot invalidate downloaded data. */ } }
    private static void DeleteWork(string part, string checkpoint) { if (File.Exists(part)) File.Delete(part); if (File.Exists(checkpoint)) File.Delete(checkpoint); }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle) return new(_dispose ??= StopAsync());
    }
    private async Task StopAsync()
    {
        _stopping = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _idle.Task.ConfigureAwait(false);
        _transport.Dispose(); _downloads.Dispose(); _lifetime.Dispose();
    }
}
