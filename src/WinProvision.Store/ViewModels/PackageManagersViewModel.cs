using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PackageManagersViewModel : ObservableObject
{
    private readonly PackageManagerSettingsAdapterRegistry _registry;
    private readonly IPresentationService? _presentation;
    private readonly ApplicationPreferencesService? _preferences;
    private readonly OfficeViewModel? _office;
    private bool _updatingOfficeModule;
    private readonly Dictionary<string, PackageManagerSettingsDescriptor> _descriptors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PackageManagerProbeState> _probes = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<PackageManagerRowViewModel> ManagerRows { get; } = [];
    public ObservableCollection<PackageManagerRowViewModel> VisibleManagerRows { get; } = [];
    public ObservableCollection<PackageManagerSettingsFieldViewModel> SelectedInstallFields { get; } = [];
    public ObservableCollection<PackageManagerSettingsFieldViewModel> SelectedInstallToggleFields { get; } = [];
    public ObservableCollection<PackageManagerSettingsFieldViewModel> SelectedInstallControlFields { get; } = [];
    public ObservableCollection<PackageManagerSettingsFieldViewModel> SelectedAdvancedFields { get; } = [];
    public ObservableCollection<PackageManagerSettingsFieldViewModel> SelectedStatusFields { get; } = [];
    public ObservableCollection<PackageManagerSettingsSourceViewModel> SelectedSourceRows { get; } = [];
    public ObservableCollection<PackageManagerSettingsActionViewModel> SelectedActions { get; } = [];
    public ObservableCollection<PackageManagerSettingsActionViewModel> SelectedStatusActions { get; } = [];
    public ObservableCollection<PackageManagerSettingsActionViewModel> SelectedSourceActions { get; } = [];
    public ObservableCollection<string> SelectedNotes { get; } = [];
    public event EventHandler<string>? ManagerDetailsRequested;
    public event EventHandler? OfficeSettingsRequested;
    public string PageTitle => PackageManagerCatalog.Default.GetText("managerPreferences.title", "Preferências do gerenciador de pacotes");
    public string SelectedPageTitle => SelectedManager is null ? "Configurações do gerenciador" : $"Configurações do {SelectedManager.DisplayName}";
    public string SelectedEnableLabel => SelectedManager is null ? "Ativar gerenciador" : FormatTerm("managerPreferences.enable", "Ativar {name}", SelectedManager.DisplayName);
    public string SelectedEnabledLabel => SelectedManager?.IsEnabled == true ? Term("managerPreferences.enabled", "Ativado") : Term("managerPreferences.disabled", "Desativado");
    public string EnabledStateText => Term("managerPreferences.enabled", "Ativado");
    public string DisabledStateText => Term("managerPreferences.disabled", "Desativado");
    public string SelectedStatusSectionTitle => SelectedManager is null ? "Status" : FormatTerm("managerPreferences.statusTitle", "Status de {name}", SelectedManager.DisplayName);
    public string SelectedInstallSectionTitle => SelectedManager is null ? "Opções de instalação padrão" : FormatTerm("managerPreferences.installOptionsTitle", "Opções de instalação padrão para pacotes do {name}", SelectedManager.DisplayName);
    public string SelectedSourceSectionTitle => SelectedManager is null ? "Fontes" : FormatTerm("managerPreferences.sourcesTitle", "Fontes do {name}", SelectedManager.DisplayName);
    public string ApplyText => Term("managerPreferences.apply", "Aplicar");
    public string ResetText => Term("managerPreferences.reset", "Redefinir");
    public string AddSourceText => Term("managerPreferences.addSource", "Adicionar fonte");
    public string DefaultPathText => Term("managerPreferences.defaultPath", "Padrão do pacote");
    public string SelectPathText => Term("managerPreferences.selectPath", "Selecionar");
    public string RefreshSourcesAutomationName => Term("managerPreferences.refreshSources", "Atualizar fontes");
    public string VerifyAllText => PackageManagerCatalog.Default.GetText("managerPreferences.verifyAll", "Verificar novamente");
    public bool AreManagerChecksComplete => _probes.Count == _registry.Adapters.Count;
    public bool SupportsSources => SelectedSettingsDescriptor?.Sources.Supported == true;
    public bool CanAddSource => SelectedSettingsDescriptor?.Sources.CanAdd == true;
    public bool CanRemoveSource => SelectedSettingsDescriptor?.Sources.CanRemove == true;
    public bool CanRefreshSources => SelectedSettingsDescriptor?.Sources.CanRefresh == true;
    public bool CanResetSources => SelectedSettingsDescriptor?.Sources.CanReset == true;
    public bool SupportsActions => SelectedSettingsDescriptor?.Actions.Supported == true;
    public bool HasSelectedStatusFields => SelectedStatusFields.Count > 0;
    public bool HasSelectedStatusActions => SelectedStatusActions.Count > 0;
    [ObservableProperty] public partial bool HasPendingSettingsChanges { get; private set; }
    public bool CanResetSelectedSettings => SelectedManager?.IsEnabled == true
        && SelectedInstallFields.Concat(SelectedAdvancedFields).Concat(SelectedStatusFields).Any(setting => !setting.IsDefault);

    [ObservableProperty] public partial PackageManagerRowViewModel? SelectedManager { get; private set; }
    [ObservableProperty] public partial PackageManagerSettingsDescriptor? SelectedSettingsDescriptor { get; private set; }
    [ObservableProperty] public partial string SelectedManagerStatus { get; private set; } = "Ainda não verificado";
    [ObservableProperty] public partial string SelectedManagerVersionLabel { get; private set; } = string.Empty;
    [ObservableProperty] public partial string SelectedManagerExecutable { get; private set; } = string.Empty;
    [ObservableProperty] public partial string SelectedManagerCheckedAt { get; private set; } = string.Empty;
    [ObservableProperty] public partial bool HasAdvancedFields { get; private set; }
    [ObservableProperty] public partial string NewSelectedSourceName { get; set; } = string.Empty;
    [ObservableProperty] public partial string NewSelectedSourceUrl { get; set; } = string.Empty;
    [ObservableProperty] public partial PackageManagerSettingsSourceViewModel? SelectedSourceRow { get; set; }
    [ObservableProperty] public partial string SelectedSourcesStatus { get; private set; } = string.Empty;
    [ObservableProperty] public partial bool HasSelectedNotes { get; private set; }
    [ObservableProperty] public partial string SelectedLogsText { get; private set; } = string.Empty;
    [ObservableProperty] public partial bool IsCheckingAllManagers { get; private set; }
    [ObservableProperty] public partial string LastAllManagersChecked { get; private set; } = string.Empty;
    [ObservableProperty] public partial string ManagerSearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsOfficeModuleEnabled { get; set; }
    [ObservableProperty] public partial string OfficeModuleFeedbackText { get; private set; } = string.Empty;
    public bool HasOfficeModuleFeedback => !string.IsNullOrWhiteSpace(OfficeModuleFeedbackText);
    public bool IsOfficeModuleVisible => string.IsNullOrWhiteSpace(ManagerSearchText)
        || "Office".Contains(ManagerSearchText.Trim(), StringComparison.OrdinalIgnoreCase)
        || "Instale, configure e remova o Microsoft Office (Microsoft 365). Desative para ocultá-lo do menu lateral."
            .Contains(ManagerSearchText.Trim(), StringComparison.OrdinalIgnoreCase);

    public IAsyncRelayCommand CheckAllManagersCommand { get; }
    public IAsyncRelayCommand CheckSelectedManagerCommand { get; }
    public IAsyncRelayCommand ApplySelectedSettingsCommand { get; }
    public IAsyncRelayCommand ResetSelectedSettingsCommand { get; }
    public IAsyncRelayCommand AddSelectedSourceCommand { get; }
    public IAsyncRelayCommand RefreshSelectedSourcesCommand { get; }
    public IAsyncRelayCommand ExecuteSelectedActionCommand { get; }

    public PackageManagersViewModel(PackageManagerSettingsAdapterRegistry registry, IPresentationService? presentation = null,
        ApplicationPreferencesService? preferences = null, OfficeViewModel? office = null)
    {
        _registry = registry;
        _presentation = presentation;
        _preferences = preferences;
        _office = office;
        IsOfficeModuleEnabled = preferences?.OfficeModuleEnabled ?? true;
        foreach (IPackageManagerSettingsAdapter adapter in registry.Adapters)
        {
            PackageManagerDescriptor definition = adapter.Definition;
            ManagerRows.Add(new PackageManagerRowViewModel(definition, PackageManagerCatalog.Default,
                adapter.Enabled, SetManagerEnabledAsync,
                id => ManagerDetailsRequested?.Invoke(this, id)));
        }
        PackageManagerCatalog catalog = PackageManagerCatalog.Default;
        foreach (PackageManagerDescriptor definition in catalog.SettingsManagers
                     .Where(definition => registry.Adapters.All(adapter => !adapter.Definition.Id.Equals(definition.Id, StringComparison.OrdinalIgnoreCase))))
        {
            ManagerRows.Add(new PackageManagerRowViewModel(definition, catalog, enabled: false,
                static (_, _) => Task.CompletedTask, static _ => { }));
        }
        RefreshVisibleManagerRows();
        CheckAllManagersCommand = new AsyncRelayCommand(CheckAllManagersAsync);
        CheckSelectedManagerCommand = new AsyncRelayCommand(CheckSelectedManagerAsync);
        ApplySelectedSettingsCommand = new AsyncRelayCommand(ApplySelectedSettingsAsync,
            () => HasPendingSettingsChanges && SelectedManager?.IsEnabled == true);
        ResetSelectedSettingsCommand = new AsyncRelayCommand(ResetSelectedSettingsAsync,
            () => CanResetSelectedSettings);
        AddSelectedSourceCommand = new AsyncRelayCommand(AddSelectedSourceAsync);
        RefreshSelectedSourcesCommand = new AsyncRelayCommand(RefreshSelectedSourcesAsync);
        ExecuteSelectedActionCommand = new AsyncRelayCommand<string?>(ExecuteSelectedActionAsync);
    }

    partial void OnManagerSearchTextChanged(string value)
    {
        RefreshVisibleManagerRows();
        OnPropertyChanged(nameof(IsOfficeModuleVisible));
    }

    partial void OnIsOfficeModuleEnabledChanged(bool value)
    {
        if (_updatingOfficeModule || _preferences is null) return;
        OfficeModuleFeedbackText = string.Empty;
        OnPropertyChanged(nameof(HasOfficeModuleFeedback));
        if (!value && _office?.HasActiveOfficeOperation == true)
        {
            _updatingOfficeModule = true;
            IsOfficeModuleEnabled = true;
            _updatingOfficeModule = false;
            OfficeModuleFeedbackText = "Há uma operação do Office em andamento";
            OnPropertyChanged(nameof(HasOfficeModuleFeedback));
            return;
        }
        try { _preferences.SetOfficeModuleEnabled(value); }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "PackageManagersViewModel.SetOfficeModuleEnabled");
            _updatingOfficeModule = true;
            IsOfficeModuleEnabled = !value;
            _updatingOfficeModule = false;
            OfficeModuleFeedbackText = "Não foi possível salvar a preferência do Office.";
            OnPropertyChanged(nameof(HasOfficeModuleFeedback));
        }
    }

    public void OpenOfficeSettings() => OfficeSettingsRequested?.Invoke(this, EventArgs.Empty);

    private void RefreshVisibleManagerRows()
    {
        string query = ManagerSearchText.Trim();
        IEnumerable<PackageManagerRowViewModel> filtered = string.IsNullOrEmpty(query)
            ? ManagerRows
            : ManagerRows.Where(row =>
                row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains(query, StringComparison.OrdinalIgnoreCase));
        VisibleManagerRows.Clear();
        foreach (PackageManagerRowViewModel row in filtered)
            VisibleManagerRows.Add(row);
    }

    public async Task CheckAllManagersAsync(CancellationToken cancellationToken = default)
    {
        IsCheckingAllManagers = true;
        try
        {
            foreach (IPackageManagerSettingsAdapter adapter in _registry.Adapters)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PackageManagerRowViewModel? row = ManagerRows.FirstOrDefault(item => item.Id.Equals(adapter.Definition.Id, StringComparison.OrdinalIgnoreCase));
                try
                {
                    if (!adapter.Enabled)
                    {
                        await ReloadDescriptorAsync(adapter, cancellationToken);
                        continue;
                    }
                    row?.SetBusy(true);
                    row?.SetProgress(adapter.Definition.Id.Equals("winget", StringComparison.OrdinalIgnoreCase)
                        ? "Verificando o WinGet…" : "Verificando Chocolatey…");
                    var progress = row is null ? null : new Progress<string>(row.SetProgress);
                    await adapter.CheckAsync(progress, cancellationToken);
                    await ReloadDescriptorAsync(adapter, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    CoreLog.Warning(exception, "PackageManagersViewModel.CheckAllManagers");
                    row?.SetFeedback(exception.Message);
                }
                finally { row?.SetBusy(false); }
            }
            LastAllManagersChecked = $"Última verificação: {DateTime.Now:dd/MM/yyyy HH:mm:ss}";
        }
        finally { IsCheckingAllManagers = false; OnPropertyChanged(nameof(AreManagerChecksComplete)); }
    }

    public async Task OpenManagerDetails(string id)
    {
        if (!_registry.TryGet(id, out IPackageManagerSettingsAdapter? adapter))
        {
            SelectedManager = ManagerRows.FirstOrDefault(row => row.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            SelectedSettingsDescriptor = null;
            return;
        }
        if (adapter.Definition.ParentId is { Length: > 0 } parentId && _registry.TryGet(parentId, out IPackageManagerSettingsAdapter? parent))
            adapter = parent;
        SelectedManager = ManagerRows.FirstOrDefault(row => row.Id.Equals(adapter.Definition.Id, StringComparison.OrdinalIgnoreCase));
        if (SelectedManager is null) return;
        await ReloadDescriptorAsync(adapter);
        await RefreshSelectedSourcesAsync();
    }

    public void UpdateField(string managerId, string fieldId, string value) => _registry.Get(managerId).SetField(fieldId, value);

    public async Task<string?> SelectPathAsync(string managerId, string fieldId, string pathKind, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_presentation is null) return null;
        string? path = pathKind switch
        {
            "folder" => await _presentation.PickFolderAsync(),
            "file" => await _presentation.PickExecutableAsync(),
            _ => throw new InvalidOperationException($"Tipo de caminho não reconhecido: '{pathKind}'.")
        };
        cancellationToken.ThrowIfCancellationRequested();
        return path;
    }

    public void NotifySettingsChanged()
    {
        HasPendingSettingsChanges = SelectedInstallFields.Concat(SelectedAdvancedFields).Concat(SelectedStatusFields)
            .Any(field => field.IsChanged);
        OnPropertyChanged(nameof(CanResetSelectedSettings));
        ApplySelectedSettingsCommand?.NotifyCanExecuteChanged();
        ResetSelectedSettingsCommand?.NotifyCanExecuteChanged();
    }

    private async Task SetManagerEnabledAsync(PackageManagerRowViewModel row, bool enabled)
    {
        IPackageManagerSettingsAdapter adapter = _registry.Get(row.Id);
        var progress = new Progress<string>(row.SetProgress);
        try
        {
            await adapter.SetEnabledAsync(enabled, progress);
            await ReloadDescriptorAsync(adapter);
        }
        finally
        {
            RefreshParentStates();
        }
        if (_descriptors.TryGetValue(row.Id, out PackageManagerSettingsDescriptor? current))
            _descriptors[row.Id] = current with { Enabled = enabled };
        if (SelectedManager?.Id.Equals(row.Id, StringComparison.OrdinalIgnoreCase) == true && SelectedSettingsDescriptor is not null)
        {
            SelectedSettingsDescriptor = SelectedSettingsDescriptor with { Enabled = enabled };
            OnPropertyChanged(nameof(SelectedEnabledLabel));
            SelectedManagerStatus = enabled ? SelectedSettingsDescriptor.Status.Message : Term("status.disabled", "Desativado");
            NotifySettingsChanged();
        }
    }

    private async Task ReloadDescriptorAsync(IPackageManagerSettingsAdapter adapter, CancellationToken cancellationToken = default)
    {
        PackageManagerSettingsDescriptor descriptor = await adapter.GetDescriptorAsync(cancellationToken);
        _descriptors[adapter.Definition.Id] = descriptor;
        _probes[adapter.Definition.Id] = new PackageManagerProbeState(false, true,
            descriptor.Status.State is "ready" or "available", descriptor.Status.State is "ready" or "available",
            descriptor.Status.State is "error", descriptor.Status.Version);
        PackageManagerRowViewModel? row = ManagerRows.FirstOrDefault(item => item.Id.Equals(adapter.Definition.Id, StringComparison.OrdinalIgnoreCase));
        row?.SetProbe(_probes[adapter.Definition.Id]);
        RefreshParentStates();
        if (SelectedManager?.Id.Equals(adapter.Definition.Id, StringComparison.OrdinalIgnoreCase) != true) return;

        SelectedSettingsDescriptor = descriptor;
        NotifyCapabilities();
        SelectedManagerStatus = descriptor.Enabled ? descriptor.Status.Message : Term("status.disabled", "Desativado");
        SelectedManagerVersionLabel = string.IsNullOrWhiteSpace(descriptor.Status.Version) ? string.Empty : $"Versão: {descriptor.Status.Version}";
        SelectedManagerExecutable = descriptor.Status.Executable ?? string.Empty;
        SelectedManagerCheckedAt = descriptor.Status.CheckedAt is { } checkedAt ? $"Verificado em {checkedAt.LocalDateTime:dd/MM/yyyy HH:mm:ss}" : string.Empty;
        SelectedInstallFields.Clear(); SelectedInstallToggleFields.Clear(); SelectedInstallControlFields.Clear(); SelectedAdvancedFields.Clear(); SelectedStatusFields.Clear();
        foreach (PackageManagerSettingsField field in descriptor.Fields)
        {
            var item = new PackageManagerSettingsFieldViewModel(this, descriptor.Id, field);
            if (field.Section == "status") SelectedStatusFields.Add(item);
            else if (field.Section == "advanced") SelectedAdvancedFields.Add(item);
            else
            {
                SelectedInstallFields.Add(item);
                if (field.Type == "toggle") SelectedInstallToggleFields.Add(item);
                else SelectedInstallControlFields.Add(item);
            }
        }
        HasAdvancedFields = SelectedAdvancedFields.Count > 0;
        SelectedActions.Clear(); SelectedStatusActions.Clear(); SelectedSourceActions.Clear();
        foreach (PackageManagerSettingsAction action in descriptor.Actions.Items)
        {
            var item = new PackageManagerSettingsActionViewModel(action);
            if (string.Equals(action.Section, "status", StringComparison.OrdinalIgnoreCase)) SelectedStatusActions.Add(item);
            else if (string.Equals(action.Section, "sources", StringComparison.OrdinalIgnoreCase)) SelectedSourceActions.Add(item);
            else SelectedActions.Add(item);
        }
        OnPropertyChanged(nameof(HasSelectedStatusFields));
        OnPropertyChanged(nameof(HasSelectedStatusActions));
        SelectedNotes.Clear(); foreach (PackageManagerSettingsNote note in descriptor.Notes) SelectedNotes.Add(note.Text);
        HasSelectedNotes = SelectedNotes.Count > 0;
        SelectedSourceRows.Clear(); foreach (PackageManagerSettingsSource source in descriptor.Sources.Items) SelectedSourceRows.Add(new(source, this));
        SelectedSourcesStatus = descriptor.Sources.Supported
            ? FormatSourceCount(descriptor.Sources.Items.Count)
            : "Este gerenciador não oferece gerenciamento de fontes.";
        NotifySettingsChanged();
        OnPropertyChanged(nameof(AreManagerChecksComplete)); OnPropertyChanged(nameof(SelectedPageTitle)); OnPropertyChanged(nameof(SelectedEnableLabel));
        OnPropertyChanged(nameof(SelectedEnabledLabel)); OnPropertyChanged(nameof(SelectedStatusSectionTitle)); OnPropertyChanged(nameof(SelectedInstallSectionTitle));
        OnPropertyChanged(nameof(SelectedSourceSectionTitle));
    }

    private async Task CheckSelectedManagerAsync(CancellationToken cancellationToken)
    {
        IPackageManagerSettingsAdapter adapter = GetSelectedAdapter();
        PackageManagerRowViewModel? row = SelectedManager;
        try
        {
            row?.SetBusy(true);
            row?.SetProgress("Verificando e preparando o gerenciador…");
            await adapter.CheckAsync(row is null ? null : new Progress<string>(row.SetProgress), cancellationToken);
            await ReloadDescriptorAsync(adapter, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            row?.SetFeedback(exception.Message);
            throw;
        }
        finally { row?.SetBusy(false); }
    }

    private async Task ApplySelectedSettingsAsync(CancellationToken cancellationToken)
    {
        IPackageManagerSettingsAdapter adapter = GetSelectedAdapter();
        await adapter.ApplyAsync(cancellationToken);
        await ReloadDescriptorAsync(adapter, cancellationToken);
    }

    private async Task ResetSelectedSettingsAsync(CancellationToken cancellationToken)
    {
        IPackageManagerSettingsAdapter adapter = GetSelectedAdapter();
        await adapter.ResetAsync(cancellationToken);
        await ReloadDescriptorAsync(adapter, cancellationToken);
    }

    private async Task RefreshSelectedSourcesAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedManager is null || SelectedSettingsDescriptor is null) return;
        IPackageManagerSettingsAdapter adapter = GetSelectedAdapter();
        PackageManagerSettingsDescriptor descriptor = SelectedSettingsDescriptor;
        if (!descriptor.Sources.Supported) { SelectedSourcesStatus = "Este gerenciador não oferece gerenciamento de fontes."; return; }
        IReadOnlyList<PackageManagerSettingsSource> sources = await adapter.ListSourcesAsync(cancellationToken);
        SelectedSettingsDescriptor = descriptor with { Sources = descriptor.Sources with { Items = sources } };
        SelectedSourceRows.Clear(); foreach (PackageManagerSettingsSource source in sources) SelectedSourceRows.Add(new(source, this));
        SelectedSourcesStatus = sources.Count == 0
            ? FormatSourceCount(0) : FormatSourceCount(sources.Count);
    }

    private async Task AddSelectedSourceAsync(CancellationToken cancellationToken)
    {
        IPackageManagerSettingsAdapter adapter = GetSelectedAdapter();
        await adapter.AddSourceAsync(NewSelectedSourceName, NewSelectedSourceUrl, cancellationToken);
        NewSelectedSourceName = NewSelectedSourceUrl = string.Empty;
        await RefreshSelectedSourcesAsync(cancellationToken);
    }

    public async Task RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        if (SelectedManager is null || string.IsNullOrWhiteSpace(sourceId)) return;
        IPackageManagerSettingsAdapter adapter = GetSelectedAdapter();
        if (!adapter.Definition.Id.Equals(SelectedManager.Id, StringComparison.OrdinalIgnoreCase)) return;
        if (SelectedSettingsDescriptor?.Sources.CanRemove != true) return;
        if (SelectedSourceRows.FirstOrDefault(source => source.Id.Equals(sourceId, StringComparison.OrdinalIgnoreCase)) is not { Removable: true } source) return;
        await adapter.RemoveSourceAsync(source.Id, cancellationToken);
        if (SelectedSourceRow?.Id.Equals(source.Id, StringComparison.OrdinalIgnoreCase) == true) SelectedSourceRow = null;
        await RefreshSelectedSourcesAsync(cancellationToken);
    }

    public async Task ResetSourcesAsync(CancellationToken cancellationToken = default)
    {
        IPackageManagerSettingsAdapter adapter = GetSelectedAdapter();
        await adapter.ResetSourcesAsync(cancellationToken);
        await RefreshSelectedSourcesAsync(cancellationToken);
    }

    private async Task ExecuteSelectedActionAsync(string? actionId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actionId)) return;
        string? result = await GetSelectedAdapter().ExecuteActionAsync(actionId, cancellationToken);
        if (result is not null) SelectedLogsText = result;
        IPackageManagerSettingsAdapter adapter = GetSelectedAdapter();
        await ReloadDescriptorAsync(adapter, cancellationToken);
        if (SelectedSettingsDescriptor?.Sources.Supported == true) await RefreshSelectedSourcesAsync(cancellationToken);
    }

    public Task RunActionAsync(string actionId, CancellationToken cancellationToken = default) => ExecuteSelectedActionAsync(actionId, cancellationToken);
    private void NotifyCapabilities()
    {
        OnPropertyChanged(nameof(SupportsSources)); OnPropertyChanged(nameof(CanAddSource)); OnPropertyChanged(nameof(CanRemoveSource));
        OnPropertyChanged(nameof(CanRefreshSources)); OnPropertyChanged(nameof(CanResetSources)); OnPropertyChanged(nameof(SupportsActions));
    }
    private void RefreshParentStates()
    {
        foreach (PackageManagerRowViewModel child in ManagerRows.Where(item => item.Definition.ParentId is not null))
        {
            string parentId = child.Definition.ParentId!;
            child.SetParentState(_registry.TryGet(parentId, out IPackageManagerSettingsAdapter? parent) && parent.Enabled,
                _probes.GetValueOrDefault(parentId));
        }
    }
    private IPackageManagerSettingsAdapter GetSelectedAdapter() => SelectedManager is null
        ? throw new InvalidOperationException("Selecione um gerenciador de pacotes.") : _registry.Get(SelectedManager.Id);

    private static string Term(string key, string fallback) => PackageManagerCatalog.Default.GetText(key, fallback);
    private static string FormatSourceCount(int count) => count switch
    {
        0 => "Nenhuma fonte configurada.",
        1 => "1 fonte carregada.",
        _ => $"{count} fontes carregadas."
    };
    private static string FormatTerm(string key, string fallback, string name) =>
        Term(key, fallback).Replace("{name}", name, StringComparison.Ordinal);
    internal string GetTerm(string key, string fallback) => Term(key, fallback);
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PackageManagerSettingsSourceViewModel
{
    private readonly PackageManagersViewModel _owner;
    public string Id { get; }
    public string Name { get; }
    public string? Url { get; }
    public bool Removable { get; }
    public string RemoveAutomationName => _owner.GetTerm("managerPreferences.removeSource", "Remover fonte") + " " + Name;
    public IAsyncRelayCommand RemoveCommand { get; }

    public PackageManagerSettingsSourceViewModel(PackageManagerSettingsSource source, PackageManagersViewModel owner)
    {
        _owner = owner; Id = source.Id; Name = source.Name; Url = source.Url; Removable = source.Removable;
        RemoveCommand = new AsyncRelayCommand(RemoveAsync, () => Removable && _owner.CanRemoveSource && _owner.SelectedManager?.IsEnabled == true);
    }

    private Task RemoveAsync(CancellationToken cancellationToken) => _owner.RemoveSourceAsync(Id, cancellationToken);
}
