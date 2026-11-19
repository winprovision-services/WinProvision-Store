using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WinProvision.Cli;

internal static partial class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args is ["--background-update"] or ["--background-update-now"] or ["--deferred-update-worker", ..]) FreeConsole();
        else Console.OutputEncoding = Encoding.UTF8;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        bool initialized = false;
        try
        {
            // Native AOT does not initialize COM from MTAThreadAttribute.
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 0));
            initialized = true;
            WinRT.ComWrappersSupport.InitializeComWrappers();
            WinProvision.Elevation.ElevationRuntime.Configure(WinProvision.Elevation.ElevationRuntime.CreateDefaultRunner(
                CliDiagnostics.Write,
                () => new WinProvision.Core.Services.ElevationPreferencesService().AllowElevation));
            return RunAsync(args, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { CliConsole.Standard().Cancelled(); return 130; }
        catch (Exception exception)
        {
            CliDiagnostics.Write(exception.ToString());
            CliConsole.Standard().Result(exception is ArgumentException ? "Erro: " + CliApplication.Clean(exception.Message) : CliFailure.Message(CliFailure.Describe(exception)), false);
            return exception is ArgumentException ? 2 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            try
            {
                WinProvision.Elevation.ElevationRuntime.CacheManager.StopAsync().GetAwaiter().GetResult();
            }
            catch (Exception exception) { CliDiagnostics.Write(exception.ToString()); }
            if (initialized) CoUninitialize();
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        if (args.Length > 0 && args[0] == "--deferred-update-worker")
            return await WinProvision.Core.Services.DeferredAppUpdate.RunWorkerAsync(args, token).ConfigureAwait(false);
        if (args is ["--selftest"])
        {
            Console.Out.WriteLine(WinProvision.Core.Services.LanWorkerSelfTest.SuccessMarker);
            return 0;
        }
        if (args is [var command] && command == WinProvision.PackageManagement.ElevatedComProtocol.ProbeCommand)
        {
            Console.Out.WriteLine(WinProvision.PackageManagement.ElevatedComProtocol.ReadyMarker);
            return 0;
        }
        if (args is [var loadCommand] && loadCommand == WinProvision.PackageManagement.ElevatedComProtocol.LoadProbeCommand)
        {
            WinProvision.Providers.WinGet.Com.WinGetComClient.EnableInProcCom();
            var loadProbe = await new WinProvision.Providers.WinGet.Com.WinGetComClient().ProbeActivationAsync(token);
            if (loadProbe.Status != WinProvision.Providers.WinGet.Com.Contracts.WinGetComStatus.Success)
                throw new System.Runtime.InteropServices.COMException(loadProbe.TechnicalMessage, loadProbe.HResult);
            Console.Out.WriteLine(WinProvision.PackageManagement.ElevatedComProtocol.ReadyMarker);
            return 0;
        }
        if (args is ["--lan-install-worker", var requestPath])
        {
            return await WinProvision.Core.Services.LanInstallService.RunElevatedWorkerAsync(
                requestPath,
                item => Console.Error.WriteLine(WinProvision.Core.Services.LanInstallService.EncodeWorkerProgress(item)),
                token);
        }
        if (args.Length > 0 && args[0] == WinProvision.PackageManagement.ElevatedComProtocol.Command)
            return await RunElevatedComAsync(args, token);
        if (args is ["--background-update"] or ["--background-update-now"])
        {
            WinProvision.Core.Services.AotJson.Register(BackgroundJsonContext.Default);
            return await BackgroundUpdateApplication.RunScheduledAsync(token, force: args[0] == "--background-update-now");
        }
        string version = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";
        WinProvision.Core.Services.AotJson.Register(BackgroundJsonContext.Default);
        var installation = new WinProvision.Store.Services.InstallationPreferencesService();
        var options = new WinProvision.Store.Services.PackageInstallOptionsService();
        await using var backend = new ResilientCliBackend(CliDiagnostics.Write,
            preferredEngine: () => installation.PreferredWinGetEngine, updateOptions: options.Get);
        var presentation = CliConsole.Standard();
        return await new CliApplication(backend, Console.In, Console.Out, Console.Error, version, Console.IsInputRedirected, presentation).RunAsync(args, token);
    }

    private static async Task<int> RunElevatedComAsync(string[] args, CancellationToken token)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (args.Length != 2 || !new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new ArgumentException("O worker COM exige autorização administrativa pelo WinProvision Elevator.");
        var request = WinProvision.PackageManagement.ElevatedComProtocol.DecodeRequest(args[1]);
        var selection = request.Selection is { } selected
            ? new WinProvision.Core.Models.PackageInstallOptions(Scope: selected.Scope, Architecture: selected.Architecture,
                Version: selected.Version, InstallerType: selected.InstallerType, Locale: selected.Locale)
            : new WinProvision.Core.Models.PackageInstallOptions();
        WinProvision.Providers.WinGet.Com.WinGetComClient.EnableInProcCom();
        var activation = await new WinProvision.Providers.WinGet.Com.WinGetComClient().ProbeActivationAsync(token);
        if (activation.Status != WinProvision.Providers.WinGet.Com.Contracts.WinGetComStatus.Success)
            throw new System.Runtime.InteropServices.COMException(activation.TechnicalMessage, activation.HResult);
        Console.Out.WriteLine(WinProvision.PackageManagement.ElevatedComProtocol.ReadyMarker);
        Console.Out.Flush();
        var service = new WinProvision.PackageManagement.WinGetService(new(), new());
        void Log(string line) => Console.Error.WriteLine(CliApplication.Clean(line));
        WinProvision.PackageManagement.PackageDiagnostics.Configure(Log, () => { });
        void Progress(WinProvision.Core.Models.InstallProgressUpdate update)
            => Console.Out.WriteLine(WinProvision.PackageManagement.ElevatedComProtocol.EncodeProgress(update));
        var result = request.Operation == "install"
            ? await service.InstallWithMethodAsync(request.PackageId, WinProvision.Core.Models.PackageInstallMethod.ComApi,
                Log, token, request.InstallLocation, Progress, options: selection)
            : request.Operation == "update" ? await service.UpdateComOnlyAsync(request.PackageId, Log, token,
                onProgress: Progress, options: selection)
            : await service.UninstallComOnlyAsync(request.PackageId, null, token)
                ?? new WinProvision.Core.Services.WingetExecutionResult { Success = false, ExitCode = -1, Output = "Pacote não localizado pela COM local." };
        Console.Out.WriteLine(WinProvision.PackageManagement.ElevatedComProtocol.EncodeResult(result));
        return 0; // The complete operation result travels in the framed response.
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint apartment);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
