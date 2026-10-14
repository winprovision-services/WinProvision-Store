using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Models;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class PackageItemViewModel : ObservableObject
{
    public AppEntry App { get; }
    public ProfileAppRef? ImportedReference { get; }
    public string Name => App.Name;
    public string Publisher => App.Publisher;
    public string Id => App.Id;
    public string Version => string.IsNullOrWhiteSpace(App.Version) ? "—" : App.Version;
    public string IconUrl => App.IconUrl;
    public string SourceLabel => App.Office is not null || App.Source == "office" ? "Office" :
        App.Source == "msstore" ? "Microsoft Store" : "WinGet";
    public string SourceGlyph => SourceLabel switch
    {
        "Microsoft Store" => "\uE719",
        "Office" => "\uECA5",
        _ => "\uE896"
    };
    public string CompatibilityNote => App.Office is not null
        ? "Plano do Office configurado para instalação pelo ODT."
        : App.Source.Equals("office", StringComparison.OrdinalIgnoreCase)
            ? "Este item Office não contém uma configuração de instalação válida."
            : !string.IsNullOrEmpty(ImportedReference?.PinnedVersion)
                ? $"Versão fixa: {ImportedReference.PinnedVersion}"
                : string.Empty;
    public bool SupportsInstall => App.Office is not null
        || (!App.Source.Equals("office", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(App.Id));
    public bool SupportsDownload => App.Office is null
        && !App.Source.Equals("office", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(ImportedReference?.PinnedVersion);

    [ObservableProperty]
    public partial bool IsSelected { get; set; } = true;

    [ObservableProperty]
    public partial bool IsWorking { get; set; }

    [ObservableProperty]
    public partial bool IsInstalled { get; set; }

    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand DetailsCommand { get; }
    public IAsyncRelayCommand DownloadCommand { get; }
    public IRelayCommand AddCommand { get; }
    public IRelayCommand RemoveCommand { get; }

    private readonly Func<PackageItemViewModel, Task> _install;
    private readonly Func<PackageItemViewModel, Task> _details;
    private readonly Func<PackageItemViewModel, Task> _download;
    private readonly Action<PackageItemViewModel> _add;
    private readonly Action<PackageItemViewModel> _remove;
    public PackageItemViewModel(
        AppEntry app,
        Func<PackageItemViewModel, Task> install,
        Func<PackageItemViewModel, Task> details,
        Action<PackageItemViewModel> add,
        Func<PackageItemViewModel, Task>? download = null,
        ProfileAppRef? imported = null,
        Action<PackageItemViewModel>? remove = null)
    {
        App = app;
        ImportedReference = imported;
        _install = install;
        _details = details;
        _download = download ?? (_ => Task.CompletedTask);
        _add = add;
        _remove = remove ?? (_ => { });
        App.IsSelectedForInstall = IsSelected;
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => SupportsInstall && !IsWorking && !IsInstalled);
        DetailsCommand = new AsyncRelayCommand(() => _details(this));
        DownloadCommand = new AsyncRelayCommand(() => _download(this), () => !IsWorking && SupportsDownload);
        AddCommand = new RelayCommand(() => _add(this));
        RemoveCommand = new RelayCommand(() => _remove(this), () => !IsWorking);
    }

    partial void OnIsSelectedChanged(bool value) => App.IsSelectedForInstall = value;

    partial void OnIsWorkingChanged(bool value)
    {
        InstallCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        RemoveCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsInstalledChanged(bool value)
    {
        InstallCommand.NotifyCanExecuteChanged();
    }

    private Task InstallAsync() => SupportsInstall && !IsWorking && !IsInstalled
        ? _install(this)
        : Task.CompletedTask;

    public ProfileAppRef ToReference() => ImportedReference ?? new ProfileAppRef
    {
        Id = App.Id,
        Name = App.Name,
        Source = App.Source,
        Publisher = App.Publisher,
        IconUrl = App.IconUrl,
        Description = App.Description,
        OfficeOptions = App.Office
    };
}
