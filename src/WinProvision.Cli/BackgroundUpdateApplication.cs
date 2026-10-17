using System.Security.Principal;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Cli;

public static class BackgroundUpdateApplication
{
    public static async Task<int> RunAsync(ICliPackageBackend backend, ApplicationPreferencesService preferences,
        IgnoredUpdatesService ignored, Action<string> log, Action<int>? checkedSummary = null,
        Action<int, int, int>? completedSummary = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!preferences.AutoUpdateAtStartup) return 0;
        var candidates = (await backend.UpdatesAsync(log, token))
            .Where(p => !ignored.IsIgnored(p.Id, p.AvailableVersion))
            .GroupBy(p => $"{p.Source}:{p.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToArray();
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
                if ((await backend.ExecuteAsync(false, package, log, token)).Success) succeeded++;
                else failed++;
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

    public static async Task<int> RunScheduledAsync(CancellationToken token)
    {
        // Own and release the named mutex on this thread even across asynchronous operations.
        return await Task.Run(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var gate = new Mutex(false, @"Local\WinProvision.WinUI3.BackgroundUpdates." + identity.User!.Value);
            bool acquired;
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return 0;
            try { return RunHostAsync(token).GetAwaiter().GetResult(); }
            finally { gate.ReleaseMutex(); }
        }, token);
    }

    private static async Task<int> RunHostAsync(CancellationToken token)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStoreWinUI3", "logs");
        Directory.CreateDirectory(folder);
        using var writer = new StreamWriter(Path.Combine(folder, "background-update.log"), append: true) { AutoFlush = true };
        var log = TextWriter.Synchronized(writer);
        void Log(string message) => log.WriteLine($"{DateTimeOffset.Now:O} {SensitiveDataRedactor.Redact(CliApplication.Clean(message))}");
        using var notifications = new WindowsAppNotificationService { Log = Log };
        notifications.TryRegister();
        var preferences = new ApplicationPreferencesService(persistStartup: false);
        var installation = new InstallationPreferencesService();
        var options = new PackageInstallOptionsService();
        await using var backend = new ResilientCliBackend(Log, () => installation.PreferredMethod, options.Get);
        try
        {
            return await RunAsync(backend, preferences, new(), Log, notifications.ShowBackgroundUpdateCheckSummary,
                notifications.ShowBackgroundUpdateSummary, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) { Log(ex.Message); notifications.ShowBackgroundUpdateFailure(); return 1; }
        finally { await GsudoElevationService.StopCacheAsync(); }
    }
}
