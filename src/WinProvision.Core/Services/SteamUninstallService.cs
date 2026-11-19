using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinProvision.Core.Services;

/// <summary>Acompanha o registro do aplicativo, sem controlar a duração do processo compartilhado.</summary>
public static class SteamUninstallService
{
    public static bool IsGameUninstall(string command) => TryGetCommand(command, out _, out _, out _);

    private static bool TryGetCommand(string command, out string executable, out string arguments, out string appId)
    {
        appId = string.Empty;
        if (!UninstallerEngineService.TrySplitCommand(command, out executable, out arguments)
            || !Path.GetFileName(executable).Equals("steam.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var match = Regex.Match(arguments, @"^(?:steam://uninstall/|-?uninstall\s+)([0-9]+)\s*$", RegexOptions.IgnoreCase);
        if (!match.Success || !uint.TryParse(match.Groups[1].Value, out uint id) || id == 0) return false;
        appId = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    public static async Task<bool> UninstallAsync(string command, CancellationToken cancellationToken)
    {
        if (!TryGetCommand(command, out string executable, out string arguments, out string appId))
            throw new ArgumentException("Comando de remoção Steam inválido.", nameof(command));
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsRegistered(appId)) return true;
        using var process = Process.Start(new ProcessStartInfo(executable, arguments)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        });
        // O aplicativo pode reutilizar um processo existente ou permanecer aberto após a remoção.
        return await WaitForRemovalAsync(() => IsRegistered(appId), TimeSpan.FromMinutes(30),
            TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRegistered(string appId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var game = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App " + appId);
            if (game is not null) return true;
        }
        return false;
    }

    internal static async Task<bool> WaitForRemovalAsync(Func<bool> isRegistered, TimeSpan timeout,
        TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!isRegistered()) return true;
            if (elapsed.Elapsed >= timeout) return false;
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
