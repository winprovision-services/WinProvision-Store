using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinProvision.Store.Services;

public static class OperationsPanelResourcePublisher
{
    public static void Publish(ResourceDictionary resources, OperationsPanelConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(configuration);
        OperationsPanelLayout layout = configuration.Layout;
        OperationsPanelTheme theme = configuration.Theme;

        resources["OperationCornerRadius"] = new CornerRadius(theme.RowCornerRadius);
        resources["OperationMessageCornerRadius"] = new CornerRadius(theme.MessageCornerRadius);
        resources["OperationProgressCornerRadius"] = new CornerRadius(theme.ProgressCornerRadius);
        resources["OperationRowHeight"] = layout.RowHeight;
        resources["OperationRowSpacing"] = layout.RowSpacing;
        resources["OperationPanelMinHeight"] = layout.PanelMinHeight;
        resources["OperationPanelMaxHeight"] = layout.PanelMaxHeight;
        resources["OperationPanelDefaultHeight"] = layout.PanelDefaultHeight;
        resources["OperationRowPadding"] = new Thickness(layout.RowHorizontalPadding, layout.RowVerticalPadding,
            layout.RowHorizontalPadding, layout.RowVerticalPadding);
        resources["OperationIconColumnWidth"] = layout.IconColumnWidth;
        resources["OperationIconSize"] = layout.IconSize;
        resources["OperationRowIconHeight"] = layout.RowIconHeight;
        resources["OperationFlexibleColumnSpacing"] = layout.FlexibleColumnSpacing;
        resources["OperationTitleMaxWidthFraction"] = layout.TitleMaxWidthFraction;
        resources["OperationTitleMaxWidth"] = layout.TitleMaxWidth;
        resources["OperationTitleMinWidth"] = layout.TitleMinWidth;
        resources["OperationDescriptionMinWidth"] = layout.DescriptionMinWidth;
        resources["OperationActionButtonWidth"] = layout.ActionButtonWidth;
        resources["OperationMoreButtonWidth"] = layout.MoreButtonWidth;
        resources["OperationButtonHeight"] = layout.ActionButtonHeight;
        resources["OperationMessageHeight"] = layout.DescriptionHeight;
        resources["OperationProgressHeight"] = theme.ProgressHeight;
        resources["OperationProgressTrackHeight"] = theme.ProgressTrackHeight;
        resources["OperationMessagePadding"] = new Thickness(theme.MessagePaddingHorizontal, theme.MessagePaddingTop,
            theme.MessagePaddingHorizontal, theme.MessagePaddingBottom);
        resources["OperationMessageFontFamily"] = new FontFamily(theme.MessageFontFamily);
        resources["OperationMessageFontSize"] = theme.MessageFontSize;
        resources["OperationTitleFontSize"] = theme.TitleFontSize;
        resources["OperationActionFontSize"] = theme.ActionFontSize;
        resources["OperationNeutralBackgroundAlpha"] = theme.NeutralBackgroundAlpha;
        resources["OperationAttentionBackgroundAlpha"] = theme.AttentionBackgroundAlpha;
        resources["OperationMessageBackgroundAlpha"] = theme.MessageBackgroundAlpha;
    }
}
