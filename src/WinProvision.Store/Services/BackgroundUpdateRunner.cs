using WinProvision.Core.Services;
using System.Security.Principal;
namespace WinProvision.Store.Services;
public sealed partial class BackgroundUpdateRunner(IPackageBackend backend, ApplicationPreferencesService preferences, IUpdatesIgnoreStore ignored, PackageInstallOptionsService packageOptions)
{
    public async Task<int> RunAsync(WindowsAppNotificationService? notifications=null,CancellationToken token=default)
    {
        if(!preferences.AutoUpdateAtStartup)return 0;
        var available=await backend.ScanUpdatesAsync(token);
        var candidates=available.Where(p=>!ignored.IsIgnored(p.Id,p.AvailableVersion)).GroupBy(p=>$"{p.Source}:{p.Id}",StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToArray();
        if(preferences.UpdateMode==BackgroundUpdateMode.NotifyOnly) { notifications?.ShowBackgroundUpdateCheckSummary(candidates.Length); return 0; }
        var packages=candidates.Where(p=>preferences.UpdateScope==BackgroundUpdateScope.AllSupportedPackages||preferences.IsAutoUpdatePackageSelected(p.Id,p.Source)).ToArray();
        int success=0,failed=0;
        foreach(var p in packages)
        {
            token.ThrowIfCancellationRequested();
            try { if((await backend.UpdateAsync(p,packageOptions.Get(p.Id,p.Source))).Success)success++;else failed++; }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { throw; }
            catch(Exception ex) { failed++;WinProvisionLog.Error(ex,"BackgroundUpdate"); }
        }
        notifications?.ShowBackgroundUpdateSummary(success,failed,candidates.Length-packages.Length);
        return failed==0?0:1;
    }
}
