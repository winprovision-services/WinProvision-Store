using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using WinProvision.Store.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public partial class PackageScreenFilterOption : ObservableObject
{
    [ObservableProperty] public partial string Id { get; set; } = string.Empty;
    [ObservableProperty] public partial string Title { get; set; } = string.Empty;
    [ObservableProperty] public partial string Kind { get; set; } = "toggle";
    [ObservableProperty] public partial string? Icon { get; set; }
    [ObservableProperty] public partial string? IconUri { get; set; }
    [ObservableProperty] public partial string? IconUriDark { get; set; }
    [ObservableProperty] public partial bool? IsChecked { get; set; }
    [ObservableProperty] public partial string? SelectedValue { get; set; }

    public IReadOnlyList<PackageScreenFilterValue> Values { get; init; } = [];
    public ICommand? Command { get; init; }

    public PackageScreenFilterOption() { }

    public PackageScreenFilterOption(
        string id, string title, string kind = "toggle", string? icon = null,
        bool? isChecked = null, string? selectedValue = null,
        IReadOnlyList<PackageScreenFilterValue>? values = null, ICommand? command = null, string? iconUri = null,
        string? iconUriDark = null)
    {
        Id = id;
        Title = title;
        Kind = kind;
        Icon = icon;
        IconUri = iconUri;
        IconUriDark = iconUriDark;
        IsChecked = isChecked;
        SelectedValue = selectedValue;
        Values = values ?? [];
        Command = command;
    }
}
