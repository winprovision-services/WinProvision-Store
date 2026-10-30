using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WinProvision.Core.Services;

/// <summary>Host integration for the pinned WinProvision fork of helper de elevação.</summary>
public static class GsudoElevationService
{
    private static readonly SemaphoreSlim CacheGate = new(1, 1);
    private static bool _cacheStarted;
    public const int CacheMinutes = 5;

    /// <summary>Exit code que o helper de elevação usa para qualquer erro próprio (UAC cancelado, senha inválida, pipe, etc.).</summary>
    internal const int GsudoErrorExitCode = 999;

    // Códigos Win32 usados para falhas do helper, para que todos os chamadores que já tratam
    // Win32Exception (1223/1625) reconheçam as novas causas sem depender de outros tipos de exceção.
    public const int ErrorHelperMissing = 2;              // ERROR_FILE_NOT_FOUND
    public const int ErrorHelperUnreadable = 5;           // ERROR_ACCESS_DENIED
    public const int ErrorHelperIntegrity = 577;          // ERROR_INVALID_IMAGE_HASH
    public const int ErrorAuthorizationCanceled = 1223;   // ERROR_CANCELLED
    public const int ErrorLogonFailure = 1326;            // ERROR_LOGON_FAILURE
    public const int ErrorRequiresInteractive = 1459;     // ERROR_REQUIRES_INTERACTIVE_WINDOWSTATION
    public const int ErrorAuthorizationTimeout = 1460;    // ERROR_TIMEOUT
    public const int ErrorStartFailed = 1627;             // ERROR_FUNCTION_FAILED

    /// <summary>Tempo máximo esperando o UAC ser respondido; evita travar lotes e atualizações em segundo plano.</summary>
    public static TimeSpan AuthorizationTimeout { get; internal set; } = TimeSpan.FromMinutes(3);

    // Pontos de substituição para testes determinísticos (não usados em produção).
    internal static Func<bool> ElevationProbe { get; set; } = DefaultIsProcessElevated;
    internal static Func<bool> InteractiveProbe { get; set; } = static () => Environment.UserInteractive;
    internal static Func<ElevationPreferencesService> PreferencesFactory { get; set; } = static () => new ElevationPreferencesService();

    public static string HelperPath => Path.Combine(AppContext.BaseDirectory, "Tool", "gsudo", "WinProvision Elevator.exe");
    public static bool IsBundled => File.Exists(HelperPath);

    /// <summary>True quando o processo atual já tem token administrativo (não precisa de UAC nem do helper).</summary>
    public static bool IsProcessElevated => ElevationProbe();

    private static bool DefaultIsProcessElevated()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    internal static void ResetProbesForTests()
    {
        ElevationProbe = DefaultIsProcessElevated;
        InteractiveProbe = static () => Environment.UserInteractive;
        PreferencesFactory = static () => new ElevationPreferencesService();
        AuthorizationTimeout = TimeSpan.FromMinutes(3);
    }

    public static void VerifyHelper(string path, string expectedSha256)
    {
        using var file = File.OpenRead(path);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("O helper de elevação diverge da versão distribuída. Reinstale o WinProvision Store.");
    }

    /// <summary>
    /// Confere o helper distribuído. Leituras bloqueadas por antivírus (violação de compartilhamento)
    /// são repetidas poucas vezes antes de falhar; arquivo ausente ou hash divergente falham na hora.
    /// </summary>
    private static void VerifyBundledFile()
    {
        for (int attempt = 1; ; attempt++)
        {
            try { VerifyHelper(HelperPath, GsudoBinary.Sha256); return; }
            catch (Exception ex) when (ex is IOException and not (FileNotFoundException or DirectoryNotFoundException) && attempt < 3)
            { Thread.Sleep(100 * attempt); }
        }
    }

