using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ElevationSettingOption : ObservableObject
{
    public ElevationSettingDefinition Definition { get; }
    public string Title { get; }
    public string Description { get; }
    public string? RiskWarning { get; }
    public string? LockedReason { get; }
    public bool ShowToggle { get; private set; }
    public bool IsToggleEnabled { get; private set; }
    public bool IsDimmed { get; private set; }
    public bool ShowRiskWarning { get; private set; }
    public bool HasLockedReason => !string.IsNullOrWhiteSpace(LockedReason);
    public double CardOpacity => IsDimmed ? 0.55 : 1.0;
    public string StateText => IsEnabled ? "Habilitado" : "Desativado";

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    public ElevationSettingOption(
        ElevationSettingDefinition definition,
        string title,
        string description,
        string? riskWarning,
        string? lockedReason,
        bool value,
        IReadOnlyDictionary<string, bool> values,
        IReadOnlyList<ElevationSettingDefinition>? settings = null)
    {
        Definition = definition;
        Title = title;
        Description = description;
        RiskWarning = riskWarning;
        LockedReason = lockedReason;
        IsEnabled = value;
        UpdatePresentation(values, settings);
    }

    partial void OnIsEnabledChanged(bool value) => OnPropertyChanged(nameof(StateText));

    public void UpdatePresentation(
        IReadOnlyDictionary<string, bool> values,
        IReadOnlyList<ElevationSettingDefinition>? settings = null)
    {
        ElevationSettingPresentation presentation = ElevationSettingsCatalog.ResolvePresentation(
            Definition, values, settings: settings);
        ShowToggle = presentation.ShowToggle;
        IsToggleEnabled = presentation.IsEnabled;
        IsDimmed = presentation.IsDimmed;
        ShowRiskWarning = presentation.ShowRiskWarning;
        OnPropertyChanged(nameof(ShowToggle));
        OnPropertyChanged(nameof(IsToggleEnabled));
        OnPropertyChanged(nameof(IsDimmed));
        OnPropertyChanged(nameof(ShowRiskWarning));
        OnPropertyChanged(nameof(CardOpacity));
    }
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class ElevationSettingGroupOption(
    string title,
    IReadOnlyList<ElevationSettingOption> settings)
{
    public string Title { get; } = title;
    public IReadOnlyList<ElevationSettingOption> Settings { get; } = settings;
}

internal sealed record ElevationPreferenceChangedLogData(
    [property: JsonPropertyName("settingName")] string Key,
    [property: JsonPropertyName("oldValue")] bool OldValue,
    [property: JsonPropertyName("newValue")] bool NewValue,
    [property: JsonPropertyName("origin")] string Origin);

public sealed partial class SettingsViewModel
{
    private readonly IElevatorSettingsService _elevator;
    private readonly ElevationSettingsCatalogService _elevationCatalog = new();
    private ElevationPreferencesService _elevationPreferences = null!;
    private NoticeCatalogService _noticeCatalog = null!;
    private NoticeDismissalStore _noticeDismissals = null!;

    [ObservableProperty] public partial string ElevationCacheMessage { get; private set; } = "Consulte o estado da autorização desta sessão.";
    [ObservableProperty] public partial string ElevatorIntegrityMessage { get; private set; } = "Integridade ainda não verificada.";
    [ObservableProperty] public partial string ElevatorDetailsMessage { get; private set; } = string.Empty;
    [ObservableProperty] public partial bool IsElevationBusy { get; private set; }
    public IReadOnlyList<ElevationSettingGroupOption> ElevationSettingGroups { get; private set; } = [];
    public string ElevationAuthorizationDescription => _elevationCatalog.ResolveText("authorization.description");
    public bool CanEditElevator => !IsElevationBusy;
    public IAsyncRelayCommand AuthorizeElevationCommand => field ??= new AsyncRelayCommand(AuthorizeElevationAsync, () => !IsElevationBusy);
    public IAsyncRelayCommand RefreshElevatorCommand => field ??= new AsyncRelayCommand(RefreshElevatorAsync, () => !IsElevationBusy);
    public IAsyncRelayCommand RevokeElevationCommand => field ??= new AsyncRelayCommand(RevokeElevationAsync, () => !IsElevationBusy);

    partial void OnIsElevationBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditElevator));
        AuthorizeElevationCommand.NotifyCanExecuteChanged();
        RefreshElevatorCommand.NotifyCanExecuteChanged();
        RevokeElevationCommand.NotifyCanExecuteChanged();
    }

    public void LoadElevatorOptions()
    {
        _elevationPreferences = new ElevationPreferencesService(_elevationPreferences.FilePath);
        var values = GetElevationPreferenceValues();
        ElevationSettingGroups = _elevationCatalog.Catalog.GetGroups("elevator")
            .Select(group =>
            {
                ElevationSettingOption[] options = _elevationCatalog.Catalog.GetSettings(group.Key)
                    .Select(definition => new ElevationSettingOption(
                        definition,
                        _elevationCatalog.ResolveText(definition.TitleTextKey),
                        _elevationCatalog.ResolveText(definition.DescriptionTextKey),
                        definition.RiskWarningTextKey is null ? null : _elevationCatalog.ResolveText(definition.RiskWarningTextKey),
                        definition.LockedReasonTextKey is null ? null : _elevationCatalog.ResolveText(definition.LockedReasonTextKey),
                        definition.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase)
                            && values.TryGetValue(definition.Key, out bool value)
                            && value,
                        values,
                        _elevationCatalog.Catalog.Settings))
                    .ToArray();
                return new ElevationSettingGroupOption(
                    _elevationCatalog.ResolveText(group.TitleTextKey),
                    options);
            })
            .ToArray();
        OnPropertyChanged(nameof(ElevationSettingGroups));
        RefreshNoticeOptions();
    }

    public void RefreshNoticeOptions()
    {
        IReadOnlyList<ElevationSettingDefinition> settings = _elevationCatalog.Catalog.Settings;
        IReadOnlyDictionary<string, bool> values = GetElevationPreferenceValues();
        ReplaceNotices(ElevatorNotices, "page-top", "Elevator", settings, values);
        ReplaceNotices(LanServerNotices, "group-top", "lan-server", settings, values);
        ReplaceNotices(LanCredentialNotices, "group-top", "lan-credentials", settings, values);
    }

    public void DismissNotice(NoticeOption option)
    {
        ArgumentNullException.ThrowIfNull(option);
        try
        {
            _noticeDismissals.Dismiss(option.Definition);
            RefreshNoticeOptions();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or System.Text.Json.JsonException or NotSupportedException or ArgumentException)
        {
            WinProvisionLog.Warning(exception, "Settings.NoticeDismissal");
        }
    }

    private void ReplaceNotices(
        System.Collections.ObjectModel.ObservableCollection<NoticeOption> target,
        string placement,
        string anchor,
        IReadOnlyList<ElevationSettingDefinition> settings,
        IReadOnlyDictionary<string, bool> values)
    {
        target.Clear();
        foreach (NoticeDefinition definition in _noticeCatalog.Catalog.GetApplicable(
                     placement, anchor, settings, values, _noticeDismissals))
        {
            target.Add(new NoticeOption(
                definition,
                _noticeCatalog.ResolveText(definition.TitleKey),
                _noticeCatalog.ResolveText(definition.MessageKey),
                definition.SecondaryKey is null ? null : _noticeCatalog.ResolveText(definition.SecondaryKey),
                DismissNotice));
        }
    }

    public async Task ChangeElevationPreferenceAsync(ElevationSettingOption option, bool value)
    {
        ArgumentNullException.ThrowIfNull(option);
        if (IsElevationBusy || !option.ShowToggle || !option.IsToggleEnabled || option.IsEnabled == value)
            return;

        bool previous = option.IsEnabled;
        if (value && option.Definition.RequiresConfirmation)
        {
            bool confirmed = await _presentation.ConfirmAsync(
                option.Title,
                option.RiskWarning ?? option.Description,
                "Continuar");
            if (!confirmed)
            {
                option.IsEnabled = previous;
                return;
            }
        }

        IsElevationBusy = true;
        try
        {
            await Task.Run(() =>
            {
                _elevator.SetPreferenceAsync(option.Definition.Key, value).GetAwaiter().GetResult();
            });
            _elevationPreferences = new ElevationPreferencesService(_elevationPreferences.FilePath);
            if (!_elevator.IsAllowed)
                await _elevator.RevokeAsync();
            option.IsEnabled = value;
            _appLog.Write(AppLogLevel.Debug, "Security", "Settings.Elevation",
                "Preferência de segurança alterada.", eventName: "PreferenceChanged",
                data: JsonSerializer.SerializeToElement(
                    new ElevationPreferenceChangedLogData(option.Definition.Key, previous, value, "UI"),
                    UiJsonContext.Default.ElevationPreferenceChangedLogData));
            ApplyLanArgumentPreferences();
            RefreshElevationDependencyStates();
            RefreshNoticeOptions();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            try
            {
                _elevationPreferences = new ElevationPreferencesService(_elevationPreferences.FilePath);
                if (_elevationPreferences.GetBoolean(option.Definition.Key, option.Definition.DefaultBooleanValue ?? false) != previous)
                {
                    _elevationPreferences.SetBoolean(option.Definition.Key, previous);
                    if (!_elevator.IsAllowed)
                        await _elevator.RevokeAsync();
                }
            }
            catch (Exception rollbackException) when (rollbackException is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                WinProvisionLog.Error(rollbackException, "Settings.Elevation.Rollback");
            }
            option.IsEnabled = previous;
            WinProvisionLog.Warning(exception, "Settings.Elevation.Save");
            if (_toastMessages is not null)
            {
                await _toastMessages.PublishAsync(new Notification(
                    Guid.NewGuid().ToString("N"),
                    NotificationCategories.BlockingError,
                    NotificationSeverity.Error,
                    "Não foi possível salvar a preferência",
                    $"A configuração “{option.Title}” permanece como antes."));
            }
        }
        finally
        {
            IsElevationBusy = false;
        }
    }

    private Dictionary<string, bool> GetElevationPreferenceValues() =>
        _elevationCatalog.Catalog.Settings
            .Where(setting => setting.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(setting => setting.Key,
                setting => _elevationPreferences.GetBoolean(setting.Key, setting.DefaultBooleanValue ?? false),
                StringComparer.OrdinalIgnoreCase);

    private void RefreshElevationDependencyStates()
    {
        Dictionary<string, bool> values = GetElevationPreferenceValues();
        foreach (ElevationSettingOption option in ElevationSettingGroups.SelectMany(group => group.Settings))
        {
            option.UpdatePresentation(values, _elevationCatalog.Catalog.Settings);
            option.IsEnabled = option.Definition.Kind.Equals("preference", StringComparison.OrdinalIgnoreCase)
                && values.TryGetValue(option.Definition.Key, out bool value) && value;
        }
    }

    public string GetActiveElevationPolicyText()
    {
        string Text(string key) => _elevationCatalog.ResolveText(key);
        var lines = new List<string>
        {
            Text("inspector.title"),
            $"Arquivo de preferências: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStoreWinUI3", "elevation-preferences.json")}",
            string.Empty
        };
        foreach (ElevationSettingGroupOption group in ElevationSettingGroups)
        {
            lines.Add(group.Title);
            foreach (ElevationSettingOption option in group.Settings)
            {
                string effective = option.StateText;
                string origin = option.ShowToggle && _elevationPreferences.HasValue(option.Definition.Key)
                    ? "usuário"
                    : "padrão";
                lines.Add($"  {option.Title}: {effective} (origem: {origin})");
            }
        }
        lines.Add(string.Empty);
        lines.Add(Text("inspector.batchAuthorization"));
        lines.Add(Text("inspector.sessionTimeout"));
        lines.Add(string.Empty);
        lines.Add(Text("inspector.fixedProtections"));
        lines.Add($"  {Text("inspector.sha256")}");
        lines.Add($"  {Text("inspector.protectedCopy")}");
        lines.Add($"  {Text("inspector.workerRevalidation")}");
        lines.Add($"  {Text("inspector.argumentValidation")}");
        return string.Join(Environment.NewLine, lines);
    }

    public async Task<string> ReadElevatedLogTextAsync(CancellationToken cancellationToken = default)
    {
        var entries = new List<AppLogEvent>();
        if (!Directory.Exists(_appLog.LogDirectory))
            return "Ainda não há registros elevados no log.";

        foreach (string file in Directory.EnumerateFiles(_appLog.LogDirectory, "app-*.jsonl")
                     .OrderByDescending(File.GetLastWriteTimeUtc).Take(14))
        {
            await foreach (AppLogEvent entry in AppLogJsonLinesReader.ReadAsync(file, cancellationToken))
            {
                if (entry.Source.Equals("Elevated", StringComparison.OrdinalIgnoreCase))
                    entries.Add(entry);
            }
        }
        return entries.Count == 0
            ? "Ainda não há eventos elevados registrados."
            : string.Join(Environment.NewLine, entries.OrderByDescending(entry => entry.Timestamp).Select(entry =>
                $"{entry.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Message}"));
    }

    private async Task InspectElevatorCoreAsync(CancellationToken token)
    {
        var status = await _elevator.InspectAsync(token);
        ElevatorDetailsMessage = $"Caminho: {status.Path}{Environment.NewLine}Versão: {status.Version}{Environment.NewLine}" +
            $"Última verificação: {(status.LastVerified?.ToLocalTime().ToString("g") ?? "não verificado")}";
        ElevatorIntegrityMessage = !status.Present ? "WinProvision Elevator não encontrado. Reinstale o aplicativo."
            : !status.Verified ? "A integridade do Elevator não confere. Reinstale o aplicativo."
            : "WinProvision Elevator verificado por SHA-256.";
        ElevationCacheMessage = !_elevator.IsAllowed ? status.Authorized
            ? "Pedidos pelo Elevator bloqueados; há um cache ativo nesta sessão. Use Encerrar autorização para invalidá-lo."
            : "Pedidos de elevação pelo Elevator estão bloqueados."
            : status.Authorized ? "Autorização disponível nesta sessão. Expira após 5 minutos de inatividade."
            : "Sem autorização ativa nesta sessão. A próxima operação administrativa pode solicitar o UAC.";
    }

    private async Task RefreshElevatorAsync(CancellationToken token)
    {
        if (IsElevationBusy) return;
        IsElevationBusy = true;
        try { LoadElevatorOptions(); await InspectElevatorCoreAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.Elevator.Inspect"); ElevatorIntegrityMessage = "Não foi possível verificar o Elevator. Consulte os logs."; }
        finally { IsElevationBusy = false; }
    }

    private async Task AuthorizeElevationAsync(CancellationToken token)
    {
        if (IsElevationBusy) return;
        IsElevationBusy = true;
        try
        {
            await _elevator.AuthorizeAsync(token);
            await InspectElevatorCoreAsync(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { ElevationCacheMessage = "Solicitação de autorização cancelada."; }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.AuthorizeElevation"); ElevationCacheMessage = _elevator.IsAllowed ? "A autorização não foi concluída. Você pode tentar novamente." : "Pedidos de elevação pelo Elevator estão bloqueados. Altere a preferência para autorizar."; }
        finally { IsElevationBusy = false; }
    }

    private async Task RevokeElevationAsync()
    {
        if (IsElevationBusy) return;
        IsElevationBusy = true;
        try
        {
            await _elevator.RevokeAsync();
            ElevationCacheMessage = "Autorização desta sessão encerrada. A próxima operação administrativa poderá solicitar o UAC novamente.";
        }
        catch (Exception ex) { WinProvisionLog.Warning(ex, "Settings.Elevator.Revoke"); ElevationCacheMessage = "Não foi possível encerrar a autorização. Consulte os logs."; }
        finally { IsElevationBusy = false; }
    }
}
