using System.Text;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

internal static class UnhandledExceptionLog
{
    private static readonly object Gate = new();

    public static void Write(string origin, Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string directory = AppLogPaths.GetLogDirectory();
        string path = Path.Combine(directory, $"crash-{DateTimeOffset.UtcNow:yyyy-MM-dd}.log");
        string text = $"[{DateTimeOffset.UtcNow:O}] origin={origin} thread={Environment.CurrentManagedThreadId}{Environment.NewLine}"
            + (string.IsNullOrWhiteSpace(context) ? string.Empty : $"context: {context}{Environment.NewLine}")
            + FormatException(exception)
            + Environment.NewLine + new string('-', 80) + Environment.NewLine;

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                byte[] bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Exception reporting must never throw back into an unhandled-exception callback.
        }
    }

    public static void WriteNonException(string origin, object? exceptionObject, string? context = null) =>
        Write(origin, new InvalidOperationException($"Objeto de exceção não gerenciado: {exceptionObject ?? "null"}"), context);

    private static string FormatException(Exception exception)
    {
        var builder = new StringBuilder();
        AppendException(builder, exception, "exception", new HashSet<Exception>(ReferenceEqualityComparer.Instance));
        return builder.ToString();
    }

    private static void AppendException(StringBuilder builder, Exception exception, string label, HashSet<Exception> seen)
    {
        if (!seen.Add(exception))
        {
            builder.AppendLine($"{label}: <ciclo de exceções>");
            return;
        }

        builder.AppendLine($"{label}.type: {exception.GetType().FullName ?? exception.GetType().Name}");
        builder.AppendLine($"{label}.message: {exception.Message}");
        builder.AppendLine($"{label}.stackTrace:");
        builder.AppendLine(exception.StackTrace ?? "<sem stack trace>");
        if (exception is AggregateException aggregate)
        {
            for (int index = 0; index < aggregate.InnerExceptions.Count; index++)
                AppendException(builder, aggregate.InnerExceptions[index], $"{label}.inner[{index}]", seen);
        }
        else if (exception.InnerException is { } inner)
        {
            AppendException(builder, inner, $"{label}.inner", seen);
        }
    }
}
