using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Globalization;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

public sealed partial class SettingsViewModel
{
    private readonly IBackgroundUpdateStartupService? _backgroundUpdates;
    public IAsyncRelayCommand SaveScheduledMaintenanceCommand => field ??= new AsyncRelayCommand(SaveScheduledMaintenanceAsync);
    public IAsyncRelayCommand RunScheduledMaintenanceCommand => field ??= new AsyncRelayCommand(RunScheduledMaintenanceAsync);
    [ObservableProperty] public partial bool ScheduledUpdatesEnabled { get; set; }
    [ObservableProperty] public partial string ScheduledUpdateMode { get; set; } = "NotifyOnly";
    [ObservableProperty] public partial string ScheduledUpdateScope { get; set; } = "AllSupportedPackages";
    [ObservableProperty] public partial string ScheduledUpdateFrequency { get; set; } = "Interval";
    [ObservableProperty] public partial string ScheduledUpdateInterval { get; set; } = "60";
    [ObservableProperty] public partial string ScheduledUpdateTime { get; set; } = "09:00";
    [ObservableProperty] public partial string ScheduledUpdateDay { get; set; } = "Monday";
    [ObservableProperty] public partial bool ScheduledUpdateNotifications { get; set; } = true;
    [ObservableProperty] public partial string ScheduledUpdateHistory { get; private set; } = "Ainda não executada.";
    [ObservableProperty] public partial string ScheduledUpdateNext { get; private set; } = "Agenda desativada.";
    [ObservableProperty] public partial bool IsMaintenanceBusy { get; private set; }
    public bool CanEditMaintenance => !IsMaintenanceBusy;
    public bool IsIntervalSchedule => ScheduledUpdateFrequency == "Interval";
    public bool IsCalendarSchedule => ScheduledUpdateFrequency is "Daily" or "Weekly";
    public bool IsWeeklySchedule => ScheduledUpdateFrequency == "Weekly";
    partial void OnIsMaintenanceBusyChanged(bool value) => OnPropertyChanged(nameof(CanEditMaintenance));
    partial void OnScheduledUpdateFrequencyChanged(string value)
    {
        OnPropertyChanged(nameof(IsIntervalSchedule));
        OnPropertyChanged(nameof(IsCalendarSchedule));
        OnPropertyChanged(nameof(IsWeeklySchedule));
    }

    public void ReloadScheduledMaintenance()
    {
        ScheduledUpdatesEnabled = _preferences.AutoUpdateAtStartup;
        ScheduledUpdateMode = _preferences.UpdateMode.ToString();
        ScheduledUpdateScope = _preferences.UpdateScope.ToString();
        ScheduledUpdateFrequency = _preferences.UpdateSchedule.ToString();
        ScheduledUpdateInterval = _preferences.UpdateIntervalMinutes.ToString(CultureInfo.InvariantCulture);
        ScheduledUpdateTime = _preferences.UpdateScheduleTime;
        ScheduledUpdateDay = _preferences.UpdateScheduleDay.ToString();
        ScheduledUpdateNotifications = _preferences.UpdateNotificationsEnabled;
        RefreshMaintenanceHistory();
    }

