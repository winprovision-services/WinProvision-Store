namespace WinProvision.Providers.WinGet.Cli.Contracts;

/// <summary>Executes one explicit WinGet CLI operation. It never selects another backend.</summary>
public interface IWinGetCliClient
{
    Task<WinGetCliResult> ProbeAsync(CancellationToken cancellationToken = default);
    Task<WinGetCliResult> SearchAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default);
    Task<WinGetCliResult> ListInstalledAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default);
    Task<WinGetCliResult> ShowAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default);
    Task<WinGetCliResult> DiscoverUpdatesAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default);
    Task<WinGetCliResult> InstallAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default);
    Task<WinGetCliResult> UpgradeAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default);
    Task<WinGetCliResult> UninstallAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default);
    Task<WinGetCliResult> DownloadAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default);
    Task<WinGetCliResult> ExportAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default);
    Task<WinGetCliResult> ManageSourcesAsync(WinGetCliInvocation invocation, IProgress<WinGetCliEvent>? progress = null, CancellationToken cancellationToken = default);
}
