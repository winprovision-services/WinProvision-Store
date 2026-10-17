using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WinProvision.Cli;

internal static partial class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args is ["--background-update"]) FreeConsole();
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
            return RunAsync(args, cancellation.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("Operação cancelada."); return 130; }
        catch (Exception exception)
        {
            Console.Error.WriteLine("Erro: " + CliApplication.Clean(exception.Message));
            return exception is ArgumentException ? 2 : 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            if (initialized) CoUninitialize();
        }
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        if (args.Length > 0 && args[0] == WinProvision.PackageManagement.ElevatedComProtocol.Command)
            return await RunElevatedComAsync(args, token);
        if (args is ["--background-update"])
        {
            WinProvision.Core.Services.AotJson.Register(BackgroundJsonContext.Default);
            return await BackgroundUpdateApplication.RunScheduledAsync(token);
        }
        string version = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";
        await using var backend = new ResilientCliBackend(message => Console.Error.WriteLine(CliApplication.Clean(message)));
        try { return await new CliApplication(backend, Console.In, Console.Out, Console.Error, version, Console.IsInputRedirected).RunAsync(args, token); }
        finally
        {
            try { await WinProvision.Core.Services.GsudoElevationService.StopCacheAsync(); }
            catch (Exception ex) { Console.Error.WriteLine("Não foi possível encerrar a autorização temporária: " + CliApplication.Clean(ex.Message)); }
        }
    }

    private static async Task<int> RunElevatedComAsync(string[] args, CancellationToken token)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (args.Length != 2 || !new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new ArgumentException("O worker COM exige autorização administrativa pelo WinProvision Elevator.");
        var request = WinProvision.PackageManagement.ElevatedComProtocol.DecodeRequest(args[1]);
        WinProvision.PackageManagement.InProcessWinGetFactory.Enable();
        WinProvision.PackageManagement.WinGetFactoryHelper.ConfigureMode(["/winget-mode=com"]);
        await using var api = new WinProvision.Core.Services.WinProvisionApiService();
        var service = new WinProvision.PackageManagement.WinGetService(new(), api, new());
        void Log(string line) => Console.Error.WriteLine(CliApplication.Clean(line));
        WinProvision.PackageManagement.PackageDiagnostics.Configure(Log, () => { });
        void Progress(WinProvision.Core.Models.InstallProgressUpdate update)
            => Console.Out.WriteLine(WinProvision.PackageManagement.ElevatedComProtocol.EncodeProgress(update));
        var result = request.Operation == "install"
            ? await service.InstallWithMethodAsync(request.PackageId, WinProvision.Core.Models.PackageInstallMethod.ComApi,
                Log, token, request.InstallLocation, Progress)
            : request.Operation == "update" ? await service.UpdateAsync(request.PackageId, Log, token, onProgress: Progress)
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
