namespace WinProvision.Viper;

public sealed record ViperOptions
{
    public int MaxConcurrentDownloads { get; init; } = 3;
    public int ConnectionsPerFile { get; init; } = 4;
    public int MaxConnectionsPerHost { get; init; } = 6;
    public int MaxConnections { get; init; } = 24;
    public int PieceSize { get; init; } = 4 * 1024 * 1024;
    public int MaxAttempts { get; init; } = 4;
    public long MaxFileSize { get; init; } = 64L * 1024 * 1024 * 1024;
    public TimeSpan HeaderTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromMinutes(2);
    public bool AllowHttp { get; init; }
    public bool Resume { get; init; } = true;
    public void Validate()
    {
        if (MaxConcurrentDownloads is < 1 or > 32 || ConnectionsPerFile is < 1 or > 16
            || MaxConnectionsPerHost is < 1 or > 32 || MaxConnections is < 1 or > 128
            || PieceSize is < 65536 or > 67108864 || MaxAttempts is < 1 or > 10 || MaxFileSize < 1
            || HeaderTimeout <= TimeSpan.Zero || HeaderTimeout > TimeSpan.FromMinutes(10)
            || ReadTimeout <= TimeSpan.Zero || ReadTimeout > TimeSpan.FromMinutes(10)
            || MaxRetryDelay < TimeSpan.Zero || MaxRetryDelay > TimeSpan.FromHours(1))
            throw new ArgumentException("As opções do Viper estão fora dos limites permitidos.");
    }
}

/// <summary>Define como o motor trata um arquivo final que já existe no destino.</summary>
public enum ViperExistingFilePolicy
{
    /// <summary>Falha sem modificar o arquivo existente.</summary>
    Fail = 0,

    /// <summary>
    /// Confere o SHA-256 esperado e retorna sucesso sem abrir conexão quando o arquivo
    /// local já corresponde ao conteúdo solicitado.
    /// </summary>
    ReuseIfVerified = 1
}

public sealed record ViperRequest(Uri Source, string Destination)
{
    public string? Sha256 { get; init; }
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public ViperExistingFilePolicy ExistingFilePolicy { get; init; } = ViperExistingFilePolicy.Fail;
}

public enum ViperPhase { Queued, Probing, Downloading, Verifying, Completed }
public sealed record ViperProgress(ViperPhase Phase, long DownloadedBytes, long? TotalBytes,
    double BytesPerSecond, int ActiveConnections, int CompletedPieces = 0, int TotalPieces = 0);
public sealed record ViperResult(string Destination, long Bytes, string Sha256, bool Resumed, bool Segmented, TimeSpan Elapsed,
    bool ReusedExisting = false);
public sealed class ViperProtocolException(string message) : IOException(message);
public sealed class ViperIntegrityException(string message) : IOException(message);
