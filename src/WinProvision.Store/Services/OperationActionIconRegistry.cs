namespace WinProvision.Store.Services;

/// <summary>Translates allowlisted action icon IDs to the app's shared Fluent icon controls.</summary>
public static class OperationActionIconRegistry
{
    public static string? GetManagerIconUrl(string iconId) => iconId switch
    {
        "managerChocolatey" => "ms-appx:///Assets/Icons/chocolatey.svg",
        "managerOffice" => "ms-appx:///Assets/OfficeApps/ms365.png",
        "managerWinget" => "ms-appx:///Assets/Icons/winget.png",
        _ => null
    };

    public static FluentIcons.WinUI.FluentIcon CreateIcon(string iconId) => new()
    {
        Icon = GetIcon(iconId),
        IconVariant = FluentIcons.Common.IconVariant.Regular,
        IconSize = FluentIcons.Common.IconSize.Size16,
        FontSize = 16
    };

    public static FluentIcons.Common.Icon GetIcon(string iconId) => iconId switch
    {
            "apps" => FluentIcons.Common.Icon.Apps,
            "document" => FluentIcons.Common.Icon.Document,
            "info" => FluentIcons.Common.Icon.Info,
            "settings" => FluentIcons.Common.Icon.Settings,
            "folderOpen" => FluentIcons.Common.Icon.FolderOpen,
            "arrowClockwise" => FluentIcons.Common.Icon.ArrowClockwise,
            "shield" => FluentIcons.Common.Icon.Shield,
            "desktop" => FluentIcons.Common.Icon.Desktop,
            "dismiss" => FluentIcons.Common.Icon.Dismiss,
            "delete" => FluentIcons.Common.Icon.Delete,
            "checkmark" => FluentIcons.Common.Icon.Checkmark,
            _ => throw new KeyNotFoundException($"Ícone de ação não registrado: {iconId}")
    };
}
