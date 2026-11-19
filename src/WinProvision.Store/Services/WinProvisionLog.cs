using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

internal static class WinProvisionLog
{
    private static IAppLog? _logger;

    public static string FilePath => AppLogPaths.GetLogFilePath(DateTimeOffset.UtcNow);

    public static void Configure(IAppLog logger) =>
        Volatile.Write(ref _logger, logger ?? throw new ArgumentNullException(nameof(logger)));

    public static void Warning(Exception exception, string operation) =>
        WriteException(AppLogLevel.Warning, exception, operation);

    public static void Error(Exception exception, string operation) =>
        WriteException(AppLogLevel.Error, exception, operation);

    public static void Write(string message) =>
        Log.Write(AppLogLevel.Info, "App", "Store", message);

    public static void WriteComServerInfo() =>
        Log.Write(AppLogLevel.Info, "Manager", "PackageManagerDiagnostics", "Informações do servidor de pacotes solicitadas.");

    public static void WritePackageOperation(string? source, string message, bool failed = false)
    {
        string? category = PackageManagerLogCategories.ForSource(source);
        if (category is null) return;
        Log.Write(failed ? AppLogLevel.Warning : AppLogLevel.Info, category, "PackageOperation", message);
    }

    private static IAppLog Log => Volatile.Read(ref _logger) ?? AppLogRuntime.Log;

    private static void WriteException(AppLogLevel level, Exception exception, string operation) =>
        Log.Write(level, "App", operation, exception.Message, exception: exception);
}