    /// <summary>Versão usada antes de executar: converte as falhas em <see cref="Win32Exception"/> com causa clara.</summary>
    private static void VerifyBundledHelper()
    {
        try { VerifyBundledFile(); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new Win32Exception(ErrorHelperMissing,
                "O WinProvision Elevator não foi encontrado (pode ter sido removido pelo antivírus). Reinstale o WinProvision Store.");
        }
        catch (InvalidDataException ex) { throw new Win32Exception(ErrorHelperIntegrity, ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new Win32Exception(ErrorHelperUnreadable,
                "Não foi possível ler o WinProvision Elevator (arquivo em uso ou sem permissão). Tente novamente em instantes.");
        }
    }

    public static void EnsureElevationAllowed() => PreferencesFactory().EnsureAllowed();

    private static void EnsureInteractiveDesktop()
    {
        if (!InteractiveProbe())
            throw new Win32Exception(ErrorRequiresInteractive,
                "Não há área de trabalho interativa para exibir o UAC. Execute o WinProvision em uma sessão de usuário ou como administrador.");
    }

    public static async Task<bool> IsCacheAvailableAsync(CancellationToken token)
    {
        await CacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!_cacheStarted) return false;
            var result = await RunControlAsync(["status", "CacheAvailable"], token).ConfigureAwait(false);
            return result.ExitCode == 0 && IsCacheAvailableOutput(result.Output);
        }
        finally { CacheGate.Release(); }
    }

    /// <summary>Verificação pública (interface/CLI): mantém <see cref="InvalidDataException"/> para hash divergente.</summary>
    public static void VerifyBundledIntegrity() => VerifyBundledFile();

    public static async Task AuthorizeCacheAsync(CancellationToken token)
    {
        // Já elevado: não há o que autorizar; a sessão não precisa do cache do helper.
        if (IsProcessElevated) { WinProvisionElevationState.MarkAuthorized(); return; }
        await EnsureCacheAsync(token).ConfigureAwait(false);
        WinProvisionElevationState.MarkAuthorized();
    }

    private static async Task EnsureCacheAsync(CancellationToken token)
    {
        await CacheGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureElevationAllowed();
            EnsureInteractiveDesktop();
            if (_cacheStarted)
            {
                var status = await RunControlAsync(["status", "CacheAvailable"], token).ConfigureAwait(false);
                if (status.ExitCode == 0 && IsCacheAvailableOutput(status.Output)) return;
                _cacheStarted = false;
            }

            string[] arguments = BuildCacheOnArguments();
            for (int attempt = 1; ; attempt++)
            {
                var result = await RunControlAsync(arguments, token, AuthorizationTimeout).ConfigureAwait(false);
                if (result.TimedOut)
                {
                    WinProvisionElevationState.MarkFailed();
                    throw new Win32Exception(ErrorAuthorizationTimeout,
                        "O pedido de autorização (UAC) não foi respondido a tempo. Autorize novamente nas Configurações.");
                }
                if (result.ExitCode == 0) { _cacheStarted = true; return; }

                // Recusa do usuário ou credencial inválida: não repete (evita novo UAC) e bloqueia a sessão.
                switch (ClassifyHelperOutput(result.Output))
                {
                    case HelperFailure.Canceled:
                        WinProvisionElevationState.MarkFailed();
                        throw new Win32Exception(ErrorAuthorizationCanceled, "A autorização do cache de elevação não foi concluída.");
                    case HelperFailure.BadCredentials:
                        WinProvisionElevationState.MarkFailed();
                        throw new Win32Exception(ErrorLogonFailure, "As credenciais de administrador informadas no UAC são inválidas.");
                }

                // Falha desconhecida do helper: uma nova tentativa cobre erros transitórios (pipe/serviço
                // ainda iniciando). Persistindo, interrompe o lote com a causa real em vez de "UAC recusado".
                if (attempt >= 2)
                {
                    WinProvisionElevationState.MarkFailed();
                    string detail = result.Output.Trim();
                    throw new Win32Exception(ErrorStartFailed,
                        $"O helper de elevação falhou ao autorizar a sessão (código {result.ExitCode})."
                        + (detail.Length == 0 ? string.Empty : " " + detail));
                }
                await Task.Delay(500, token).ConfigureAwait(false);
            }
        }
        finally { CacheGate.Release(); }
    }

    public static async Task StopCacheAsync()
    {
        await CacheGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_cacheStarted) return;
            // Helper removido (antivírus) ou adulterado: não há mais como controlar o cache, e o
            // upstream encerra a sessão ao fim do PID/inatividade. Não deve impedir o encerramento do app.
            if (!IsBundled) { _cacheStarted = false; return; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await RunControlAsync(BuildCacheOffArguments(), timeout.Token).ConfigureAwait(false);
            if (result.ExitCode != 0)
                CoreLog.Warning(new IOException($"gsudo cache off retornou {result.ExitCode}."), "GsudoCacheStop");
            _cacheStarted = false;
        }
        finally { CacheGate.Release(); }
    }

    public static async Task<int> RunElevatedAsync(string fileName, string arguments, CancellationToken token,
        Action? onStarted = null, Action<string>? onLog = null, string? workingDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        if (fileName.Contains('"') || fileName.Any(char.IsControl)) throw new ArgumentException("Executável inválido.", nameof(fileName));
        if (WinProvisionElevationState.HasFailedThisSession)
            throw new Win32Exception(1223, "A elevação anterior não foi concluída. Autorize a sessão novamente nas Configurações ou inicie uma nova execução da CLI.");

        // Processo já administrativo (ex.: /auto no primeiro logon, execução como administrador):
        // não há UAC a pedir. Executa direto, sem depender do helper, do cache ou da política do Elevator.
        if (IsProcessElevated)
            return await RunDirectAsync(fileName, arguments, token, onStarted, onLog, workingDirectory).ConfigureAwait(false);

        EnsureElevationAllowed();
        EnsureInteractiveDesktop();
        VerifyBundledHelper();
        await EnsureCacheAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        EnsureElevationAllowed();
        var start = CreateStartInfo();
        start.WorkingDirectory = ResolveWorkingDirectory(fileName, workingDirectory);
        // Existing installer/gerenciador de pacotes builders supply a Windows command line. --direct
        // avoids interpreting it through PowerShell or cmd.exe.
        start.Arguments = $"--direct --loglevel Error \"{fileName}\" {arguments}";
        using var process = Process.Start(start)
            ?? throw new Win32Exception(ErrorStartFailed, "Não foi possível iniciar o helper de elevação.");
        onStarted?.Invoke();
        var tail = new OutputTail();
        Task stdout = DrainAsync(process.StandardOutput, onLog, tail);
        Task stderr = DrainAsync(process.StandardError, onLog, tail);
        // Once dispatched, observe the real exit. Never cancel the wait and then
        // delete a running installer's files or dispatch a second backend.
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        int exitCode = process.ExitCode;
        // O GSudo reserva 999 para falhas ao elevar. Um processo elevado que retorne 999
        // não deve ser confundido com uma execução normal.
        if (exitCode == GsudoErrorExitCode)
        {
            switch (ClassifyHelperOutput(tail.ToString()))
            {
                case HelperFailure.Canceled:
                    WinProvisionElevationState.MarkFailed();
                    throw new Win32Exception(ErrorAuthorizationCanceled, "A elevação foi cancelada no UAC.");
                case HelperFailure.BadCredentials:
                    WinProvisionElevationState.MarkFailed();
                    throw new Win32Exception(ErrorLogonFailure, "As credenciais de administrador informadas no UAC são inválidas.");
            }
            WinProvisionElevationState.MarkFailed();
            throw new Win32Exception(ErrorStartFailed,
                "O GSudo não conseguiu iniciar o processo elevado."
                + (tail.ToString().Trim() is { Length: > 0 } detail ? " " + detail : string.Empty));
        }
        return exitCode;
    }

    /// <summary>Executa sem o helper, para processos que já são administrativos.</summary>
    private static async Task<int> RunDirectAsync(string fileName, string arguments, CancellationToken token,
        Action? onStarted, Action<string>? onLog, string? workingDirectory)
    {
        var start = new ProcessStartInfo(fileName)
        {
            Arguments = arguments, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = ResolveWorkingDirectory(fileName, workingDirectory)
        };
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start)
            ?? throw new Win32Exception(ErrorStartFailed, "Não foi possível iniciar o processo.");
        onStarted?.Invoke();
        var tail = new OutputTail();
        Task stdout = DrainAsync(process.StandardOutput, onLog, tail);
        Task stderr = DrainAsync(process.StandardError, onLog, tail);
        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        return process.ExitCode;
    }

    /// <summary>
    /// Diretório de trabalho seguro: o pedido, o do executável ou o atual, desde que exista e não seja
    /// uma pasta protegida de pacotes (WindowsApps). Caso contrário usa System32, sempre acessível.
    /// </summary>
    internal static string ResolveWorkingDirectory(string fileName, string? requested)
    {
        string? candidate = requested;
        if (string.IsNullOrWhiteSpace(candidate))
            candidate = Path.IsPathFullyQualified(fileName) ? Path.GetDirectoryName(fileName) : Environment.CurrentDirectory;
        try
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)
                && !candidate.Contains(@"\WindowsApps", StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        return Environment.SystemDirectory;
    }

    internal enum HelperFailure { None, Canceled, BadCredentials }

    internal static string[] BuildCacheOnArguments() => ["cache", "on"];

    internal static string[] BuildCacheOffArguments() => ["cache", "off"];

    internal static bool IsCacheAvailableOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        string normalized = output.Trim();
        if (normalized.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        return normalized.Contains("\"CacheAvailable\":true", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("CacheAvailable: true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reconhece as mensagens de erro do helper de elevação (idioma fixo do upstream, no formato "Error: ...").</summary>
    internal static HelperFailure ClassifyHelperOutput(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return HelperFailure.None;
        if (output.Contains("canceled by the user", StringComparison.OrdinalIgnoreCase)
            || output.Contains("cancelled by the user", StringComparison.OrdinalIgnoreCase))
            return HelperFailure.Canceled;
        if (output.Contains("user name or password is incorrect", StringComparison.OrdinalIgnoreCase))
            return HelperFailure.BadCredentials;
        return HelperFailure.None;
    }

    /// <summary>Guarda apenas o final da saída (limitado) para classificar falhas sem crescer sem controle.</summary>
    private sealed class OutputTail
    {
        private const int Limit = 8192;
        private readonly StringBuilder _text = new();
        public void Add(string line)
        {
            lock (_text)
            {
                _text.AppendLine(line);
                if (_text.Length > Limit) _text.Remove(0, _text.Length - Limit);
            }
        }
        public override string ToString() { lock (_text) return _text.ToString(); }
    }

    private static async Task DrainAsync(StreamReader reader, Action<string>? log, OutputTail tail)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            string redacted = SensitiveDataRedactor.Redact(line);
            tail.Add(redacted);
            try { log?.Invoke(redacted); }
            catch (Exception ex) { CoreLog.Warning(ex, "GsudoOutputCallback"); }
        }
    }

    private static ProcessStartInfo CreateStartInfo() => new(HelperPath)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
    };

    private readonly record struct ControlResult(int ExitCode, string Output, bool TimedOut = false);

    private static async Task<ControlResult> RunControlAsync(string[] arguments, CancellationToken token, TimeSpan? timeout = null)
    {
        VerifyBundledHelper();
        var start = CreateStartInfo();
        start.ArgumentList.Add("--loglevel"); start.ArgumentList.Add("Error");
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)
            ?? throw new Win32Exception(ErrorStartFailed, "Não foi possível iniciar o helper de elevação.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (timeout is { } limit) wait.CancelAfter(limit);
        try { await process.WaitForExitAsync(wait.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            bool timedOut = !token.IsCancellationRequested;
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            // Espera curta e limitada: o encerramento não pode depender de um filho que não morre.
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (!timedOut)
            {
                if (process.HasExited) await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                throw;
            }
            return new ControlResult(-1, string.Empty, TimedOut: true);
        }
        return new ControlResult(process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }
}
