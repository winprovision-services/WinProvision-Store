using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace WinProvision.Store;

internal static partial class Program
{
    [STAThread]
    private static void Main()
    {
        bool initialized = false;
        try
        {

            WinProvision.Core.Services.AotJson.Register(UiJsonContext.Default);
            var arguments = Environment.GetCommandLineArgs();
#if VALIDATION_HARNESS
            WinGetFactoryHelper.ConfigureMode(arguments.Contains("--probe-com")
                ? arguments.Append("/winget-mode=com").ToArray() : arguments);
#else
            WinGetFactoryHelper.ConfigureMode(arguments);
#endif
            // Native AOT does not initialize COM from STAThreadAttribute.
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2));
            initialized = true;

            WinRT.ComWrappersSupport.InitializeComWrappers();

            Application.Start(parameters =>
            {

                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                try { _ = new App(); }
                catch (Exception exception) { RecordStartupFailure(exception); throw; }
            });
        }
        catch (Exception exception) { RecordStartupFailure(exception); throw; }
        finally { if (initialized) CoUninitialize(); }
    }

    [System.Runtime.InteropServices.LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint apartment);
    [System.Runtime.InteropServices.LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    private static void RecordStartupFailure(Exception exception)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStoreWinUI3", "Logs");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "startup-failure.log"), exception.ToString());
    }
}
