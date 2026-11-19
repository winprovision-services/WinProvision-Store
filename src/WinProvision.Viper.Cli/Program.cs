using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Viper;

return await ViperProgram.RunAsync(args);

internal static class ViperProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args is ["--help"] or ["-h"])
        {
            Console.WriteLine("""
                WinProvision Viper 0.1 — downloads HTTP independentes

                winprovision-viper download URL --output arquivo [opções]
                winprovision-viper batch lista.json [opções]

                --connections N   Partes simultâneas por arquivo: 1 a 16 (4)
                --parallel N      Arquivos simultâneos: 1 a 32 (3)
                --per-host N      Conexões totais por origem: 1 a 32 (6)
                --piece-mib N     Tamanho das partes: 1 a 64 MiB (4)
                --attempts N      Tentativas por requisição: 1 a 10 (4)
                --max-size-mib N  Tamanho máximo por arquivo em MiB (65536)
                --sha256 HASH     Hash esperado, somente em download
                --reuse-verified  Reutilizar destino existente somente se o SHA-256 corresponder
                --no-resume       Reiniciar em vez de retomar partes validadas
                --allow-http      Autorizar HTTP sem TLS, útil em redes locais
                --json            Resultado estruturado; progresso vai para stderr
                --version         Exibir versão

                Ctrl+C cancela e conserva partes retomáveis. Nenhum destino é sobrescrito.
                Lote: [{"url":"https://servidor/arquivo","destination":"C:/Downloads/arquivo","sha256":null}]
                """);
            return 0;
        }
        if (args is ["--version"]) { Console.WriteLine("WinProvision Viper 0.1.0"); return 0; }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            if (args.Length < 2 || args[0] is not ("download" or "batch")) throw new ArgumentException("Use download ou batch; consulte --help.");
            string? output = null, hash = null;
            bool json = false, reuseVerified = false;
            var options = new ViperOptions();
            for (int i = 2; i < args.Length; i++)
            {
                string flag = args[i];
                string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"Falta um valor para {flag}.");
                int Number() => int.TryParse(Value(), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : throw new ArgumentException($"Valor inválido para {flag}.");
                switch (flag)
                {
                    case "--output" or "-o": output = Value(); break;
                    case "--sha256": hash = Value(); break;
                    case "--reuse-verified": reuseVerified = true; break;
                    case "--connections": options = options with { ConnectionsPerFile = Number() }; break;
                    case "--parallel": options = options with { MaxConcurrentDownloads = Number() }; break;
                    case "--per-host": options = options with { MaxConnectionsPerHost = Number() }; break;
                    case "--piece-mib": options = options with { PieceSize = checked(Number() * 1024 * 1024) }; break;
                    case "--max-size-mib": options = options with { MaxFileSize = (long)Number() * 1024 * 1024 }; break;
                    case "--attempts": options = options with { MaxAttempts = Number() }; break;
                    case "--allow-http": options = options with { AllowHttp = true }; break;
                    case "--no-resume": options = options with { Resume = false }; break;
                    case "--json": json = true; break;
                    default: throw new ArgumentException($"Opção desconhecida: {flag}.");
                }
            }
            options.Validate();
            await using var engine = new ViperEngine(options);
            ViperResult[] results;
            if (args[0] == "download")
            {
                if (string.IsNullOrWhiteSpace(output)) throw new ArgumentException("Informe --output com o nome do arquivo.");
                if (reuseVerified && string.IsNullOrWhiteSpace(hash))
                    throw new ArgumentException("--reuse-verified exige --sha256.");
                if (!Uri.TryCreate(args[1], UriKind.Absolute, out var uri)) throw new ArgumentException("A URL é inválida.");
                var progress = new ConsoleProgress();
                results = [await engine.DownloadAsync(new(uri, Path.GetFullPath(output))
                {
                    Sha256 = hash,
                    ExistingFilePolicy = reuseVerified ? ViperExistingFilePolicy.ReuseIfVerified : ViperExistingFilePolicy.Fail
                }, progress, cancellation.Token)];
            }
            else
            {
                if (reuseVerified) throw new ArgumentException("--reuse-verified é usado apenas com download; no lote use reuseIfVerified.");
                if (output is not null || hash is not null) throw new ArgumentException("No lote, informe destino e hash em cada item do JSON.");
                if (new FileInfo(args[1]).Length > 4 * 1024 * 1024) throw new ArgumentException("A lista excede 4 MiB.");
                var jobs = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(args[1], cancellation.Token), ViperCliJson.Default.ViperJobArray)
                    ?? throw new ArgumentException("A lista está vazia ou inválida.");
                if (jobs.Length is < 1 or > 10000) throw new ArgumentException("Use entre 1 e 10.000 arquivos por lote.");
                if (jobs.Any(job => job is null || string.IsNullOrWhiteSpace(job.Url) || string.IsNullOrWhiteSpace(job.Destination)
                    || job.ReuseIfVerified && string.IsNullOrWhiteSpace(job.Sha256)))
                    throw new ArgumentException("Cada item precisa de URL e destino.");
                var requests = jobs.Select(job => new ViperRequest(new Uri(job.Url, UriKind.Absolute), Path.GetFullPath(job.Destination))
                {
                    Sha256 = job.Sha256,
                    ExistingFilePolicy = job.ReuseIfVerified ? ViperExistingFilePolicy.ReuseIfVerified : ViperExistingFilePolicy.Fail
                }).ToArray();
                if (requests.Select(job => job.Destination).Distinct(StringComparer.OrdinalIgnoreCase).Count() != requests.Length)
                    throw new ArgumentException("O lote contém destinos repetidos.");
                results = (await engine.DownloadManyAsync(requests, cancellation.Token)).ToArray();
            }
            if (json) Console.WriteLine(JsonSerializer.Serialize(results, ViperCliJson.Default.ViperResultArray));
            else foreach (var result in results) Console.WriteLine($"Concluído: {result.Destination}\n{result.Bytes:N0} bytes · SHA-256 {result.Sha256}");
            return 0;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Console.Error.WriteLine("Cancelado. Partes válidas foram preservadas para retomada."); return 130; }
        catch (Exception ex) when (ex is ArgumentException or JsonException or OverflowException or FormatException)
        { Console.Error.WriteLine("Configuração inválida. Consulte --help."); return 2; }
        catch (ViperIntegrityException) { Console.Error.WriteLine("Falha: SHA-256 divergente; o arquivo não foi publicado."); return 1; }
        catch (ViperProtocolException ex) { Console.Error.WriteLine($"Falha: {ex.Message}"); return 1; }
        catch (HttpRequestException ex) { Console.Error.WriteLine(ex.StatusCode is { } status ? $"Falha: servidor retornou HTTP {(int)status}." : "Falha de conexão com o servidor."); return 1; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        { Console.Error.WriteLine("Falha de transferência ou acesso ao destino. Verifique a conexão, o espaço e as permissões."); return 1; }
        finally { Console.CancelKeyPress -= handler; }
    }
    private sealed class ConsoleProgress : IProgress<ViperProgress>
    {
        private long _last;
        public void Report(ViperProgress value)
        {
            long now = Environment.TickCount64;
            if (value.Phase == ViperPhase.Downloading && now - Interlocked.Read(ref _last) < 1000) return;
            Interlocked.Exchange(ref _last, now);
            string phase = value.Phase switch { ViperPhase.Queued => "Na fila", ViperPhase.Probing => "Consultando",
                ViperPhase.Downloading => "Baixando", ViperPhase.Verifying => "Verificando", _ => "Concluído" };
            Console.Error.WriteLine($"{phase}: {value.DownloadedBytes:N0}/{value.TotalBytes?.ToString("N0") ?? "?"} bytes · {value.BytesPerSecond / 1048576:N1} MiB/s · {value.ActiveConnections} conexão(ões)");
        }
    }
}

internal sealed record ViperJob(string Url, string Destination, string? Sha256 = null, bool ReuseIfVerified = false);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ViperJob[]))]
[JsonSerializable(typeof(ViperResult[]))]
internal partial class ViperCliJson : JsonSerializerContext;
