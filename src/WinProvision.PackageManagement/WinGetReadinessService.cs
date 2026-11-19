using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

/// <summary>Provisiona o WinGet e prepara a ativação COM antes de operações que dependem dele.</summary>
internal sealed class WinGetReadinessService
{
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(30);
    private readonly Func<Action<string>?, CancellationToken, Task<WingetBootstrapResult>> _provision;
    private readonly Action<string> _diagnostic;
    private readonly Func<CancellationToken, Task<bool>> _activationCheck;
    private readonly Func<bool> _comEnabled;
    private readonly Func<Func<CancellationToken, Task<bool>>, Action<string>?, CancellationToken, Task<bool>> _ensureCom;
    private readonly Func<CancellationToken, bool, Action<string>?, Task> _probe;
    private readonly Action<string> _resetCom;
    private readonly Action<string> _forceDisableCom;
    private readonly Action _reportComSuccess;
    private readonly TimeSpan _activationTimeout;
    private readonly SemaphoreSlim _comReadinessGate = new(1, 1);
    private Task<bool>? _activationTask;
    private int _postProvisionHandled;
    private bool _comReadinessChecked;

    internal WinGetReadinessService(
        WingetBootstrapper bootstrapper,
        Func<Action<string>?, CancellationToken, Task<WingetBootstrapResult>> provision,
        Action<string>? diagnostic = null)
        : this(provision, diagnostic, CheckComActivationAsync,
            () => WinGetFactoryHelper.Mode != WinGetMode.CliOnly,
            (check, log, token) => bootstrapper.EnsureComAsync(check, log, token),
            (token, includeStore, log) => WinGetFactoryHelper.ProbeAsync(token, includeStore, log),
            ActivationTimeout,
            WinGetFactoryHelper.ResetAfterProvisioning,
            WinGetFactoryHelper.ForceDisableComForSession,
            WinGetFactoryHelper.ReportComSuccess)
    {
    }

    internal WinGetReadinessService(
        Func<Action<string>?, CancellationToken, Task<WingetBootstrapResult>> provision,
        Action<string>? diagnostic,
        Func<CancellationToken, Task<bool>> activationCheck,
        Func<bool> comEnabled,
        Func<Func<CancellationToken, Task<bool>>, Action<string>?, CancellationToken, Task<bool>> ensureCom,
        Func<CancellationToken, bool, Action<string>?, Task> probe,
        TimeSpan? activationTimeout = null,
        Action<string>? resetCom = null,
        Action<string>? forceDisableCom = null,
        Action? reportComSuccess = null)
    {
        _provision = provision;
        _diagnostic = diagnostic ?? PackageDiagnostics.Write;
        _activationCheck = activationCheck;
        _comEnabled = comEnabled;
        _ensureCom = ensureCom;
        _probe = probe;
        _activationTimeout = activationTimeout ?? ActivationTimeout;
        _resetCom = resetCom ?? WinGetFactoryHelper.ResetAfterProvisioning;
        _forceDisableCom = forceDisableCom ?? WinGetFactoryHelper.ForceDisableComForSession;
        _reportComSuccess = reportComSuccess ?? WinGetFactoryHelper.ReportComSuccess;
    }

