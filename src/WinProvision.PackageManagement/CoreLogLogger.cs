using Microsoft.Extensions.Logging;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

/// <summary>Routes COM provider exceptions through the application's structured diagnostic log.</summary>
internal sealed class CoreLogLogger : ILogger
{
    internal static CoreLogLogger Instance { get; } = new();

    private CoreLogLogger() { }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (exception is null || !IsEnabled(logLevel)) return;
        string operation = $"WinGetCom.{eventId.Name ?? "Provider"}";
        string message = formatter(state, exception);
        if (logLevel >= LogLevel.Error) CoreLog.Error(exception, $"{operation}: {message}");
        else CoreLog.Warning(exception, $"{operation}: {message}");
    }
}
