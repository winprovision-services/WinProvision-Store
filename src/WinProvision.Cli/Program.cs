using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace WinProvision.Cli;

internal static partial class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
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
        string version = typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.1.0";
        await using var backend = new ResilientCliBackend(message => Console.Error.WriteLine(CliApplication.Clean(message)));
        return await new CliApplication(backend, Console.In, Console.Out, Console.Error, version).RunAsync(args, token);
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint apartment);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
