using System.Security.Principal;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Cli;

public static class BackgroundUpdateApplication
{
    private const int MaxPackageUpdateRetries = 1;
    private static readonly TimeSpan PackageUpdateRetryDelay = TimeSpan.FromSeconds(1);

    public static async Task<int> RunAsync(ICliPackageBackend backend, ApplicationPreferencesService preferences,
        IgnoredUpdatesService ignored, Action<string> log, Action<int>? checkedSummary = null,
        Action<int, int, int>? completedSummary = null, CancellationToken token = default, bool force = false,
        Action<int, string>? discovered = null)
    {
        token.ThrowIfCancellationRequested();
        if (!preferences.AutoUpdateAtStartup && !force) return 0;
        var candidates = (await backend.UpdatesAsync(log, token))
            .Where(p => !ignored.IsIgnored(p.Id, p.AvailableVersion))
            .GroupBy(p => $"{p.Source}:{p.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToArray();
        string inventory = string.Join('\n', candidates.Select(p => $"{p.Source}:{p.Id}:{p.AvailableVersion}")
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        discovered?.Invoke(candidates.Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(inventory))));
        if (preferences.UpdateMode == BackgroundUpdateMode.NotifyOnly)
        {
            checkedSummary?.Invoke(candidates.Length);
            return 0;
        }
        var packages = candidates.Where(p => preferences.UpdateScope == BackgroundUpdateScope.AllSupportedPackages
            || preferences.IsAutoUpdatePackageSelected(p.Id, p.Source)).ToArray();
        int succeeded = 0, failed = 0;
        foreach (var package in packages)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var result = await backend.ExecuteAsync(false, package, log, token);
                for (int retry = 1; ShouldRetryUpdate(result, retry, token); retry++)
                {
                    log($"A atualização de {package.Name} falhou antes de iniciar o instalador; nova tentativa {retry} de {MaxPackageUpdateRetries}.");
                    await Task.Delay(PackageUpdateRetryDelay, token);
                    result = await backend.ExecuteAsync(false, package, log, token);
                }
                if (result.Success) succeeded++;
                else
                {
                    failed++;
                    log(result.Output.StartsWith("A atualização foi executada, mas não foi confirmada.", StringComparison.Ordinal)
                        ? result.Output
                        : WingetErrorTranslator.ToMessage(result.FailureReason, "atualizar", package.Name));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex) { failed++; log(CliApplication.Clean(ex.Message)); }
            if (WinProvisionElevationState.HasFailedThisSession)
            {
                log("Autorização administrativa não concluída; lote interrompido sem novos pedidos de UAC.");
                break;
            }
        }
        completedSummary?.Invoke(succeeded, failed, candidates.Length - succeeded - failed);
        return failed == 0 ? 0 : 1;
    }

    internal static bool ShouldRetryUpdate(WingetExecutionResult result, int retry, CancellationToken token) =>
        retry <= MaxPackageUpdateRetries && !token.IsCancellationRequested
        && !WinProvisionElevationState.HasFailedThisSession && !result.Success
        && !result.InstallerStarted && !result.WasElevated
        && result.FailureReason is WingetFailureReason.DownloadError or WingetFailureReason.CatalogError;

    public static async Task<int> RunScheduledAsync(CancellationToken token, bool force = false)
    {
        // Own and release the named mutex on this thread even across asynchronous operations.
        return await Task.Run(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var gate = new Mutex(false, @"Local\WinProvision.WinUI3.BackgroundUpdates." + identity.User!.Value);
            bool acquired;
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return force ? 3 : 0;
            try { return RunHostAsync(token, force).GetAwaiter().GetResult(); }
            finally { gate.ReleaseMutex(); }
        }, token);
    }

    private static async Task<int> RunHostAsync(CancellationToken token, bool force)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStoreWinUI3", "logs");
        Directory.CreateDirectory(folder);
        using var writer = new StreamWriter(Path.Combine(folder, "background-update.log"), append: true) { AutoFlush = true };
        var log = TextWriter.Synchronized(writer);
        void Log(string message) => log.WriteLine($"{DateTimeOffset.Now:O} {SensitiveDataRedactor.Redact(CliApplication.Clean(message))}");
        var preferences = new ApplicationPreferencesService(persistStartup: false);
        if (!preferences.AutoUpdateAtStartup && !force) return 0;
        using var notifications = new WindowsAppNotificationService { Log = Log, SuppressNotifications = !preferences.UpdateNotificationsEnabled };
        if (preferences.UpdateNotificationsEnabled) notifications.TryRegister();
        var installation = new InstallationPreferencesService();
        var options = new PackageInstallOptionsService();
        await using var backend = new ResilientCliBackend(Log, () => installation.PreferredMethod, options.Get,
            () => installation.PreferredUpdateMethod);
        var previous = BackgroundUpdateRunStore.Read();
        var state = new BackgroundUpdateRunState(DateTimeOffset.UtcNow);
        void SaveState()
        {
            try { BackgroundUpdateRunStore.Write(state); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log(ex.Message); }
        }
        SaveState();
        int exitCode = 1;
        try
        {
            exitCode = await RunAsync(backend, preferences, new(), Log, available =>
            {
                state = state with { Available = available };
                if (available > 0 && (force || state.Fingerprint != previous?.Fingerprint || previous?.ExitCode != 0))
                    notifications.ShowBackgroundUpdateCheckSummary(available);
            }, (updated, failed, unselected) =>
            {
                if (updated > 0 || failed > 0) notifications.ShowBackgroundUpdateSummary(updated, failed, unselected);
            }, token, force, (available, fingerprint) => state = state with { Available = available, Fingerprint = fingerprint });
            return exitCode;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { exitCode = 130; throw; }
        catch (Exception ex) { Log(ex.Message); notifications.ShowBackgroundUpdateFailure(); return 1; }
        finally
        {
            state = state with { CompletedUtc = DateTimeOffset.UtcNow, ExitCode = exitCode };
            SaveState();
            await GsudoElevationService.StopCacheAsync();
        }
    }
}
