using System.Diagnostics;
using System.Text.Json;

namespace WinProvision.Core.Services;

/// <summary>Host-owned structured diagnostics; standalone tools retain a Trace fallback.</summary>
public static class CoreLog
{
    private static IAppLog? _logger;

    public static void Configure(IAppLog logger) =>
        Volatile.Write(ref _logger, logger ?? throw new ArgumentNullException(nameof(logger)));

    public static void Error(Exception exception, string operation) =>
        WriteException(AppLogLevel.Error, exception, operation);

    public static void Warning(Exception exception, string operation) =>
        WriteException(AppLogLevel.Warning, exception, operation);

    public static void Information(string operation, string message, JsonElement? data = null)
    {
        if (Volatile.Read(ref _logger) is { } logger)
            logger.Write(AppLogLevel.Info, "App", operation, message, data: data);
        else
            Trace.TraceInformation("{0}: {1}", operation, SensitiveDataRedactor.Redact(message));
    }

    public static void Debug(string operation, string message)
    {
        if (Volatile.Read(ref _logger) is { } logger)
            logger.Write(AppLogLevel.Debug, "App", operation, message);
        else
            Trace.WriteLine($"{operation}: {SensitiveDataRedactor.Redact(message)}");
    }

    public static void Debug(string message) =>
        Debug("General", message);

    private static void WriteException(AppLogLevel level, Exception exception, string operation)
    {
        if (Volatile.Read(ref _logger) is { } logger)
            logger.Write(level, "App", operation, exception.Message, exception: exception);
        else
            Trace.WriteLine($"{operation}: {SensitiveDataRedactor.Redact(exception.ToString())}");
    }
}
