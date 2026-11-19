using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinProvision.Store.Services;

namespace WinProvision.Store.Controls;

/// <summary>Visualizador único da saída de uma operação, compartilhado pela fila e pelo histórico.</summary>
internal static class OperationOutputDialog
{
    public static async Task ShowAsync(XamlRoot? xamlRoot, string title, string? output, Func<string, string>? getText = null)
    {
        getText ??= static key => key switch
        {
            "operations.output.empty" => "Nenhum registro ainda",
            "operations.output.copy" => "Copiar",
            "operations.output.close" => "Fechar",
            _ => key
        };
        string textContent = string.IsNullOrWhiteSpace(output)
            ? getText("operations.output.empty")
            : output.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
        var text = new TextBox
        {
            Text = textContent,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 520,
            MaxWidth = 680,
            MinHeight = 280,
            MaxHeight = 520,
            FontFamily = new FontFamily("Consolas")
        };
        var scroll = new ScrollViewer
        {
            Content = text,
            MinHeight = 280,
            MaxHeight = 520,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = title,
            Content = scroll,
            PrimaryButtonText = getText("operations.output.copy"),
            CloseButtonText = getText("operations.output.close"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await ContentDialogCoordinator.ShowAsync(dialog) == ContentDialogResult.Primary)
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(output ?? string.Empty);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }
    }
}
