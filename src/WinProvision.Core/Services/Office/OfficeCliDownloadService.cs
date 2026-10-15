using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Profile;

namespace WinProvision.Core.Services.Office;

/// <summary>Prepares the default Office module without configuring or installing Office.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeCliDownloadService(IOfflineOfficeSource source)
{
    public static OfflineCollectionRequest DefaultRequest(bool offline) => new("office.default", "Microsoft 365 FamÃ­lia", "office",
        IsOffice: true, Office: new OfficeInstallOptions
        {
            ProductId = OfficePlan.Family.ProductId, Architecture = 64, LanguageId = "pt-br",
            ExcludedApps = ["Access", "Publisher"], Silent = true, AutoUpdatesEnabled = true,
            DownloadBeforeInstall = offline
        });

    public static OfficeInstallRequest DefaultInstallRequest()
    {
        var options = DefaultRequest(false).Office!;
        return new(OfficePlan.Family, options.Architecture, options.LanguageId, options.ExcludedApps,
            AutoUpdatesEnabled: options.AutoUpdatesEnabled);
    }

    public async Task<string> DownloadAsync(bool offline, string downloads, IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
        => await DownloadAsync(DefaultRequest(offline), downloads, progress, token);

    public async Task<string> DownloadAsync(OfflineCollectionRequest request, string downloads, IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
    {
        bool offline = request.Office?.DownloadBeforeInstall == true;
        token.ThrowIfCancellationRequested();
        string parent = Path.GetFullPath(downloads);
        Directory.CreateDirectory(parent);
        string name = $"WinProvision-Office-{(offline ? "Offline" : "Online")}-{Guid.NewGuid():N}";
        string staging = Path.Combine(parent, "." + name + ".partial");
        string destination = Path.Combine(parent, name);
        try
        {
            await OfflineOfficeModuleBuilder.PrepareAsync(staging, [request], source, progress, token).ConfigureAwait(false);
            if (offline) await OfficeOfflinePayload.VerifyAsync(Path.Combine(staging, "Office"), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            return Path.Combine(destination, "Office");
        }
        finally
        {
            // Only the unique staging directory created for this call is removed.
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public static string GetDownloadsFolder()
    {
        Guid id = new("374DE290-123F-4565-9164-39C4925E467B");
        int result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out IntPtr path);
        try
        {
            Marshal.ThrowExceptionForHR(result);
            return Marshal.PtrToStringUni(path) ?? throw new IOException("A pasta Downloads nÃ£o foi encontrada.");
        }
        finally { if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path); }
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid folder, uint flags, IntPtr token, out IntPtr path);
}
