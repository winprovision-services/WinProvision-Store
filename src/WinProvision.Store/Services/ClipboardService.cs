using Windows.ApplicationModel.DataTransfer;

namespace WinProvision.Store.Services;

public interface IClipboardService
{
    bool TrySetText(string text);
    Task<string?> TryGetTextAsync();
}

public sealed partial class ClipboardService : IClipboardService
{
    public bool TrySetText(string text)
    {
        try
        {
            var content = new DataPackage();
            content.SetText(text);
            Clipboard.SetContent(content);
            return true;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Clipboard");
            return false;
        }
    }

    public async Task<string?> TryGetTextAsync()
    {
        try
        {
            var content = Clipboard.GetContent();
            return content.Contains(StandardDataFormats.Text)
                ? await content.GetTextAsync()
                : null;
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Clipboard.Read");
            return null;
        }
    }
}
