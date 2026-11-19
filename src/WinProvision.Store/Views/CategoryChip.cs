using FluentIcons.Common;

namespace WinProvision.Store.Views;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record CategoryChip(string Tag, string Label, Icon Icon, bool IsSelected)
{
    public bool IsBrowserCategory => Tag.Equals("browsers", StringComparison.OrdinalIgnoreCase);
}
