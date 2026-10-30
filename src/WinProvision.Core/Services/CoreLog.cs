using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WinProvision.Core.Services;

/// <summary>Host-owned structured diagnostics; standalone tools retain a Trace fallback.</summary>
public static class CoreLog
{
    private static ILogger? _logger;

    public static void Configure(ILogger logger) =>
        Volatile.Write(ref _logger, logger ?? throw new ArgumentNullException(nameof(logger)));

    public static void Error(Exception exception, string operation)
    {
        var logger = Volatile.Read(ref _logger);
        if (logger is not null) logger.LogError("Falha na operação {Operation}: {Error}", operation, SensitiveDataRedactor.Redact(exception.ToString()));
        else Trace.TraceError("{0}: {1}", operation, SensitiveDataRedactor.Redact(exception.ToString()));
    }

    public static void Warning(Exception exception, string operation)
    {
        var logger = Volatile.Read(ref _logger);
        if (logger is not null) logger.LogWarning("Falha recuperável na operação {Operation}: {Error}", operation, SensitiveDataRedactor.Redact(exception.ToString()));
        else Trace.TraceWarning("{0}: {1}", operation, SensitiveDataRedactor.Redact(exception.ToString()));
    }

    public static void Information(string operation, string message)
    {
        var logger = Volatile.Read(ref _logger);
        if (logger is not null)
            logger.LogInformation("Operação {Operation}: {Message}", operation, SensitiveDataRedactor.Redact(message));
        else
            Trace.TraceInformation("{0}: {1}", operation, SensitiveDataRedactor.Redact(message));
    }

    public static void Debug(string operation, string message)
    {
        var logger = Volatile.Read(ref _logger);
        if (logger is not null)
            logger.LogDebug("Diagnóstico {Operation}: {Message}", operation, SensitiveDataRedactor.Redact(message));
        else
            Trace.WriteLine($"{operation}: {SensitiveDataRedactor.Redact(message)}");
    }

    public static void Debug(string message) =>
        Debug("General", message);
}
