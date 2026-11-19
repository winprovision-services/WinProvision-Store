using Microsoft.UI.Xaml.Controls;

namespace WinProvision.Store.Services;

internal static class ContentDialogCoordinator
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dialog);
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            Gate.Release();
        }
    }
}