    private async Task SaveScheduledMaintenanceAsync(CancellationToken token)
    {
        if (IsMaintenanceBusy || _backgroundUpdates is null) return;
        if (!Enum.TryParse<BackgroundUpdateSchedule>(ScheduledUpdateFrequency, out var schedule) || !Enum.IsDefined(schedule)
            || !Enum.TryParse<BackgroundUpdateMode>(ScheduledUpdateMode, out var mode) || !Enum.IsDefined(mode)
            || !Enum.TryParse<BackgroundUpdateScope>(ScheduledUpdateScope, out var scope) || !Enum.IsDefined(scope)
            || !Enum.TryParse<DayOfWeek>(ScheduledUpdateDay, out var day) || !Enum.IsDefined(day)
            || !int.TryParse(ScheduledUpdateInterval, out int interval) || interval is < 15 or > 1440
            || !TimeOnly.TryParseExact(ScheduledUpdateTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            StatusText = "Confira as opções: intervalo de 15 minutos a 24 horas e horário no formato HH:mm.";
            return;
        }
        var old = (_preferences.UpdateMode, _preferences.UpdateScope, _preferences.UpdateSchedule,
            _preferences.UpdateScheduleTime, _preferences.UpdateScheduleDay, _preferences.UpdateIntervalMinutes,
            _preferences.UpdateNotificationsEnabled, _preferences.AutoUpdateAtStartup);
        IsMaintenanceBusy = true;
        try
        {
            token.ThrowIfCancellationRequested();
            _preferences.SetUpdateAutomationOptions(mode, scope, schedule, ScheduledUpdateTime, day, interval, ScheduledUpdateNotifications);
            var result = ScheduledUpdatesEnabled ? await _backgroundUpdates.EnableAsync() : await _backgroundUpdates.DisableAsync();
            if (!result.Success) throw new InvalidOperationException(result.Message);
            StatusText = ScheduledUpdatesEnabled ? "Manutenção agendada salva. A CLI executará sem abrir a interface." : "Agenda desativada.";
            OnPropertyChanged(nameof(LaunchAtStartup));
            RefreshMaintenanceHistory();
        }
        catch (Exception ex)
        {
            WinProvisionLog.Error(ex, "Settings.Maintenance.Save");
            try
            {
                _preferences.SetUpdateAutomationOptions(old.Item1, old.Item2, old.Item3, old.Item4, old.Item5, old.Item6, old.Item7);
                var restored = old.Item8 ? await _backgroundUpdates.EnableAsync() : await _backgroundUpdates.DisableAsync();
                if (!restored.Success) WinProvisionLog.Write($"Falha ao restaurar agenda: {restored.Message}");
            }
            catch (Exception restore) { WinProvisionLog.Error(restore, "Settings.Maintenance.Restore"); }
            ReloadScheduledMaintenance();
            StatusText = "Não foi possível salvar a agenda. Consulte os logs.";
        }
        finally { IsMaintenanceBusy = false; }
    }

    private async Task RunScheduledMaintenanceAsync()
    {
        if (IsMaintenanceBusy || _backgroundUpdates is null) return;
        IsMaintenanceBusy = true;
        try
        {
            StatusText = "Executando a configuração salva pela CLI...";
            int result = await _backgroundUpdates.RunNowAsync();
            StatusText = result switch { 0 => "Execução concluída.", 3 => "Já existe uma execução em andamento.", _ => "A execução encontrou falhas. Consulte os logs." };
            RefreshMaintenanceHistory();
        }
        catch (Exception ex) { WinProvisionLog.Error(ex, "Settings.Maintenance.Run"); StatusText = "Não foi possível executar a manutenção pela CLI."; }
        finally { IsMaintenanceBusy = false; }
    }

    private void RefreshMaintenanceHistory()
    {
        var state = BackgroundUpdateRunStore.Read();
        ScheduledUpdateHistory = state is null ? "Última execução: ainda não executada."
            : state.CompletedUtc is null ? $"Iniciada em {state.StartedUtc.ToLocalTime():g}; conclusão não registrada."
            : $"Última execução: {state.CompletedUtc.Value.ToLocalTime():g} — {(state.ExitCode == 0 ? "concluída" : state.ExitCode == 130 ? "cancelada" : "com falhas")}.";
        if (!_preferences.AutoUpdateAtStartup) { ScheduledUpdateNext = "Próxima execução: agenda desativada."; return; }
        if (_preferences.UpdateSchedule == BackgroundUpdateSchedule.AtLogon) { ScheduledUpdateNext = "Próxima execução: ao entrar no Windows."; return; }
        if (_preferences.UpdateSchedule == BackgroundUpdateSchedule.Interval)
        {
            var anchor = (_preferences.UpdateScheduleAnchorUtc ?? DateTimeOffset.UtcNow).ToLocalTime();
            anchor = anchor.AddSeconds(-anchor.Second).AddTicks(-(anchor.Ticks % TimeSpan.TicksPerSecond));
            double elapsed = Math.Max(0, (DateTimeOffset.Now - anchor).TotalMinutes);
            var nextInterval = anchor.AddMinutes((Math.Floor(elapsed / _preferences.UpdateIntervalMinutes) + 1) * _preferences.UpdateIntervalMinutes);
            ScheduledUpdateNext = $"Próxima execução prevista: {nextInterval:g} — a cada {_preferences.UpdateIntervalMinutes} minuto(s).";
            return;
        }
        var time = TimeOnly.ParseExact(_preferences.UpdateScheduleTime, "HH:mm", CultureInfo.InvariantCulture);
        DateTime next = DateTime.Today.Add(time.ToTimeSpan());
        while (next <= DateTime.Now || (_preferences.UpdateSchedule == BackgroundUpdateSchedule.Weekly && next.DayOfWeek != _preferences.UpdateScheduleDay)) next = next.AddDays(1);
        ScheduledUpdateNext = $"Próxima execução prevista: {next:g}.";
    }
}
