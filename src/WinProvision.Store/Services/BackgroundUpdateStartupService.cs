using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Extensions.Logging;
using System.IO;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed partial class BackgroundUpdateStartupService(
    ApplicationPreferencesService preferences, Microsoft.Extensions.Logging.ILogger<BackgroundUpdateStartupService> logger) : IBackgroundUpdateStartupService, IDisposable
{
    private readonly SemaphoreSlim _scheduleGate = new(1, 1);
    public void Dispose() => _scheduleGate.Dispose();
    private const string LegacyTaskName = @"\WinProvisionStoreWinUI3\AutoUpdate";
    private const string ScheduledTaskName = @"\WinProvisionStoreWinUI3BackgroundUpdate";
    private const string LegacyScriptPath = @"WinProvisionStoreWinUI3\AutoUpdate.vbs";

    public bool IsEnabled => preferences.AutoUpdateAtStartup;

    public async Task<string?> MigrateLegacyTaskAsync(CancellationToken cancellationToken = default)
    {
        await _scheduleGate.WaitAsync(cancellationToken);
        try
        {
            if (!await LegacyTaskExistsAsync(cancellationToken)) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var result = await ChangeEnabledCoreAsync(true);
            return result.Success
                ? "A configuração antiga foi migrada para execução em segundo plano."
                : $"A configuração antiga foi mantida: {result.Message}";
        }
        finally { _scheduleGate.Release(); }
    }

    public Task<(bool Success, string? Message)> EnableAsync(CancellationToken cancellationToken = default) =>
        ChangeEnabledAsync(true, cancellationToken);

    public Task<(bool Success, string? Message)> DisableAsync(CancellationToken cancellationToken = default) =>
        ChangeEnabledAsync(false, cancellationToken);

    private async Task<(bool Success, string? Message)> ChangeEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _scheduleGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Commit is short and must be observed through completion, including compensation.
            return await ChangeEnabledCoreAsync(enabled);
        }
        finally { _scheduleGate.Release(); }
    }

    private async Task<(bool Success, string? Message)> ChangeEnabledCoreAsync(bool enabled)
    {
        bool previous = preferences.AutoUpdateAtStartup;
        try
        {
            var schedule = await ApplyScheduleCoreAsync(enabled || preferences.AutoMaintenanceUpdates, CancellationToken.None);
            if (!schedule.Success)
                throw new InvalidOperationException(schedule.Message ?? "Falha ao aplicar a agenda.");
            preferences.SetAutoUpdateAtStartup(enabled);
            string? cleanup = await RemoveLegacyTaskAsync(CancellationToken.None);
            return (true, cleanup is null ? null : $"Preferência aplicada; limpeza da tarefa antiga pendente: {cleanup}");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falha ao alterar agenda para {Enabled}", enabled);
            try
            {
                preferences.SetAutoUpdateAtStartup(previous);
                var restore = await ApplyScheduleCoreAsync(previous || preferences.AutoMaintenanceUpdates, CancellationToken.None);
                if (!restore.Success)
                    logger.LogError("Não foi possível reconciliar a agenda: {Reason}", restore.Message);
            }
            catch (Exception restoreException)
            {
                logger.LogError(restoreException, "Falha na compensação da agenda");
            }
            return (false, exception.Message);
        }
    }

    public async Task<(bool Success, string? Message)> ApplyConfiguredScheduleAsync(CancellationToken cancellationToken = default)
    {
        await _scheduleGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await ApplyScheduleCoreAsync(preferences.BackgroundUpdatesEnabled, CancellationToken.None);
        }
        finally { _scheduleGate.Release(); }
    }

    public async Task<(bool Success, string? Message)> SetMaintenanceAutomationAsync(
        bool enabled, bool includeDrivers, CancellationToken token = default)
    {
        await _scheduleGate.WaitAsync(token);
        bool previousEnabled = preferences.AutoMaintenanceUpdates;
        bool previousDrivers = preferences.AutoMaintenanceDrivers;
        try
        {
            token.ThrowIfCancellationRequested();
            preferences.SetMaintenanceAutomation(enabled, includeDrivers);
            var result = await ApplyScheduleCoreAsync(preferences.BackgroundUpdatesEnabled, CancellationToken.None);
            if (!result.Success) throw new InvalidOperationException(result.Message ?? "Não foi possível aplicar a agenda.");
            return (true, null);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogError(exception, "Falha ao alterar a automação de manutenção");
            try
            {
                preferences.SetMaintenanceAutomation(previousEnabled, previousDrivers);
                var restore = await ApplyScheduleCoreAsync(preferences.BackgroundUpdatesEnabled, CancellationToken.None);
                if (!restore.Success) logger.LogError("Falha ao restaurar agenda de manutenção: {Reason}", restore.Message);
            }
            catch (Exception restoreException) { logger.LogError(restoreException, "Falha ao restaurar automação de manutenção"); }
            return (false, exception.Message);
        }
        finally { _scheduleGate.Release(); }
    }

    private async Task<(bool Success, string? Message)> ApplyScheduleCoreAsync(bool enabled, CancellationToken cancellationToken)
    {
        if (!enabled || preferences.UpdateSchedule == BackgroundUpdateSchedule.AtLogon)
        {
            string? error = await RemoveScheduledTaskAsync(cancellationToken);
            return error is null ? (true, null) : (false, error);
        }

        string command = BackgroundUpdateCommand.ForDirectory(AppContext.BaseDirectory);
        string scheduleType = preferences.UpdateSchedule switch
        {
            BackgroundUpdateSchedule.Interval => preferences.UpdateIntervalMinutes == 1440 ? "DAILY" : "MINUTE",
            BackgroundUpdateSchedule.Daily => "DAILY",
            _ => "WEEKLY"
        };
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in new[]
        {
            "/Create", "/TN", ScheduledTaskName, "/SC", scheduleType,
            "/ST", preferences.UpdateSchedule == BackgroundUpdateSchedule.Interval ? DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : preferences.UpdateScheduleTime,
            "/TR", command, "/IT", "/F"
        })
            startInfo.ArgumentList.Add(argument);
        if (preferences.UpdateSchedule == BackgroundUpdateSchedule.Interval)
        {
            startInfo.ArgumentList.Add("/MO");
            startInfo.ArgumentList.Add((preferences.UpdateIntervalMinutes == 1440 ? 1 : preferences.UpdateIntervalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (preferences.UpdateSchedule == BackgroundUpdateSchedule.Weekly)
        {
            startInfo.ArgumentList.Add("/D");
            startInfo.ArgumentList.Add(ToSchtasksDay(preferences.UpdateScheduleDay));
        }
        var result = await RunSchtasksAsync(startInfo, cancellationToken);
        return result.ExitCode == 0
            ? (true, null)
            : (false, string.IsNullOrWhiteSpace(result.Output)
                ? $"schtasks.exe retornou código {result.ExitCode}." : result.Output.Trim());
    }

    public async Task<int> RunNowAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(BackgroundUpdateCommand.CliPath(AppContext.BaseDirectory))
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        start.ArgumentList.Add("--background-update-now");
        using var process = Process.Start(start) ?? throw new IOException("Não foi possível iniciar a CLI.");
        // The child owns its operation and cancellation; never abandon an installer mid-flight.
        await process.WaitForExitAsync().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return process.ExitCode;
    }

    private static string ToSchtasksDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => "SUN",
        DayOfWeek.Monday => "MON",
        DayOfWeek.Tuesday => "TUE",
        DayOfWeek.Wednesday => "WED",
        DayOfWeek.Thursday => "THU",
        DayOfWeek.Friday => "FRI",
        DayOfWeek.Saturday => "SAT",
        _ => "MON"
    };

    private async Task<string?> RemoveScheduledTaskAsync(CancellationToken cancellationToken)
    {
        var query = await RunSchtasksAsync($"/query /tn \"{ScheduledTaskName}\"", cancellationToken).ConfigureAwait(false);
        if (query.ExitCode != 0)
            return null;

        var result = await RunSchtasksAsync($"/delete /tn \"{ScheduledTaskName}\" /f", cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0
            ? null
            : string.IsNullOrWhiteSpace(result.Output) ? $"schtasks.exe retornou código {result.ExitCode}." : result.Output.Trim();
    }

    private async Task<bool> LegacyTaskExistsAsync(CancellationToken cancellationToken)
    {
        var result = await RunSchtasksAsync($"/query /tn \"{LegacyTaskName}\"", cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private async Task<string?> RemoveLegacyTaskAsync(CancellationToken cancellationToken)
    {
        if (!await LegacyTaskExistsAsync(cancellationToken).ConfigureAwait(false))
            return null;

        var result = await RunSchtasksAsync($"/delete /tn \"{LegacyTaskName}\" /f", cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            // A tarefa antiga podia ter sido criada com privilégios elevados. Tenta removê-la
            // no contexto atual primeiro; só solicita UAC se o Windows negar essa remoção.
            var elevated = await ElevatedProcessRunner.RunElevatedAsync(
                "schtasks.exe",
                $"/delete /tn {LegacyTaskName} /f",
                cancellationToken).ConfigureAwait(false);
            if (!elevated.Success)
                return string.IsNullOrWhiteSpace(elevated.Output)
                    ? string.IsNullOrWhiteSpace(result.Output) ? $"código {result.ExitCode}" : result.Output.Trim()
                    : elevated.Output.Trim();
        }

        try
        {
            string scriptPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), LegacyScriptPath);
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Não foi possível remover o script antigo {ScriptPath}", LegacyScriptPath);
        }

        return null;
    }

    private async Task<(int ExitCode, string Output)> RunSchtasksAsync(string arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        return await RunSchtasksAsync(startInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(int ExitCode, string Output)> RunSchtasksAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Não foi possível iniciar schtasks.exe.");
            // Observe both pipe readers even when waiting for the process is canceled.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            Task<string[]> output = Task.WhenAll(stdout, stderr);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
                {
                    logger.LogWarning(exception, "Não foi possível interromper schtasks.exe; aguardando sua saída");
                }
                await process.WaitForExitAsync().ConfigureAwait(false);
                throw;
            }
            finally
            {
                await output.ConfigureAwait(false);
            }
            return (process.ExitCode, string.Concat(await output.ConfigureAwait(false)));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            logger.LogError(exception, "Falha ao executar schtasks.exe");
            return (-1, exception.Message);
        }
    }
}
