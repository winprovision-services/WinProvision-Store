using Windows.ApplicationModel.DataTransfer;

namespace WinProvision.Store.Services;

public interface IClipboardService
{
    bool TrySetText(string text);
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
}
