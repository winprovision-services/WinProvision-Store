using System.Diagnostics;
using System.Security.Principal;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

public static class PackageDiagnostics
{
    private static Action<string>? _write;
    private static Action? _serverInfo;
    public static void Configure(Action<string> write, Action serverInfo)
    {
        _write = write;
        _serverInfo = serverInfo;
    }
    public static void Write(string message)
    {
        string safe = SensitiveDataRedactor.Redact(message);
        if (_write is { } sink) sink(safe); else Trace.WriteLine(safe);
    }
    public static void Warning(Exception exception, string operation) => CoreLog.Warning(exception, operation);
    public static void Error(Exception exception, string operation) => CoreLog.Error(exception, operation);
    public static void WriteComServerInfo() => _serverInfo?.Invoke();
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
