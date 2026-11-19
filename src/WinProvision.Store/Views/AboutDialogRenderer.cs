using FluentIcons.Common;
using FluentIcons.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using WinProvision.Core.Models;
using WinProvision.Store.Controls;
using WinProvision.Store.Services;

namespace WinProvision.Store.Views;

internal static class AboutDialogRenderer
{
    public static async Task ShowAsync(UIElement keyboardScope, XamlRoot root, AboutContentCatalog catalog,
        AboutValueProviderRegistry providers, Func<Uri, Task> openUri, Action closed)
    {
        var tabs = catalog.Tabs;
        if (tabs.Count == 0) return;
        var tabButtons = new List<Button>();
        var tabContent = new StackPanel { Spacing = 16, Margin = new Thickness(0, 4, 0, 20) };
        var contentScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = Math.Max(240, root.Size.Height - 220),
            Content = tabContent
        };
        var linkStatus = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var tabFooter = new TextBlock
        {
            Visibility = Visibility.Collapsed,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
        };
        var footerSeparator = new Border { Height = 1, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"], Visibility = Visibility.Collapsed };
        var tabBar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var blockRenderer = new BlockRenderer(catalog, providers, openUri,
            message => { linkStatus.Text = message; linkStatus.Visibility = Visibility.Visible; });
        void SelectTab(int index)
        {
            AboutDialogTab tab = tabs[index];
            tabContent.Children.Clear();
            foreach (AboutDialogBlock block in tab.Blocks)
                if (blockRenderer.Render(block) is { } element) tabContent.Children.Add(element);
            tabFooter.Text = tab.Footer ?? string.Empty;
            tabFooter.Visibility = string.IsNullOrWhiteSpace(tab.Footer) ? Visibility.Collapsed : Visibility.Visible;
            footerSeparator.Visibility = string.IsNullOrWhiteSpace(tab.Footer) ? Visibility.Collapsed : Visibility.Visible;
            for (int i = 0; i < tabButtons.Count; i++)
                if (tabButtons[i].Content is StackPanel stack && stack.Children.LastOrDefault() is Border underline)
                    underline.Opacity = i == index ? 1 : 0;
        }

        for (int i = 0; i < tabs.Count; i++)
        {
            int tabIndex = i;
            string label = catalog.GetText(tabs[i].LabelKey);
            var underline = new Border { Height = 2, Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"], Opacity = 0 };
            var stack = new StackPanel { Spacing = 4 };
            stack.Children.Add(new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center });
            stack.Children.Add(underline);
            var button = new Button { Content = stack, Padding = new Thickness(12, 6, 12, 2), MinWidth = 72, Background = null, BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => SelectTab(tabIndex);
            button.KeyDown += (_, args) =>
            {
                if (args.Key is not (VirtualKey.Left or VirtualKey.Right)) return;
                int next = args.Key == VirtualKey.Right ? (tabIndex + 1) % tabs.Count : (tabIndex + tabs.Count - 1) % tabs.Count;
                SelectTab(next);
                tabButtons[next].Focus(FocusState.Keyboard);
                args.Handled = true;
            };
            tabButtons.Add(button);
            tabBar.Children.Add(button);
        }

        var titleRow = new Grid { ColumnSpacing = 8 };
        double dialogWidth = Math.Max(280, Math.Min(520, root.Size.Width - 48));
        titleRow.Width = Math.Max(232, dialogWidth - 48);
        titleRow.HorizontalAlignment = HorizontalAlignment.Stretch;
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        titleRow.Children.Add(new TextBlock
        {
            Text = catalog.GetText("about.dialog.title"),
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        var close = new Button
        {
            Content = new FluentIcon { Icon = Icon.Dismiss, IconVariant = IconVariant.Regular, IconSize = IconSize.Size16 },
            Width = 32, Height = 32, MinWidth = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(8),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
        };
        AutomationProperties.SetName(close, catalog.GetText("about.close"));
        Grid.SetColumn(close, 1);
        titleRow.Children.Add(close);
        AutomationProperties.SetName(titleRow, catalog.GetText("about.dialog.title"));

        var layout = new StackPanel { Spacing = 12, MaxWidth = 520 };
        layout.Children.Add(tabBar);
        layout.Children.Add(footerSeparator);
        layout.Children.Add(contentScroll);
        layout.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] });
        layout.Children.Add(tabFooter);
        layout.Children.Add(linkStatus);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = titleRow,
            Content = layout,
            Width = dialogWidth,
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        close.Click += (_, _) => dialog.Hide();
        var escapeAccelerator = new KeyboardAccelerator { Key = VirtualKey.Escape, ScopeOwner = keyboardScope };
        escapeAccelerator.Invoked += (_, args) =>
        {
            dialog.Hide();
            args.Handled = true;
        };
        keyboardScope.KeyboardAccelerators.Add(escapeAccelerator);
        dialog.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Escape) return;
            dialog.Hide();
            args.Handled = true;
        };
        dialog.Opened += (_, _) => tabButtons[0].DispatcherQueue.TryEnqueue(
            () => tabButtons[0].Focus(FocusState.Keyboard));

        SelectTab(0);
        try { await ContentDialogCoordinator.ShowAsync(dialog); }
        finally
        {
            keyboardScope.KeyboardAccelerators.Remove(escapeAccelerator);
            closed();
        }
    }

    internal static StackPanel RenderTabForSmoke(AboutDialogTab tab, AboutContentCatalog catalog,
        AboutValueProviderRegistry providers)
    {
        var renderer = new BlockRenderer(catalog, providers, _ => Task.CompletedTask, _ => { });
        var content = new StackPanel { Spacing = 16 };
        foreach (AboutDialogBlock block in tab.Blocks)
            if (renderer.Render(block) is { } element) content.Children.Add(element);
        return content;
    }

    private sealed class BlockRenderer(AboutContentCatalog catalog, AboutValueProviderRegistry providers,
        Func<Uri, Task> openUri, Action<string> linkFailure)
    {
        public FrameworkElement? Render(AboutDialogBlock block)
        {
            string Text(string? key) => string.IsNullOrWhiteSpace(key) ? string.Empty : catalog.GetText(key);
            return block.Type switch
            {
                "heading" => new TextBlock { Text = Text(block.TextKey), FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                "subtitle" => new TextBlock { Text = Text(block.TextKey), FontSize = 14, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], TextWrapping = TextWrapping.Wrap },
                "paragraph" => new TextBlock { Text = Text(block.TextKey), TextWrapping = TextWrapping.Wrap },
                "valueLine" => RenderValueLine(block, Text),
                "separator" => new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4), Background = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"] },
                "notice" => RenderNotice(block, Text),
                "links" => RenderLinks(block.Links ?? [], Text),
                "list" => RenderList(block.Items ?? [], Text),
                _ => null
            };
        }

        private FrameworkElement? RenderValueLine(AboutDialogBlock block, Func<string?, string> text) =>
            block.ProviderId is not null && providers.TryResolve(block.ProviderId, out string value)
                ? new TextBlock { Text = text(block.TextKey).Replace("{value}", value, StringComparison.Ordinal), TextWrapping = TextWrapping.Wrap }
                : null;

        private static FrameworkElement RenderNotice(AboutDialogBlock block, Func<string?, string> text)
        {
            bool warning = block.Severity == "warning";
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(new TextBlock { Text = text(block.TitleKey), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = text(block.TextKey), TextWrapping = TextWrapping.Wrap });
            Icon icon = Enum.TryParse(PackageScreenCatalog.Default.ResolveIcon(block.Icon ?? string.Empty), true, out Icon parsed) ? parsed : Icon.Info;
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(new FluentIcon { Icon = icon, IconVariant = IconVariant.Regular, IconSize = IconSize.Size20, Foreground = warning ? (Brush)Application.Current.Resources["SystemFillColorCautionBrush"] : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"], VerticalAlignment = VerticalAlignment.Top });
            Grid.SetColumn(content, 1); row.Children.Add(content);
            var border = new Border { Child = row, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1) };
            border.Background = (Brush)Application.Current.Resources[warning ? "SystemFillColorCautionBackgroundBrush" : "CardBackgroundFillColorDefaultBrush"];
            border.BorderBrush = (Brush)Application.Current.Resources[warning ? "SystemFillColorCautionBrush" : "CardStrokeColorDefaultBrush"];
            AutomationProperties.SetName(border, text(block.TitleKey));
            return border;
        }

        private FrameworkElement RenderLinks(IReadOnlyList<AboutDialogLink> links, Func<string?, string> text)
        {
            var panel = new WrapPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
            foreach (AboutDialogLink link in links)
            {
                var button = new HyperlinkButton { Content = text(link.LabelKey), Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(4) };
                AutomationProperties.SetName(button, text(link.LabelKey));
                button.Click += async (_, _) =>
                {
                    if (!AboutContentCatalog.TryValidateDestination(link.Destination, out Uri? uri) || uri is null) return;
                    try { await openUri(uri); }
                    catch { linkFailure(catalog.GetText("about.links.failure")); }
                };
                panel.Children.Add(button);
            }
            return panel;
        }

        private FrameworkElement RenderList(IReadOnlyList<AboutDialogListItem> items, Func<string?, string> text)
        {
            var panel = new StackPanel { Spacing = 8 };
            foreach (AboutDialogListItem item in items)
            {
                if (!string.IsNullOrWhiteSpace(item.TextKey)) panel.Children.Add(new TextBlock { Text = text(item.TextKey), TextWrapping = TextWrapping.Wrap });
                else if (!string.IsNullOrWhiteSpace(item.Name))
                {
                    var details = new StackPanel { Spacing = 6, MaxWidth = 480 };
                    AddDetail("Para que o app usa", item.Purpose);
                    AddDetail("Licença", item.License);
                    if (!string.IsNullOrWhiteSpace(item.Warning))
                        details.Children.Add(new TextBlock { Text = item.Warning, TextWrapping = TextWrapping.Wrap,
                            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
                    var links = new WrapPanel { Orientation = Orientation.Horizontal };
                    AddLink("Fonte", item.SourceUrl);
                    AddLink("Licença / termos", item.LicenseUrl);
                    if (links.Children.Count > 0) details.Children.Add(links);
                    var expander = new Expander
                    {
                        Header = item.Name,
                        Content = details,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        MaxWidth = 480
                    };
                    AutomationProperties.SetName(expander, item.Name);
                    panel.Children.Add(expander);

                    void AddDetail(string label, string? value)
                    {
                        if (string.IsNullOrWhiteSpace(value)) return;
                        var detail = new StackPanel { Spacing = 2 };
                        detail.Children.Add(new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
                        detail.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap });
                        details.Children.Add(detail);
                    }

                    void AddLink(string label, string? destination)
                    {
                        if (!AboutContentCatalog.TryValidateDestination(destination ?? string.Empty, out Uri? uri) || uri is null) return;
                        var link = new HyperlinkButton { Content = label, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 8, 0) };
                        link.Click += async (_, _) => { try { await openUri(uri); } catch { linkFailure(catalog.GetText("about.links.failure")); } };
                        links.Children.Add(link);
                    }
                }
            }
            return panel;
        }
    }
}
