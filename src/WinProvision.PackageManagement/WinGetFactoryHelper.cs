using WinProvision.Providers.WinGet.Com;
using WinProvision.Providers.WinGet.Com.Activation;
using WinProvision.Providers.WinGet.Com.Contracts;
using ProviderOperationResult = WinProvision.Providers.WinGet.Com.Contracts.WinGetComOperationResult;

namespace WinProvision.PackageManagement;

/// <summary>Core-side WinGet availability and fallback state. COM activation lives in the provider module.</summary>
public static class WinGetFactoryHelper
{
    private static readonly TimeSpan BreakerBaseCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BreakerMaxCooldown = TimeSpan.FromMinutes(5);
    private static long _breakerOpenUntilTicks;
    private static int _consecutiveActivationFailures;
    private static string? _disabledReason;
    // Test overrides are applied only by the validation harness. Production routes use the
    // persisted WinGetEnginePreference through WinGetRouteResolver.
    private static WinGetMode _mode = WinGetMode.Auto;

    public static WinGetMode Mode => _mode;
    public static WinGetComActivationStrategy CurrentStrategy => new WinGetComClient().ActivationStrategy;
    public static bool CliFallbackAllowed => _mode != WinGetMode.ComOnly;
    // A prior operation's COM failure must not suppress the next operation's fresh attempt.
    public static bool IsComDisabled => _mode == WinGetMode.CliOnly;
    public static string DisabledReason => _mode == WinGetMode.CliOnly
        ? "modo CLI forçado (WINPROVISION_WINGET_MODE=cli ou /winget-mode=cli)"
        : Volatile.Read(ref _disabledReason) ?? "desconhecido";

    public static void ConfigureMode(string[] args)
    {
        WinGetMode mode = ReadModeFromEnvironment();
        string overrideSource = Environment.GetEnvironmentVariable("WINPROVISION_WINGET_MODE") is { Length: > 0 }
            ? "environment" : "default";
        WinGetComActivationStrategy? strategy = null;
        foreach (var arg in args)
        {
            var separator = arg.IndexOf('=');
            if (separator <= 0) continue;
            string key = arg[..separator].TrimStart('/', '-');
            string value = arg[(separator + 1)..].Trim();
            if (key.Equals("winget-mode", StringComparison.OrdinalIgnoreCase))
            {
                mode = TryParseMode(value, out var parsed) ? parsed : WinGetMode.Auto;
                overrideSource = "command-line";
            }
            else if (key.Equals("winget-com", StringComparison.OrdinalIgnoreCase))
            {
                strategy = value.ToLowerInvariant() switch
                {
                    "packaged" => WinGetComActivationStrategy.Packaged,
                    "lowertrust" => WinGetComActivationStrategy.LowerTrust,
                    _ => null
                };
            }
        }
        _mode = mode;
        WinGetComClient.ConfigureExternalActivationStrategy(strategy);
        PackageDiagnostics.Write($"WINGET VALIDATION OVERRIDE source={overrideSource} mode={_mode} estratégia-COM={CurrentStrategy}");
    }

