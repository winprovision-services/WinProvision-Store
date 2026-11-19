using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinProvision.Store.Services;

/// <summary>Applies operation message typography while allowing default tokens to inherit.</summary>
public static class OperationMessageTypography
{
    public static readonly DependencyProperty FontTokenProperty = DependencyProperty.RegisterAttached(
        "FontToken", typeof(string), typeof(OperationMessageTypography), new PropertyMetadata("messageDefault", FontTokenChanged));

    public static readonly DependencyProperty FontWeightTokenProperty = DependencyProperty.RegisterAttached(
        "FontWeightToken", typeof(string), typeof(OperationMessageTypography), new PropertyMetadata("normal", FontWeightTokenChanged));

    public static string GetFontToken(DependencyObject target) => (string)target.GetValue(FontTokenProperty);
    public static void SetFontToken(DependencyObject target, string value) => target.SetValue(FontTokenProperty, value);
    public static string GetFontWeightToken(DependencyObject target) => (string)target.GetValue(FontWeightTokenProperty);
    public static void SetFontWeightToken(DependencyObject target, string value) => target.SetValue(FontWeightTokenProperty, value);

    private static void FontTokenChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock textBlock) return;
        string token = args.NewValue as string ?? "messageDefault";
        if (token == "messageDefault")
        {
            textBlock.ClearValue(TextBlock.FontFamilyProperty);
            return;
        }

        textBlock.FontFamily = new FontFamily(OperationStatePresentationCatalog.ResolveMessageFontFamily(token, "Segoe UI Variable Text, Segoe UI"));
    }

    private static void FontWeightTokenChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not TextBlock textBlock) return;
        string token = args.NewValue as string ?? "normal";
        if (token == "normal")
        {
            textBlock.ClearValue(TextBlock.FontWeightProperty);
            return;
        }

        textBlock.FontWeight = token switch
        {
            "semibold" => Microsoft.UI.Text.FontWeights.SemiBold,
            "bold" => Microsoft.UI.Text.FontWeights.Bold,
            _ => throw new ArgumentOutOfRangeException(nameof(args), token, "Token de peso tipográfico não reconhecido.")
        };
    }
}
