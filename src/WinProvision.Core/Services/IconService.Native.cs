using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WinProvision.Core.Services;

public partial class IconService
{
    [SupportedOSPlatform("windows")]
    public string? ExtractIconResource(string path, int resourceIndex = 0, string? cacheIdentity = null)
    {
        if (!File.Exists(path)) return null;
        string key = path + "|" + resourceIndex + "|" + File.GetLastWriteTimeUtc(path).Ticks;
        if (cacheIdentity is not null) key += "|" + cacheIdentity;
        string destination = Path.Combine(_iconCacheFolder, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".png");
        if (File.Exists(destination)) return destination;
        nint large = 0, small = 0;
        try
        {
            if (ExtractIconEx(path, resourceIndex, out large, out small, 1) == 0 || large == 0) return null;
            using var icon = Icon.FromHandle(large);
            using var bitmap = icon.ToBitmap();
            AtomicFile.Write(destination, stream => bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png));
            return destination;
        }
        finally
        {
            if (large != 0) DestroyIcon(large);
            if (small != 0) DestroyIcon(small);
        }
    }

    [SupportedOSPlatform("windows")]
    public static string ReadShortcutTarget(string path)
    {
        using var shortcut = new ShellShortcut();
        return shortcut.ReadTarget(path);
    }

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(string path, int index, out nint large, out nint small, uint count);
    [LibraryImport("user32.dll")]
    private static partial int DestroyIcon(nint icon);
}
