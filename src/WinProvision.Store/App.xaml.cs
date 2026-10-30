using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinProvision.Core.Services;
using WinProvision.Store.Services;
using WinProvision.Store.ViewModels;
using WinProvision.Store.Views;
namespace WinProvision.Store;
public partial class App : Application
{
    public static App CurrentApp => (App)Current;
#if VALIDATION_HARNESS
    public static bool IsSmokeTest => Environment.GetCommandLineArgs().Contains("--smoke-test");
#else
    public static bool IsSmokeTest => false;
#endif
    partial void ConfigureValidationDebug();
    partial void ConfigureValidationServices(ServiceCollection services);
    partial void TryLaunchValidation(ref bool handled);
    partial void ConfigureValidationWindow();
    private ServiceProvider? _services;
    private MainWindow? _window;
    private Task? _shutdown;
    private Task? _backgroundUpdateTask;
    private MemoryMilestoneLog? _memoryMilestoneLog;
    internal static MemoryMilestoneLog? MemoryLog { get; private set; }
    public bool ShutdownComplete { get; private set; }
    public bool IsShuttingDown => _shutdown is not null;
    public App()
    {
        InitializeComponent();
#if DEBUG
        DebugMemoryTelemetry.Start();
#endif
        ConfigureValidationDebug();
        UnhandledException += (_, args) => WinProvisionLog.Error(args.Exception, "UnhandledException");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _memoryMilestoneLog = MemoryMilestoneLog.TryCreate();
        MemoryLog = _memoryMilestoneLog;
        _memoryMilestoneLog?.RecordOnce("onlaunched_start");
        _services = BuildServiceProvider();
        _memoryMilestoneLog?.RecordOnce("di_provider_created");
        bool validationHandled = false;
        TryLaunchValidation(ref validationHandled);
        if (validationHandled) return;
        if (Environment.GetCommandLineArgs().Contains("--background-update"))
        {
            _backgroundUpdateTask = RunBackgroundUpdatesAsync();
            _ = _backgroundUpdateTask.ContinueWith(
                task => WinProvisionLog.Error(task.Exception!, "BackgroundUpdateTask"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
            return;
        }
        _window = new MainWindow(_services);
        _memoryMilestoneLog?.RecordOnce("mainwindow_created");
        _services.GetRequiredService<WindowsAppNotificationService>().TryRegister(() =>
            _window.RootElement.DispatcherQueue.TryEnqueue(() => _window.Activate()));
        _services.GetRequiredService<OperationsQueueService>().BatchCompleted +=
            _services.GetRequiredService<WindowsAppNotificationService>().ShowOperationBatchResult;
        _services.GetRequiredService<SettingsViewModel>().RestartRequested += Settings_RestartRequested;
        _memoryMilestoneLog?.RecordOnce("settings_viewmodel_resolved");
        _memoryMilestoneLog?.RecordOnce("startup_dependencies_resolved");
        ConfigureValidationWindow();
        _window.Activate();
        if (Environment.GetCommandLineArgs().Contains("--startup-minimized"))
            _window.Minimize();
        _memoryMilestoneLog?.RecordOnce("window_activated");
    }
    private async void Settings_RestartRequested(object? sender, EventArgs e)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        if (_window is null) return;
        await ShutdownAsync();
        _window.Close();
    }
    private async Task RunBackgroundUpdatesAsync()
    {
        // Older logon registrations still point to the UI host. Forward them to the CLI.
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(
                WinProvision.Core.Services.BackgroundUpdateCommand.CliPath(AppContext.BaseDirectory))
            {
                UseShellExecute = false, CreateNoWindow = true
            };
            start.ArgumentList.Add("--background-update");
            using var process = System.Diagnostics.Process.Start(start)
                ?? throw new InvalidOperationException("Não foi possível iniciar o atualizador pela CLI.");
            await process.WaitForExitAsync();
            Environment.ExitCode = process.ExitCode;
        }
        catch (Exception ex) { WinProvisionLog.Error(ex, "BackgroundUpdateCli"); Environment.ExitCode = 1; }
        finally
        {
            await _services!.DisposeAsync();
            Exit();
        }
    }

}
