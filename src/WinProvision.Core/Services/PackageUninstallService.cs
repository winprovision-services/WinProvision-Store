using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public sealed class PackageUninstallService(
    WingetExecutor executor,
    UninstallerEngineService uninstaller,
    WinProvisionApiService api,
    InstalledAppsService installedApps)
{
    public async Task<WingetExecutionResult> UninstallAsync(
        InstalledAppDetail app,
        string? source,
        Action<string>? log,
        CancellationToken cancellationToken,
        bool interactive = false,
        bool requiresElevation = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (SteamUninstallService.IsGameUninstall(app.UninstallString))
        {
            bool removed = await SteamUninstallService.UninstallAsync(
                app.UninstallString,
                cancellationToken).ConfigureAwait(false);
            return Result(removed, removed ? "Remoção concluída." : "A Steam não confirmou a remoção.");
        }

        if (!requiresElevation && OperatingSystem.IsWindows()
            && !string.Equals(source, "msstore", StringComparison.OrdinalIgnoreCase))
        {
            bool? apiResult = await api.UninstallAsync(
                app,
                uninstaller,
                log,
                cancellationToken).ConfigureAwait(false);
            if (apiResult.HasValue)
                return Result(apiResult.Value, apiResult.Value
                    ? "Remoção concluída."
                    : "A API da WinProvision Store não conseguiu remover o aplicativo.");
        }

        if (!interactive && UninstallerEngineService.HasInnoSetupUninstaller(app))
        {
            bool removed = await uninstaller.RunSilentUninstallAsync(
                app,
                cancellationToken).ConfigureAwait(false);
            return Result(removed, removed
                ? "Remoção concluída."
                : "Não foi possível remover o aplicativo silenciosamente.");
        }

        WingetExecutionResult result = await executor.UninstallAppAsync(
            app.Id,
            log,
            cancellationToken,
            source,
            string.IsNullOrWhiteSpace(app.DisplayVersion) ? null : app.DisplayVersion,
            interactive,
            requiresElevation).ConfigureAwait(false);

        if (result.Success)
        {
            await uninstaller.PerformAggressiveCleanupAsync(
                app,
                cancellationToken).ConfigureAwait(false);
            return result;
        }

        if (result.FailureReason is WingetFailureReason.ElevationCanceled
            or WingetFailureReason.ElevationProhibited
            or WingetFailureReason.UserScopeElevationConflict)
            return result;

        bool aggressiveSuccess = await uninstaller.UninstallAggressivelyAsync(
            app,
            cancellationToken).ConfigureAwait(false);
        if (aggressiveSuccess)
            return Result(true, "Remoção concluída.");

        return result;
    }

    public async Task<InstalledAppDetail?> FindInstalledAsync(
        string id,
        string? name,
        CancellationToken cancellationToken)
    {
        await installedApps.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        return installedApps.GetAllInstalledApps().FirstOrDefault(app =>
            app.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(name)
                && app.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    private static WingetExecutionResult Result(bool success, string output) => new()
    {
        Success = success,
        ExitCode = success ? 0 : 1,
        Output = output,
        InstallerStarted = true,
        FailureReason = success ? WingetFailureReason.Unknown : WingetFailureReason.InternalError
    };
}