    public static Task ProbeAsync(CancellationToken cancellationToken = default,
        bool includeMicrosoftStoreCatalog = false, Action<string>? onLogReceived = null) =>
        Task.Run(async () =>
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var client = new WinGetComClient();
                var result = await client.ProbeAsync(cancellationToken, includeMicrosoftStoreCatalog).ConfigureAwait(false);
                if (result.Status == WinGetComStatus.Success)
                {
                    ReportComSuccess();
                    onLogReceived?.Invoke($"[WinProvision] Conexão com catálogo WinGet: disponível ({stopwatch.Elapsed.TotalSeconds:0.0}s).");
                    if (includeMicrosoftStoreCatalog)
                        onLogReceived?.Invoke("[WinProvision] Probe da fonte Microsoft Store solicitado.");
                    PackageDiagnostics.Write($"COM PROBE status=Success strategy={client.ActivationStrategy} elapsed={stopwatch.Elapsed} mode={_mode}");
                }
                else
                {
                    var error = new System.Runtime.InteropServices.COMException(result.TechnicalMessage ?? result.Status.ToString(), result.HResult);
                    DisableComForSession(error);
                    PackageDiagnostics.Write($"COM PROBE falhou strategy={client.ActivationStrategy} HRESULT=0x{result.HResult:X8} status={result.Status}");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                DisableComForSession(ex);
                PackageDiagnostics.Write($"COM PROBE falhou strategy={CurrentStrategy} HRESULT=0x{ex.HResult:X8}: {ex}");
            }
        }, cancellationToken);

    public static bool IsActivationFailure(Exception exception) => exception.HResult is
        unchecked((int)0x800706BA) or unchecked((int)0x80040154) or unchecked((int)0x80080005)
        or unchecked((int)0x80070005) or unchecked((int)0x80010108) or unchecked((int)0x80010105)
        or unchecked((int)0x800706BE) or unchecked((int)0x80073D54) or unchecked((int)0x80040155)
        or unchecked((int)0x80004002) or unchecked((int)0x800401F0);

    public static bool IsTransient(Exception exception) => exception.HResult is
        unchecked((int)0x800706BA) or unchecked((int)0x80010108) or unchecked((int)0x80010105)
        or unchecked((int)0x800706BE);

    public static void DisableComForSession(Exception exception)
    {
        if (IsActivationFailure(exception)) OpenBreaker($"0x{exception.HResult:X8} tipo={exception.GetType().FullName} mensagem=\"{exception.Message}\"");
    }

    public static void DisableComForSession(ProviderOperationResult result)
    {
        if (result.Status is WinGetComStatus.ComUnavailable or WinGetComStatus.ClassNotRegistered
            or WinGetComStatus.AccessDenied or WinGetComStatus.ApartmentError or WinGetComStatus.ActivationFailed)
            OpenBreaker($"status={result.Status} hresult=0x{result.HResult:X8} mensagem=\"{result.TechnicalMessage}\"");
    }

    public static void ForceDisableComForSession(string reason) => OpenBreaker(reason);

    private static void OpenBreaker(string reason)
    {
        int failures = Interlocked.Increment(ref _consecutiveActivationFailures);
        long cooldownTicks = Math.Min(BreakerBaseCooldown.Ticks << Math.Min(failures - 1, 4), BreakerMaxCooldown.Ticks);
        var cooldown = TimeSpan.FromTicks(cooldownTicks);
        Interlocked.Exchange(ref _breakerOpenUntilTicks, Environment.TickCount64 + (long)cooldown.TotalMilliseconds);
        Volatile.Write(ref _disabledReason, reason);
        PackageDiagnostics.Write($"COM INDISPONÍVEL: motivo={reason} falhas-consecutivas={failures} nova-tentativa-em={cooldown.TotalSeconds:0}s");
    }

    public static void ResetAfterProvisioning(string reason)
    {
        Interlocked.Exchange(ref _consecutiveActivationFailures, 0);
        Interlocked.Exchange(ref _breakerOpenUntilTicks, 0);
        Volatile.Write(ref _disabledReason, null);
        PackageDiagnostics.Write($"COM RESET: {reason}");
    }

    public static void ReportComSuccess()
    {
        bool hadFailures = Interlocked.Exchange(ref _consecutiveActivationFailures, 0) != 0;
        bool wasOpen = Interlocked.Exchange(ref _breakerOpenUntilTicks, 0) != 0;
        if (hadFailures || wasOpen) PackageDiagnostics.Write("COM RECUPERADA: ativação bem-sucedida; voltando a usar a API COM");
    }

    private static WinGetMode ReadModeFromEnvironment() =>
        TryParseMode(Environment.GetEnvironmentVariable("WINPROVISION_WINGET_MODE"), out var mode) ? mode : WinGetMode.Auto;

    private static bool TryParseMode(string? value, out WinGetMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "com": mode = WinGetMode.ComOnly; return true;
            case "cli": mode = WinGetMode.CliOnly; return true;
            case "auto": mode = WinGetMode.Auto; return true;
            default: mode = WinGetMode.Auto; return false;
        }
    }
}

public enum WinGetMode { Auto, ComOnly, CliOnly }
