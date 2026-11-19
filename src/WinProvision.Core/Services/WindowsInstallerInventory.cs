using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Collections.Concurrent;

namespace WinProvision.Core.Services;

/// <summary>Reads the platform of the registered MSI, rather than an EXE bootstrapper.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsInstallerInventory
{
    private static readonly ConcurrentDictionary<string, string> Architectures = new(StringComparer.OrdinalIgnoreCase);

    internal sealed record PackageIdentity(string ProductCode, string Version);

    /// <summary>Reads the hash-verified MSI in read-only mode, without installing it.</summary>
    internal static PackageIdentity? ReadPackageIdentity(string path)
    {
        if (MsiOpenDatabase(path, IntPtr.Zero, out uint database) != 0) return null;
        try
        {
            string? code = ReadProperty(database, "SELECT `Value` FROM `Property` WHERE `Property`='ProductCode'");
            string? version = ReadProperty(database, "SELECT `Value` FROM `Property` WHERE `Property`='ProductVersion'");
            return Guid.TryParse(code, out _) && !string.IsNullOrWhiteSpace(version) ? new(code!, version) : null;
        }
        finally { MsiCloseHandle(database); }
    }

    private static string? ReadProperty(uint database, string query)
    {
        if (MsiDatabaseOpenView(database, query, out uint view) != 0) return null;
        try
        {
            if (MsiViewExecute(view, 0) != 0 || MsiViewFetch(view, out uint record) != 0) return null;
            try
            {
                uint length = 0;
                uint result = MsiRecordGetString(record, 1, null, ref length);
                if (result is not (0 or 234) || length == 0) return null;
                var value = new StringBuilder(checked((int)length + 1));
                length++;
                return MsiRecordGetString(record, 1, value, ref length) == 0 ? value.ToString() : null;
            }
            finally { MsiCloseHandle(record); }
        }
        finally { MsiCloseHandle(view); }
    }

    internal static string? ReadArchitecture(string productCode)
    {
        if (Architectures.TryGetValue(productCode, out var cached)) return cached;
        string? architecture = ReadRegisteredArchitecture(productCode);
        if (architecture is not null) Architectures.TryAdd(productCode, architecture);
        return architecture;
    }

    private static string? ReadRegisteredArchitecture(string productCode)
    {
        if (!Guid.TryParse(productCode, out _)) return null;
        uint length = 0;
        uint result = MsiGetProductInfo(productCode, "LocalPackage", null, ref length);
        if (result is not (0 or 234) || length == 0) return null;
        var path = new StringBuilder(checked((int)length + 1));
        length++;
        if (MsiGetProductInfo(productCode, "LocalPackage", path, ref length) != 0) return null;
        if (MsiGetSummaryInformation(0, path.ToString(), 0, out uint summary) != 0) return null;
        try
        {
            uint textLength = 0;
            int integer = 0;
            long fileTime = 0;
            result = MsiSummaryInfoGetProperty(summary, 7, out _, ref integer, ref fileTime, null, ref textLength);
            if (result is not (0 or 234) || textLength == 0) return null;
            var template = new StringBuilder(checked((int)textLength + 1));
            textLength++;
            if (MsiSummaryInfoGetProperty(summary, 7, out _, ref integer, ref fileTime, template, ref textLength) != 0) return null;
            return template.ToString().Split(';')[0].ToLowerInvariant() switch
            {
                "intel" => "x86", "x64" or "intel64" => "x64", "arm64" => "arm64", "arm" => "arm", _ => null
            };
        }
        finally { MsiCloseHandle(summary); }
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetProductInfoW")]
    private static extern uint MsiGetProductInfo(string product, string property, StringBuilder? value, ref uint length);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiGetSummaryInformationW")]
    private static extern uint MsiGetSummaryInformation(uint database, string path, uint updateCount, out uint summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiSummaryInfoGetPropertyW")]
    private static extern uint MsiSummaryInfoGetProperty(uint summary, uint property, out uint type,
        ref int integer, ref long fileTime, StringBuilder? value, ref uint length);
    [DllImport("msi.dll")]
    private static extern uint MsiCloseHandle(uint handle);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll")]
    private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll")]
    private static extern uint MsiViewFetch(uint view, out uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiRecordGetStringW")]
    private static extern uint MsiRecordGetString(uint record, uint field, StringBuilder? value, ref uint length);
}
