using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.Versioning;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Elevation;

namespace WinProvision.Core.Services;

public sealed record LanInstallBatchProgress(string File, int ItemIndex, int ItemCount, string Stage, string Message);
public sealed record LanInstallItemResult(
    string File,
    int ExitCode,
    LanInstallerExecutionStage Stage,
    LanInstallerOutcome Outcome,
    string Message,
    string? ExceptionDetails = null);
public sealed record LanInstallBatchResult(IReadOnlyList<LanInstallItemResult> Items, string? Message = null);
public sealed record LanInstallBatchRequest(IReadOnlyList<LanInstallerInstallRequest> Installers);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LanInstallBatchRequest))]
[JsonSerializable(typeof(LanInstallBatchResult))]
[JsonSerializable(typeof(LanInstallBatchProgress))]
internal partial class LanInstallJsonContext : JsonSerializerContext;

public sealed class LanInstallService
{
    private const string ProgressPrefix = "LAN_INSTALL_PROGRESS:";
    private const string RequestDirectoryName = "lan-install-requests";
    private readonly ILanTransportSecurityPolicy _transport;
    private readonly IEnumerable<IClientModuleHandler> _handlers;
    private readonly string _cacheRoot;
    private readonly string? _serverRoot;
    private readonly Action<string> _diagnosticLog;
    private readonly Func<string, CancellationToken, Task<LanWorkerSelfTestResult>> _workerSelfTest;

    public LanInstallService(
        ILanTransportSecurityPolicy transport,
        IEnumerable<IClientModuleHandler> handlers,
        string cacheRoot,
        string? serverRoot = null,
        Action<string>? diagnosticLog = null,
        Func<string, CancellationToken, Task<LanWorkerSelfTestResult>>? workerSelfTest = null)
    {
        _transport = transport;
        _handlers = handlers;
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _serverRoot = serverRoot;
        _diagnosticLog = diagnosticLog ?? (message => CoreLog.Debug("LanInstall", message));
        _workerSelfTest = workerSelfTest ?? (static (path, token) => LanWorkerSelfTest.RunAsync(path, token));
    }

