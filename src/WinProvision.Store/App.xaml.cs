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
    internal IServiceProvider Services => _services ?? throw new InvalidOperationException("Serviços do aplicativo ainda não foram inicializados.");
    private MainWindow? _window;
    private OperationsPanelReloadService? _operationsPanelReload;
    private Task? _shutdown;
    private Task? _backgroundUpdateTask;
    private Task? _automaticAppUpdateTask;
    private Task? _wingetStartupProvisionTask;
    private CancellationTokenSource? _automaticAppUpdateCancellation;
    private Task? _legacyLogMigrationTask;
    private MemoryMilestoneLog? _memoryMilestoneLog;
    internal static MemoryMilestoneLog? MemoryLog { get; private set; }
    public bool ShutdownComplete { get; private set; }
    public bool IsShuttingDown => _shutdown is not null;
    public App()
    {
        InitializeComponent();
#if DEBUG
        DebugPerformanceTelemetry.BeginSession();
#endif
        WinProvisionLog.Configure(AppLogRuntime.Log);
        CoreLog.Configure(AppLogRuntime.Log);
#if DEBUG
        DebugMemoryTelemetry.Start();
#endif
        ConfigureValidationDebug();
        UnhandledException += (_, args) =>
        {
            UnhandledExceptionLog.Write("Application.UnhandledException", args.Exception,
                $"Handled={args.Handled}; ThreadId={Environment.CurrentManagedThreadId}");
            WinProvisionLog.Error(args.Exception, $"UnhandledException (ThreadId={Environment.CurrentManagedThreadId})");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                UnhandledExceptionLog.Write("AppDomain.CurrentDomain.UnhandledException", exception,
                    $"IsTerminating={args.IsTerminating}; ThreadId={Environment.CurrentManagedThreadId}");
                WinProvisionLog.Error(exception,
                    $"AppDomain.UnhandledException (IsTerminating={args.IsTerminating}, ThreadId={Environment.CurrentManagedThreadId})");
            }
            else
            {
                UnhandledExceptionLog.WriteNonException("AppDomain.CurrentDomain.UnhandledException", args.ExceptionObject,
                    $"IsTerminating={args.IsTerminating}; ThreadId={Environment.CurrentManagedThreadId}");
                WinProvisionLog.Warning(
                    new InvalidOperationException($"Objeto de exceção não gerenciado: {args.ExceptionObject}"),
                    $"AppDomain.UnhandledException (IsTerminating={args.IsTerminating}, ThreadId={Environment.CurrentManagedThreadId})");
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            UnhandledExceptionLog.Write("TaskScheduler.UnobservedTaskException", args.Exception,
                $"ThreadId={Environment.CurrentManagedThreadId}");
            WinProvisionLog.Error(args.Exception,
                $"TaskScheduler.UnobservedTaskException (ThreadId={Environment.CurrentManagedThreadId})");
            args.SetObserved();
        };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _memoryMilestoneLog = MemoryMilestoneLog.TryCreate();
        MemoryLog = _memoryMilestoneLog;
        _memoryMilestoneLog?.RecordOnce("onlaunched_start");
#if DEBUG
        long serviceProviderStartedAt = DebugPerformanceTelemetry.Start();
#endif
        _services = BuildServiceProvider();
        IOperationsPanelCatalog operationsPanelCatalog = _services.GetRequiredService<IOperationsPanelCatalog>();
        OperationsPanelResourcePublisher.Publish(Resources, operationsPanelCatalog.Current);
        DispatcherQueue appDispatcher = DispatcherQueue.GetForCurrentThread();
        operationsPanelCatalog.Changed += (_, _) => appDispatcher.TryEnqueue(
            () => OperationsPanelResourcePublisher.Publish(Resources, operationsPanelCatalog.Current));
#if DEBUG
        DebugPerformanceTelemetry.Duration("startup-service-provider-build", serviceProviderStartedAt);
#endif
        _legacyLogMigrationTask = Task.Run(() => AppLogLegacyImporter.ImportOnceAsync(
            AppLogRuntime.Log,
            AppLogLegacyImporter.DefaultLegacyFilePath,
            AppLogLegacyImporter.DefaultCompletionMarkerPath));
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
        _operationsPanelReload = new OperationsPanelReloadService(
            operationsPanelCatalog,
            _services.GetRequiredService<OperationsPanelCatalog>(),
            _services.GetRequiredService<OperationTextProvider>(),
            DispatcherQueue.GetForCurrentThread());
        _operationsPanelReload.Start();
#if DEBUG
        long windowConstructionStartedAt = DebugPerformanceTelemetry.Start();
#endif
        _window = new MainWindow(_services);
#if DEBUG
        DebugPerformanceTelemetry.Duration("startup-mainwindow-construction", windowConstructionStartedAt);
#endif
        _memoryMilestoneLog?.RecordOnce("mainwindow_created");
#if DEBUG
        long startupResolutionStartedAt = DebugPerformanceTelemetry.Start();
#endif
        _services.GetRequiredService<WindowsAppNotificationService>().TryRegister(() =>
            _window.RootElement.DispatcherQueue.TryEnqueue(() => _window.Activate()));
        _services.GetRequiredService<OperationsQueueService>().BatchCompleted += OnOperationBatchCompleted;
        _services.GetRequiredService<SettingsViewModel>().RestartRequested += Settings_RestartRequested;
        _memoryMilestoneLog?.RecordOnce("settings_viewmodel_resolved");
        _memoryMilestoneLog?.RecordOnce("startup_dependencies_resolved");
#if DEBUG
        DebugPerformanceTelemetry.Duration("startup-service-resolution", startupResolutionStartedAt);
#endif
        ConfigureValidationWindow();
        _window.Activate();
        if (!IsSmokeTest)
        {
            if (_services.GetRequiredService<ApplicationPreferencesService>().WingetEnabled)
            {
                _wingetStartupProvisionTask = EnsureWingetAtStartupAsync();
                _ = _wingetStartupProvisionTask.ContinueWith(
                    task => WinProvisionLog.Error(task.Exception!, "Winget.StartupProvision"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }
            _automaticAppUpdateCancellation = new CancellationTokenSource();
            var updateChecks = new AutomaticAppUpdateCheckService(
                _services.GetRequiredService<ApplicationPreferencesService>(),
                _services.GetRequiredService<AppUpdateService>(),
                _services.GetRequiredService<ToastMessageService>());
            _automaticAppUpdateTask = updateChecks.RunAsync(_automaticAppUpdateCancellation.Token);
            _ = _automaticAppUpdateTask.ContinueWith(
                task => WinProvisionLog.Error(task.Exception!, "AppUpdate.AutomaticCheckTask"),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
        if (Environment.GetCommandLineArgs().Contains("--startup-minimized"))
            _window.Minimize();
        _memoryMilestoneLog?.RecordOnce("window_activated");
    }

    private async Task EnsureWingetAtStartupAsync()
    {
        ServiceProvider? services = _services;
        if (services is null) return;
        try
        {
            WingetBootstrapResult result = await services.GetRequiredService<WingetBootstrapper>()
                .EnsureOnceAsync(WinProvisionLog.Write);
            if (result.IsUsable) return;
            string message = string.IsNullOrWhiteSpace(result.ErrorMessage)
                ? "WinGet não está disponível. Abra Gerenciadores de pacotes e selecione Verificar novamente para tentar provisionar winget-cli e suas dependências."
                : $"WinGet não está disponível: {result.ErrorMessage}";
            await services.GetRequiredService<ToastMessageService>().PublishAsync(new Notification(
                Guid.NewGuid().ToString("N"), NotificationCategories.BlockingError, NotificationSeverity.Error,
                "Não foi possível preparar o WinGet", message, DeduplicationKey: "winget-startup-provision",
                Origin: nameof(WingetBootstrapper)));
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Winget.StartupProvision");
            try
            {
                await services.GetRequiredService<ToastMessageService>().PublishAsync(new Notification(
                    Guid.NewGuid().ToString("N"), NotificationCategories.BlockingError, NotificationSeverity.Error,
                    "Não foi possível preparar o WinGet",
                    $"O provisionamento do WinGet falhou: {exception.Message}",
                    DeduplicationKey: "winget-startup-provision", Origin: nameof(WingetBootstrapper)));
            }
            catch (Exception notificationException) { WinProvisionLog.Warning(notificationException, "Winget.StartupNotification"); }
        }
    }

    private void OnOperationBatchCompleted(IReadOnlyList<WinProvision.Core.Models.OperationItem> items)
    {
        int failed = items.Count(item => item.State == WinProvision.Core.Models.OperationState.Failed);
        int completed = items.Count(item => item.State == WinProvision.Core.Models.OperationState.Completed);
        int canceled = items.Count - failed - completed;
        if (failed == 0 && completed == 0)
            return;

        IOperationTextProvider texts = _services?.GetRequiredService<IOperationTextProvider>()
            ?? throw new InvalidOperationException("O provedor de textos ainda não foi inicializado.");
        string message = string.Join("; ",
            texts.FormatPlural("operations.batch.completed", completed),
            texts.FormatPlural("operations.batch.failed", failed),
            texts.FormatPlural("operations.batch.canceled", canceled)) + ".";
        var notification = new Notification(
            Id: Guid.NewGuid().ToString("N"),
            Category: failed > 0 ? NotificationCategories.BlockingError : NotificationCategories.OperationResult,
            Severity: failed > 0 ? NotificationSeverity.Error : NotificationSeverity.Success,
            Title: texts.GetText("operations.batch.title"),
            Message: message,
            DeduplicationKey: $"operation-batch:{items.FirstOrDefault()?.Id:N}",
            Origin: nameof(OperationsQueueService));
        _ = PublishNotificationAsync(notification);
    }

    private async Task PublishNotificationAsync(Notification notification)
    {
        try
        {
            if (_services is not null)
                await _services.GetRequiredService<ToastMessageService>().PublishAsync(notification);
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "Notifications.Publish");
        }
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
            await AppLogRuntime.DisposeAsync();
            Exit();
        }
    }

}
