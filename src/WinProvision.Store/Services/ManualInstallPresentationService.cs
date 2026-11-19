using System.Diagnostics;
using System.Text;
using WinProvision.Core.Models;
using WinProvision.Core.Services;
namespace WinProvision.Store.Services;
public sealed class ManualInstallPresentationService(IClipboardService clipboard, PackageInstallOptionsService installOptions)
{
    private readonly IClipboardService _clipboard = clipboard;
    private readonly PackageInstallOptionsService _installOptions = installOptions;
    public string Open(AppEntry app)
    {
        string command;
        try
        {
            command = ManualInstallCommandBuilder.BuildInstallCommand(app, _installOptions.Get(app.Id, app.Source));
        }
        catch (Exception exception)
        {
            return exception.Message;
        }
        bool copied = _clipboard.TrySetText(command);
        try
        {
            string literal = command.Replace("'", "''", StringComparison.Ordinal);
            string script = "$ErrorActionPreference = 'Continue'\n$cmd = '" + literal + "'\n"
                          + "Set-Clipboard -Value $cmd\n"
                          + ConsolePasteInjector + "\n[WinProvisionConsolePaste]::Schedule()\n";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            Process.Start(new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = $"-NoLogo -NoExit -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = true,
            });
            return copied ? "Comando manual aberto no PowerShell." : "Comando aberto no PowerShell; ele também será copiado para a área de transferência.";
        }
        catch (Exception exception)
        {
            WinProvisionLog.Warning(exception, "HomePage.OpenManualInstall");
            return copied
                ? "Não foi possível abrir o PowerShell. O comando foi copiado para a área de transferência."
                : "Não foi possível abrir o PowerShell nem copiar o comando.";
        }
    }

    private const string ConsolePasteInjector = """
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Threading;
public static class WinProvisionConsolePaste {
    [StructLayout(LayoutKind.Explicit)] struct INPUT_RECORD { [FieldOffset(0)] public ushort EventType; [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent; }
    [StructLayout(LayoutKind.Sequential)] struct KEY_EVENT_RECORD { public int KeyDown; public ushort Repeat; public ushort VirtualKey; public ushort ScanCode; public char Character; public uint ControlState; }
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool WriteConsoleInputW(IntPtr handle, INPUT_RECORD[] records, uint count, out uint written);
    static Timer timer;
    public static void Schedule() { timer = new Timer(new TimerCallback(Paste), null, 1500, Timeout.Infinite); }
    static void Paste(object state) {
        IntPtr handle = GetStdHandle(-10);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        const uint leftControlPressed = 0x0008;
        var records = new INPUT_RECORD[4];
        records[0] = Key(0x11, 0x1D, true, leftControlPressed);
        records[1] = Key(0x56, 0x2F, true, leftControlPressed);
        records[2] = Key(0x56, 0x2F, false, leftControlPressed);
        records[3] = Key(0x11, 0x1D, false, 0);
        uint written;
        WriteConsoleInputW(handle, records, (uint)records.Length, out written);
    }
    static INPUT_RECORD Key(ushort key, ushort scan, bool down, uint state) {
        INPUT_RECORD record = new INPUT_RECORD();
        record.EventType = 1;
        record.KeyEvent = new KEY_EVENT_RECORD { KeyDown = down ? 1 : 0, Repeat = 1, VirtualKey = key, ScanCode = scan, ControlState = state };
        return record;
    }
}
"@
""";

}