    public async Task<LanSyncResult> SyncAsync(
        string serverAddress,
        string token,
        IProgress<LanDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? serverRoot = null)
    {
        using HttpClient http = _transport.CreateClient();
        var client = new LanInstallerClient(http, _handlers, _cacheRoot, transport: _transport,
            serverRoot: serverRoot ?? _serverRoot);
        return await client.SynchronizeAsync(serverAddress, token, progress, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LanInstallBatchResult> InstallAsync(
        IReadOnlyList<LanInstallerInstallRequest> installers,
        IProgress<LanInstallBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("A instalação LAN elevada só está disponível no Windows.");
        ArgumentNullException.ThrowIfNull(installers);
        if (installers.Count == 0)
            throw new ArgumentException("Selecione ao menos um instalador.", nameof(installers));
        cancellationToken.ThrowIfCancellationRequested();

        string workerPath = BackgroundUpdateCommand.CliPath(AppContext.BaseDirectory);
        LanWorkerSelfTestResult selfTest = await _workerSelfTest(workerPath, cancellationToken).ConfigureAwait(false);
        if (!selfTest.Success)
        {
            string standardOutput = SensitiveDataRedactor.Redact(selfTest.StandardOutput);
            string standardError = SensitiveDataRedactor.Redact(selfTest.StandardError);
            _diagnosticLog($"LAN worker selftest failed code={selfTest.ExitCode} hex=0x{unchecked((uint)selfTest.ExitCode):X8} path=\"{workerPath}\" stdout=\"{standardOutput}\" stderr=\"{standardError}\"");
            return LanWorkerStartupFailurePolicy.CreateResult(
                installers, selfTest.ExitCode, workerPath, standardOutput, standardError);
        }

        string requestDirectory = CreateRequestDirectory();
        string requestPath = Path.Combine(requestDirectory, "request.json");
        string responsePath = Path.Combine(requestDirectory, "result.json");
        string cancelPath = Path.Combine(requestDirectory, "cancel");
        try
        {
            var request = new LanInstallBatchRequest(installers.ToArray());
            byte[] content = JsonSerializer.SerializeToUtf8Bytes(request, LanInstallJsonContext.Default.LanInstallBatchRequest);
            await WriteRestrictedRequestAsync(requestPath, content, cancellationToken).ConfigureAwait(false);

            using CancellationTokenRegistration cancellation = cancellationToken.Register(() =>
            {
                try { File.WriteAllText(cancelPath, "cancel"); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    CoreLog.Warning(exception, "LanInstall.CancelMarker");
                }
            });
            var workerOutput = new WorkerOutputCapture();
            int helperExitCode;
            try
            {
                WingetExecutionResult elevatedResult = await ElevatedProcessRunner.RunElevatedAsync(
                    workerPath, ["--lan-install-worker", requestPath], cancellationToken, ElevationCategory.LanClient,
                    affectWingetElevationState: false,
                    onLogReceived: line => ReportWorkerProgress(line, installers, progress),
                    onOutput: (stream, line) =>
                    {
                        if (line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
                            return;
                        workerOutput.Add(stream, line);
                    }).ConfigureAwait(false);
                helperExitCode = elevatedResult.ExitCode;
                if (elevatedResult.FailureReason == WingetFailureReason.ElevationCanceled) helperExitCode = 1223;
                else if (!elevatedResult.Success && !elevatedResult.InstallerStarted)
                    throw new System.ComponentModel.Win32Exception(elevatedResult.ExitCode, elevatedResult.Output);
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                LanInstallerOutcome outcome = exception.NativeErrorCode == 1223
                    ? LanInstallerOutcome.Canceled
                    : LanInstallerOutcome.Failed;
                string message = outcome == LanInstallerOutcome.Canceled
                    ? "Instalação cancelada na confirmação do WinProvision Elevator."
                    : $"Falha ao iniciar a elevação do worker (0x{exception.HResult:X8}). Etapa: início do worker. Caminho: {workerPath}. {exception.Message}";
                _diagnosticLog($"LAN worker start failed path=\"{workerPath}\" hresult=0x{exception.HResult:X8} error={SensitiveDataRedactor.Redact(exception.ToString())}");
                return new(installers.Select(item => new LanInstallItemResult(
                    item.File, exception.NativeErrorCode, LanInstallerExecutionStage.StartingWorker,
                    outcome, message, exception.ToString())).ToArray());
            }

            if (!File.Exists(responsePath))
            {
                LanInstallerOutcome outcome = helperExitCode == 1223
                    ? LanInstallerOutcome.Canceled
                    : LanInstallerOutcome.Failed;
                if (outcome == LanInstallerOutcome.Canceled)
                {
                    string message = "Instalação cancelada na confirmação do WinProvision Elevator.";
                    return new(installers.Select(item => new LanInstallItemResult(
                        item.File, helperExitCode, LanInstallerExecutionStage.RequestingElevation, outcome, message)).ToArray());
                }

                string standardOutput = workerOutput.StandardOutput;
                string standardError = workerOutput.StandardError;
                _diagnosticLog($"LAN worker exited without result code={helperExitCode} hex=0x{unchecked((uint)helperExitCode):X8} path=\"{workerPath}\" stdout=\"{standardOutput}\" stderr=\"{standardError}\"");
                return LanWorkerStartupFailurePolicy.CreateResult(
                    installers, helperExitCode, workerPath, standardOutput, standardError);
            }

            await using var stream = new FileStream(responsePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync(stream, LanInstallJsonContext.Default.LanInstallBatchResult,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("O worker elevado retornou um resultado vazio.");
        }
        finally
        {
            try { Directory.Delete(requestDirectory, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CoreLog.Warning(exception, "LanInstall.RequestCleanup");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    public static async Task<int> RunElevatedWorkerAsync(
        string requestPath,
        Action<LanInstallBatchProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!ElevationRunner.IsCurrentProcessElevated())
            throw new InvalidOperationException("O worker de instalação LAN exige autorização administrativa.");
        string fullRequestPath = Path.GetFullPath(requestPath);
        (string requestRoot, string cacheRoot) = ResolveRequestRoots(fullRequestPath);
        if (!IsDirectChildPath(fullRequestPath, Path.GetDirectoryName(fullRequestPath)!)
            || (File.GetAttributes(fullRequestPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("O arquivo temporário do worker LAN está fora da pasta segura.");
        var requestInfo = new FileInfo(fullRequestPath);
        if (requestInfo.Length is <= 0 or > 1024 * 1024)
            throw new InvalidDataException("O pedido temporário do worker LAN tem tamanho inválido.");
        VerifyRestrictedAcl(new DirectoryInfo(Path.GetDirectoryName(fullRequestPath)!));

        await using var requestStream = new FileStream(fullRequestPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        LanInstallBatchRequest request = await JsonSerializer.DeserializeAsync(
            requestStream, LanInstallJsonContext.Default.LanInstallBatchRequest, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("O pedido temporário do worker LAN está vazio.");
        if (request.Installers is null || request.Installers.Count is < 1 or > 100
            || request.Installers.Any(installer => installer is null)
            || request.Installers.Select(installer => installer.File)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Installers.Count)
            throw new InvalidDataException("O pedido precisa conter de 1 a 100 instaladores.");

        string protectedRun = CreateProtectedRunDirectory();
        var results = new List<LanInstallItemResult>(request.Installers.Count);
        try
        {
            var execution = new LanInstallerExecutionService();
            using var cancelMonitor = new CancellationTokenSource();
            Task monitor = MonitorCancellationFileAsync(
                Path.Combine(Path.GetDirectoryName(fullRequestPath)!, "cancel"),
                cancelMonitor, cancellationToken);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, cancelMonitor.Token);
                for (int index = 0; index < request.Installers.Count; index++)
                {
                    LanInstallerInstallRequest original = request.Installers[index];
                    linked.Token.ThrowIfCancellationRequested();
                    string? protectedPath = null;
                    try
                    {
                        ValidateInstallRequest(original, cacheRoot);
                        progress?.Invoke(new(original.File, index + 1, request.Installers.Count, "Cópia protegida", "Copiando e revalidando o instalador."));
                        protectedPath = await LanProtectedInstallerStager.StageAsync(
                            original, cacheRoot, protectedRun, linked.Token).ConfigureAwait(false);
                        var stagedRequest = original with { LocalPath = protectedPath };
                        var itemProgress = new InlineProgress<LanInstallerExecutionProgress>(item =>
                            progress?.Invoke(new(item.File, index + 1, request.Installers.Count, item.Stage.ToString(), item.Message)));
                        LanInstallerExecutionResult result = await execution.InstallAsync(
                            stagedRequest, itemProgress, linked.Token, runningElevated: true).ConfigureAwait(false);
                        results.Add(new(original.File, result.ExitCode, result.Stage, result.Outcome,
                            result.Message, result.ExceptionDetails));
                        progress?.Invoke(new(original.File, index + 1, request.Installers.Count,
                            result.Stage.ToString(), result.Message));
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        CoreLog.Warning(exception, $"LanInstall.Worker.{original.File}");
                        var result = new LanInstallItemResult(original.File, -1,
                            LanInstallerExecutionStage.VerifyingIntegrity, LanInstallerOutcome.Failed,
                            $"Falhou na etapa de cópia protegida/revalidação: {exception.Message} (0x{exception.HResult:X8}).",
                            exception.ToString());
                        results.Add(result);
                        progress?.Invoke(new(original.File, index + 1, request.Installers.Count,
                            result.Stage.ToString(), result.Message));
                    }
                    finally
                    {
                        if (protectedPath is not null)
                        {
                            try { File.Delete(protectedPath); }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                            {
                                CoreLog.Warning(exception, $"LanInstall.StagedCleanup.{original.File}");
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || cancelMonitor.IsCancellationRequested)
            {
                for (int index = results.Count; index < request.Installers.Count; index++)
                {
                    LanInstallerInstallRequest installer = request.Installers[index];
                    results.Add(new(installer.File, 1223, LanInstallerExecutionStage.Executing,
                        LanInstallerOutcome.Canceled, "Cancelado"));
                }
            }
            finally
            {
                cancelMonitor.Cancel();
                await monitor.ConfigureAwait(false);
            }

            string responsePath = Path.Combine(Path.GetDirectoryName(fullRequestPath)!, "result.json");
            byte[] response = JsonSerializer.SerializeToUtf8Bytes(
                new LanInstallBatchResult(results), LanInstallJsonContext.Default.LanInstallBatchResult);
            await WriteAtomicAsync(responsePath, response, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            return 0;
        }
        finally
        {
            try { Directory.Delete(protectedRun, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                CoreLog.Warning(exception, "LanInstall.ProtectedCleanup");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task WriteRestrictedRequestAsync(string path, byte[] content, CancellationToken token)
    {
        await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(content, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        SecureDirectoryAcl.Apply(new DirectoryInfo(Path.GetDirectoryName(path)!), includeCurrentUser: true);
        SecureDirectoryAcl.Apply(new FileInfo(path), includeCurrentUser: true);
    }

    [SupportedOSPlatform("windows")]
    private static string CreateRequestDirectory()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStore", "lan", RequestDirectoryName);
        Directory.CreateDirectory(root);
        string localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        EnsureNotReparsePoint(Path.Combine(localAppDataRoot, "WinProvisionStore"));
        EnsureNotReparsePoint(Path.Combine(localAppDataRoot, "WinProvisionStore", "lan"));
        EnsureNotReparsePoint(root);
        SecureDirectoryAcl.Apply(new DirectoryInfo(root), includeCurrentUser: true);
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        SecureDirectoryAcl.Apply(new DirectoryInfo(path), includeCurrentUser: true);
        return path;
    }

    [SupportedOSPlatform("windows")]
    private static string CreateProtectedRunDirectory()
    {
        string commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string parent = Path.Combine(commonData, "WinProvisionStore");
        Directory.CreateDirectory(parent);
        EnsureNotReparsePoint(parent);
        SecureDirectoryAcl.Apply(new DirectoryInfo(parent), includeCurrentUser: false);
        string root = Path.Combine(parent, "lan-run");
        Directory.CreateDirectory(root);
        EnsureNotReparsePoint(root);
        SecureDirectoryAcl.Apply(new DirectoryInfo(root), includeCurrentUser: false);
        string path = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        SecureDirectoryAcl.Apply(new DirectoryInfo(path), includeCurrentUser: false);
        return path;
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureNotReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"A pasta '{path}' não pode ser link ou ponto de redirecionamento.");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyRestrictedAcl(FileSystemInfo path)
    {
        if ((path.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("O caminho do pedido LAN não pode ser um reparse point.");
        FileSystemSecurity fileSystemSecurity = path is DirectoryInfo directory
            ? directory.GetAccessControl(AccessControlSections.Access)
            : ((FileInfo)path).GetAccessControl(AccessControlSections.Access);
        AuthorizationRuleCollection rules = fileSystemSecurity.GetAccessRules(
            includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        var owner = (SecurityIdentifier)(path is DirectoryInfo ownerDirectory
            ? ownerDirectory.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))!
            : ((FileInfo)path).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier))!);
        var allowed = new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            owner
        };
        if (WindowsIdentity.GetCurrent().User is { } currentUser)
            allowed.Add(currentUser);
        foreach (AuthorizationRule rule in rules)
            if (rule is FileSystemAccessRule fileRule
                && fileRule.AccessControlType == AccessControlType.Allow
                && fileRule.IdentityReference is SecurityIdentifier sid
                && !allowed.Contains(sid))
                throw new UnauthorizedAccessException("A pasta temporária do pedido permite acesso a outra identidade.");
    }

    private static bool IsDirectChildPath(string path, string root) =>
        string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase);

    [SupportedOSPlatform("windows")]
    private static (string RequestRoot, string CacheRoot) ResolveRequestRoots(string requestPath)
    {
        string requestDirectory = Path.GetDirectoryName(requestPath)
            ?? throw new InvalidDataException("O pedido temporário do worker LAN não tem pasta.");
        DirectoryInfo? requestRoot = Directory.GetParent(requestDirectory);
        if (!Path.GetFileName(requestPath).Equals("request.json", StringComparison.OrdinalIgnoreCase)
            || requestRoot is null || !requestRoot.Name.Equals(RequestDirectoryName, StringComparison.OrdinalIgnoreCase)
            || requestRoot.Parent is not { Name: "lan" } lanDirectory
            || lanDirectory.Parent is not { Name: "WinProvisionStore" } appDirectory
            || appDirectory.Parent is not { Name: "Local" } localDirectory
            || localDirectory.Parent is not { Name: "AppData" })
            throw new InvalidDataException("O pedido LAN deve estar na pasta LocalAppData protegida do WinProvision Store.");
        if (!Guid.TryParseExact(Path.GetFileName(requestDirectory), "N", out _))
            throw new InvalidDataException("O diretório temporário do pedido LAN não tem um identificador válido.");

        for (DirectoryInfo? current = new DirectoryInfo(requestDirectory); current is not null; current = current.Parent)
            EnsureNotReparsePoint(current.FullName);
        string localAppData = localDirectory.FullName;
        return (requestDirectory, Path.Combine(localAppData, "WinProvisionStore", "lan-cache"));
    }

    internal static void ValidateInstallRequest(LanInstallerInstallRequest request, string cacheRoot)
    {
        if (!LanInstallerValidation.IsSafeFileName(request.File)
            || !LanInstallerValidation.IsValidSha256(request.ExpectedSha256)
            || !LanInstallerValidation.IsSafeSilentArguments(request.SilentArguments)
            || request.Scope is not (null or "user" or "machine")
            || request.TimeoutSeconds is < 30 or > 21600)
            throw new InvalidDataException($"Pedido de instalação inválido para '{request.File}'.");
        string path = Path.GetFullPath(request.LocalPath);
        string prefix = cacheRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string? cacheHashDirectory = Path.GetDirectoryName(path);
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).Equals(request.File, StringComparison.OrdinalIgnoreCase)
            || cacheHashDirectory is null
            || !Path.GetFileName(cacheHashDirectory).Equals(request.ExpectedSha256, StringComparison.OrdinalIgnoreCase)
            || !LanInstallerValidation.IsSafePackageId(request.PackageId)
            || !LanInstallerValidation.IsSafeVersion(request.Version)
            || request.InstallerType is not ("nullsoft" or "inno" or "msi" or "exe" or "wix" or "burn"))
            throw new InvalidDataException($"Caminho ou metadados inválidos para '{request.File}'.");
    }

    private static async Task MonitorCancellationFileAsync(
        string cancelPath,
        CancellationTokenSource cancellation,
        CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
                if (File.Exists(cancelPath))
                {
                    cancellation.Cancel();
                    return;
                }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task WriteAtomicAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException exception) { CoreLog.Warning(exception, "LanInstall.ResponseTempCleanup"); }
        }
    }

    private static void ReportWorkerProgress(
        string line,
        IReadOnlyList<LanInstallerInstallRequest> installers,
        IProgress<LanInstallBatchProgress>? progress)
    {
        if (progress is null || !line.StartsWith(ProgressPrefix, StringComparison.Ordinal))
            return;
        try
        {
            byte[] bytes = Convert.FromBase64String(line[ProgressPrefix.Length..]);
            LanInstallBatchProgress? item = JsonSerializer.Deserialize(
                bytes, LanInstallJsonContext.Default.LanInstallBatchProgress);
            if (item is null)
                return;
            int index = installers.ToList().FindIndex(request =>
                request.File.Equals(item.File, StringComparison.OrdinalIgnoreCase));
            progress.Report(item with { ItemIndex = Math.Max(0, index + 1), ItemCount = installers.Count });
        }
        catch (FormatException exception)
        {
            CoreLog.Warning(exception, "LanInstall.ProgressDecode");
        }
    }

    public static string EncodeWorkerProgress(LanInstallBatchProgress progress)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(progress, LanInstallJsonContext.Default.LanInstallBatchProgress);
        return ProgressPrefix + Convert.ToBase64String(bytes);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class WorkerOutputCapture
    {
        private const int Limit = 8192;
        private readonly object _gate = new();
        private readonly System.Text.StringBuilder _standardOutput = new();
        private readonly System.Text.StringBuilder _standardError = new();

        public string StandardOutput { get { lock (_gate) return _standardOutput.ToString(); } }
        public string StandardError { get { lock (_gate) return _standardError.ToString(); } }

        public void Add(string stream, string line)
        {
            lock (_gate)
            {
                System.Text.StringBuilder output = stream == "stdout" ? _standardOutput : _standardError;
                output.AppendLine(SensitiveDataRedactor.Redact(line));
                if (output.Length > Limit)
                    output.Remove(0, output.Length - Limit);
            }
        }
    }
}

public static class LanProtectedInstallerStager
{
    public static async Task<string> StageAsync(
        LanInstallerInstallRequest request,
        string cacheRoot,
        string protectedRunDirectory,
        CancellationToken token = default)
    {
        LanInstallService.ValidateInstallRequest(request, Path.GetFullPath(cacheRoot));
        string source = Path.GetFullPath(request.LocalPath);
        string fullCacheRoot = Path.GetFullPath(cacheRoot);
        for (string? current = source; current is not null
             && current.StartsWith(fullCacheRoot, StringComparison.OrdinalIgnoreCase);
             current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"O caminho do instalador de origem '{current}' não pode ser link.");
            if (current.Equals(fullCacheRoot, StringComparison.OrdinalIgnoreCase))
                break;
        }
        string destination = Path.Combine(protectedRunDirectory, request.File);
        File.Copy(source, destination, overwrite: false);
        try
        {
            await using var stream = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            string actual = Convert.ToHexString(hash);
            if (!CryptographicOperations.FixedTimeEquals(
                    hash, Convert.FromHexString(request.ExpectedSha256)))
                throw new InvalidDataException(
                    $"SHA-256 divergente após cópia protegida de '{request.File}': esperado {request.ExpectedSha256}, obtido {actual}.");
        }
        catch
        {
            try { File.Delete(destination); }
            catch (IOException exception) { CoreLog.Warning(exception, $"LanInstall.StagingCleanup.{request.File}"); }
            throw;
        }

        LanInstallerDetection detection = LanInstallerTypeDetector.Detect(destination);
        if (!string.Equals(detection.ModuleType, request.InstallerType, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(destination); }
            catch (IOException exception) { CoreLog.Warning(exception, $"LanInstall.StagingCleanup.{request.File}"); }
            throw new InvalidDataException(
                $"Tipo divergente após cópia protegida de '{request.File}': esperado {request.InstallerType}, obtido {detection.ModuleType}.");
        }
        if (!LanInstallerValidation.IsSafeSilentArguments(request.SilentArguments))
        {
            try { File.Delete(destination); }
            catch (IOException exception) { CoreLog.Warning(exception, $"LanInstall.StagingCleanup.{request.File}"); }
            throw new InvalidDataException($"Argumentos silenciosos inválidos para '{request.File}'.");
        }
        return destination;
    }
}

[SupportedOSPlatform("windows")]
internal static class SecureDirectoryAcl
{
    public static void Apply(FileSystemInfo path, bool includeCurrentUser)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ACL restrita para execução LAN exige Windows.");
        FileSystemSecurity security = path is DirectoryInfo
            ? new DirectorySecurity()
            : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var allowed = new List<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null)
        };
        if (includeCurrentUser)
            allowed.Add(WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("Não foi possível identificar o proprietário do pedido LAN."));
        foreach (SecurityIdentifier sid in allowed)
        {
            FileSystemRights rights = FileSystemRights.FullControl;
            InheritanceFlags inheritance = path is DirectoryInfo
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None;
            security.AddAccessRule(new FileSystemAccessRule(
                sid, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        }
        if (path is DirectoryInfo directory)
            directory.SetAccessControl((DirectorySecurity)security);
        else
            ((FileInfo)path).SetAccessControl((FileSecurity)security);
    }
}