    /// <summary>Provisiona o WinGet e aquece a COM; falhas mantêm a cadeia de fallback disponível.</summary>
    internal async Task PrepareAsync(CancellationToken cancellationToken = default,
        bool includeMicrosoftStoreCatalog = false, Action<string>? onLogReceived = null)
    {
        try
        {
            Action<string> logSink = onLogReceived ?? PackageDiagnostics.Write;
            if (!await EnsureWingetProvisionedAsync(logSink, cancellationToken).ConfigureAwait(false))
            {
                onLogReceived?.Invoke("[WinProvision] O WinGet não ficou disponível; as operações WinGet informarão a indisponibilidade da rota selecionada.");
                return;
            }
            if (!_comEnabled()) return;
            onLogReceived?.Invoke("[WinProvision] Aquecendo a API COM do WinGet para os pacotes selecionados…");
            TimeSpan deadline = includeMicrosoftStoreCatalog ? TimeSpan.FromSeconds(180) : TimeSpan.FromSeconds(90);
            await WinGetComQueryService.RunBoundedAsync(async token =>
            {
                await _probe(token, includeMicrosoftStoreCatalog, onLogReceived).ConfigureAwait(false);
                return true;
            }, deadline, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (ex is TimeoutException)
                _forceDisableCom($"timeout de aquecimento COM (0x{ex.HResult:X8}): {ex.Message}");
            PackageDiagnostics.Write($"PREPARE falhou {ex.GetType().Name}: {ex.Message}");
            onLogReceived?.Invoke($"[WinProvision] Aquecimento do WinGet não concluído; a instalação tentará a cadeia normal. Motivo: {ex.Message}");
        }
    }

    /// <summary>Garante o provisionamento quando pesquisa, preparação ou fallback CLI precisam do WinGet.</summary>
    internal async Task<bool> EnsureWingetProvisionedAsync(Action<string>? onLog, CancellationToken cancellationToken,
        bool probeCom = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WingetBootstrapResult result;
        try { result = await _provision(onLog, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            PackageDiagnostics.Write($"WINGET PROVISION exceção {ex.GetType().Name}: {ex.Message}");
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsUsable)
        {
            PackageDiagnostics.Write($"WINGET PROVISION falhou: {result.ErrorMessage}");
            onLog?.Invoke("WinGet indisponível e não foi possível provisioná-lo.");
            return false;
        }

        PackageDiagnostics.Write($"WINGET PROVISION status={result.Status} exe={WingetLocator.ExecutablePath}");
        if (result.Status == WingetBootstrapStatus.Bootstrapped && Interlocked.Exchange(ref _postProvisionHandled, 1) == 0)
            _resetCom("winget provisionado nesta sessão");

        if (probeCom && _comEnabled())
        {
            await _comReadinessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!_comReadinessChecked)
                {
                    _ = await _ensureCom(ProbeActivationBoundedAsync, onLog ?? _diagnostic, cancellationToken)
                        .ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    _comReadinessChecked = true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Uma falha de reparo não deve travar operações futuras; a rota é resolvida por operação.
                _comReadinessChecked = true;
                PackageDiagnostics.Write($"COM provisionamento falhou: {ex.Message}");
                onLog?.Invoke("Não foi possível reparar a API COM; a operação aplicará a política de rota selecionada.");
            }
            finally { _comReadinessGate.Release(); }
        }
        return true;
    }

    private async Task<bool> ProbeActivationBoundedAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _resetCom("verificando ativação COM");
        // Ativação nativa não pode ser cancelada com segurança depois de iniciada. Reutilize
        // a tarefa mesmo se o chamador cancelar ou o limite de espera expirar.
        if (_activationTask is null || _activationTask.IsCompleted)
            _activationTask = WinGetComQueryService.RunBoundedAsync(
                _activationCheck, _activationTimeout, CancellationToken.None);
        Task<bool> activation = _activationTask;
        try
        {
            bool available = await activation.WaitAsync(token).ConfigureAwait(false);
            if (available) _reportComSuccess();
            return available;
        }
        catch (TimeoutException ex)
        {
            _forceDisableCom(
                $"timeout de ativação (0x{ex.HResult:X8}) após {_activationTimeout.TotalSeconds:0}s");
            _diagnostic($"COM ativação excedeu {_activationTimeout.TotalSeconds:0}s (0x{ex.HResult:X8}); desativada para esta sessão.");
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ObserveLateActivation(activation);
            throw;
        }
    }

    private void ObserveLateActivation(Task<bool> activation) => _ = activation.ContinueWith(task =>
    {
        try
        {
            if (task.Exception is { } exception)
            {
                var failure = exception.GetBaseException();
                _diagnostic($"COM ativação tardia falhou: 0x{failure.HResult:X8} {failure.Message}");
            }
            else if (task.IsCanceled)
                _diagnostic("COM ativação tardia cancelada.");
            else
                _diagnostic($"COM ativação tardia concluída: disponível={task.Result}.");
        }
        catch { /* A observação da falha tardia não deve criar outra falha não observada. */ }
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task<bool> CheckComActivationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await new WinProvision.Providers.WinGet.Com.WinGetComClient().ProbeAsync(token).ConfigureAwait(false);
        if (result.Status == WinProvision.Providers.WinGet.Com.Contracts.WinGetComStatus.Success) return true;
        PackageDiagnostics.Write($"COM ativação indisponível: {result.Status} 0x{result.HResult:X8} {result.TechnicalMessage}");
        WinGetFactoryHelper.DisableComForSession(result);
        return false;
    }
}
