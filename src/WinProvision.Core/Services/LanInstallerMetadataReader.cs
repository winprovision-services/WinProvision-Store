using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace WinProvision.Core.Services;

public sealed record LanInstallerMetadata(string Name, string? Version);

public static partial class LanInstallerMetadataReader
{
    [GeneratedRegex(@"^(x86|x64|x86_64|amd64|arm|arm64|aarch64|machine|user|system|portable|[a-z]{2,3}[-_][a-z]{2,4}|neutral)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FilenameTokenPattern();

    public static LanInstallerMetadata Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fallbackName = CleanFileName(Path.GetFileNameWithoutExtension(path));
        if (Path.GetExtension(path).Equals(".msi", StringComparison.OrdinalIgnoreCase))
        {
            (string? name, string? version) = ReadMsiProperties(path);
            return new(NonEmpty(name) ?? fallbackName, NormalizeVersion(version));
        }

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
            return new(NonEmpty(info.ProductName) ?? fallbackName,
                NormalizeVersion(NonEmpty(info.ProductVersion) ?? NonEmpty(info.FileVersion)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(fallbackName, null);
        }
    }

    public static string CleanFileName(string stem)
    {
        string[] tokens = stem.Split('_', StringSplitOptions.RemoveEmptyEntries);
        int end = tokens.Length;
        while (end > 1 && FilenameTokenPattern().IsMatch(tokens[end - 1]))
            end--;
        string result = string.Join(' ', tokens.Take(end)).Trim();
        return result.Length == 0 ? stem.Replace('_', ' ').Trim() : result;
    }

    public static string? NormalizeVersion(string? value)
    {
        string? normalized = NonEmpty(value);
        if (normalized is null || normalized.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            || normalized is "0" or "0.0" or "0.0.0" or "0.0.0.0")
            return null;
        string safe = new(normalized.TakeWhile(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_').ToArray());
        return LanInstallerValidation.IsSafeVersion(safe) ? safe : null;
    }

    private static (string? Name, string? Version) ReadMsiProperties(string path)
    {
        if (!OperatingSystem.IsWindows() || MsiOpenDatabase(path, IntPtr.Zero, out uint database) != 0)
            return (null, null);
        try
        {
            return (
                ReadMsiProperty(database, "ProductName"),
                ReadMsiProperty(database, "ProductVersion"));
        }
        finally { MsiCloseHandle(database); }
    }

    private static string? ReadMsiProperty(uint database, string property)
    {
        string query = $"SELECT `Value` FROM `Property` WHERE `Property`='{property}'";
        if (MsiDatabaseOpenView(database, query, out uint view) != 0)
            return null;
        try
        {
            if (MsiViewExecute(view, IntPtr.Zero) != 0 || MsiViewFetch(view, out uint record) != 0)
                return null;
            try
            {
                uint length = 0;
                uint result = MsiRecordGetString(record, 1, null, ref length);
                if (result is not (0 or 234) || length == 0)
                    return null;
                var value = new StringBuilder(checked((int)length + 1));
                length++;
                return MsiRecordGetString(record, 1, value, ref length) == 0 ? value.ToString() : null;
            }
            finally { MsiCloseHandle(record); }
        }
        finally { MsiCloseHandle(view); }
    }

    private static string? NonEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll", EntryPoint = "MsiViewExecute")]
    private static extern uint MsiViewExecute(uint view, IntPtr record);
    [DllImport("msi.dll", EntryPoint = "MsiViewFetch")]
    private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder? value, ref uint length);
    [DllImport("msi.dll", EntryPoint = "MsiCloseHandle")]
    private static extern uint MsiCloseHandle(uint handle);
}
