using Microsoft.Win32;
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
        if (app.IsSystemComponent)
            return new WingetExecutionResult { Success = false, ExitCode = 1,
                FailureReason = WingetFailureReason.BlockedByPolicy, Output = "Componente do sistema protegido." };

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

        WingetExecutionResult result = await executor.UninstallAppAsync(
            app.Id, log, cancellationToken, source,
            string.IsNullOrWhiteSpace(app.DisplayVersion) ? null : app.DisplayVersion,
            interactive, requiresElevation).ConfigureAwait(false);
        if (result.Success || result.InstallerStarted || interactive || requiresElevation
            || result.FailureReason is WingetFailureReason.ElevationCanceled
                or WingetFailureReason.ElevationProhibited or WingetFailureReason.UserScopeElevationConflict
                or WingetFailureReason.BlockedByPolicy or WingetFailureReason.OperationCanceled
            || string.IsNullOrWhiteSpace(app.RegistryIdentity))
            return result;

        // A alternativa só pode ser usada para a mesma entrada ainda presente no registro.
        await installedApps.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var matches = installedApps.GetAllInstalledApps().Where(candidate =>
            candidate.RegistryIdentity.Equals(app.RegistryIdentity, StringComparison.OrdinalIgnoreCase)
            && candidate.DisplayName.Equals(app.DisplayName, StringComparison.OrdinalIgnoreCase)
            && candidate.Publisher.Equals(app.Publisher, StringComparison.OrdinalIgnoreCase)
            && candidate.DisplayVersion.Equals(app.DisplayVersion, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].IsSystemComponent
            || string.IsNullOrWhiteSpace(matches[0].QuietUninstallString)) return result;

        log?.Invoke("O gerenciador falhou antes de iniciar o desinstalador; tentando o comando silencioso registrado da mesma instalação.");
        WingetExecutionResult fallback = await uninstaller.RunRegisteredUninstallAsync(matches[0], cancellationToken).ConfigureAwait(false);
        log?.Invoke(fallback.Output);
        if (!fallback.Success || fallback.RebootRequired) return fallback;
        cancellationToken.ThrowIfCancellationRequested();
        if (RegistryEntryStillPresent(app.RegistryIdentity))
        {
            fallback.Success = false;
            fallback.FailureReason = WingetFailureReason.InternalError;
            fallback.Output = $"Código {fallback.ExitCode}; a entrada ainda está presente. Resultado inconclusivo.";
            log?.Invoke(fallback.Output);
        }
        return fallback;
    }

    private static bool RegistryEntryStillPresent(string identity)
    {
        string[] parts = identity.Split(':', 3);
        if (parts.Length != 3 || !Enum.TryParse(parts[0], out RegistryHive hive)
            || hive is not (RegistryHive.LocalMachine or RegistryHive.CurrentUser)
            || !(parts[1] == "shared" || Enum.TryParse(parts[1], out RegistryView _))) return true;
        RegistryView view = parts[1] == "shared" ? RegistryView.Registry64 : Enum.Parse<RegistryView>(parts[1]);
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var parent = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (parent is null) return true;
            using var key = parent.OpenSubKey(parts[2]);
            return key is not null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            CoreLog.Warning(exception, "VerifyUninstallRegistryEntry");
            return true;
        }
    }

    public async Task<InstalledAppDetail?> FindInstalledAsync(
        string id,
        string? name,
        CancellationToken cancellationToken)
    {
        await installedApps.EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        var matches = installedApps.GetAllInstalledApps().Where(app =>
            app.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrWhiteSpace(name)
                && app.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase))).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
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
