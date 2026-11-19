using System.Runtime.InteropServices;
using Xunit;

namespace WinProvision.Core.Tests;

/// <summary>Small authoring-only MSI fixtures. No installation session or installer is ever started.</summary>
internal static class MsiInspectionFixture
{
    internal static void Create(string path, string? allUsers, string? perUser, int? wordCount,
        bool propertyTable = true, bool summaryInformation = true)
    {
        // MSIDBOPEN_CREATE = 3, Windows SDK 10.0.26100.0/um/MsiQuery.h:35.
        Ok(MsiOpenDatabase(path, new IntPtr(3), out uint database));
        try
        {
            if (propertyTable)
            {
                Execute(database, "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` CHAR(0) NOT NULL LOCALIZABLE PRIMARY KEY `Property`)");
                if (!string.IsNullOrEmpty(allUsers)) Insert(database, "ALLUSERS", allUsers);
                if (!string.IsNullOrEmpty(perUser)) Insert(database, "MSIINSTALLPERUSER", perUser);
            }
            if (summaryInformation)
            {
                Ok(MsiGetSummaryInformation(database, null, 4, out uint summary));
                try
                {
                    long fileTime = 0;
                    Ok(MsiSummaryInfoSetProperty(summary, 7, (uint)VarEnum.VT_LPSTR, 0, ref fileTime, ";1033"));
                    Ok(MsiSummaryInfoSetProperty(summary, 9, (uint)VarEnum.VT_LPSTR, 0, ref fileTime, Guid.NewGuid().ToString("B")));
                    Ok(MsiSummaryInfoSetProperty(summary, 14, (uint)VarEnum.VT_I4, 500, ref fileTime, ""));
                    if (wordCount.HasValue)
                        Ok(MsiSummaryInfoSetProperty(summary, 15, (uint)VarEnum.VT_I4, wordCount.Value, ref fileTime, ""));
                    Ok(MsiSummaryInfoPersist(summary));
                }
                finally { Ok(MsiCloseHandle(summary)); }
            }
            Ok(MsiDatabaseCommit(database));
        }
        finally { Ok(MsiCloseHandle(database)); }
    }

    private static void Insert(uint database, string property, string value)
    {
        Assert.DoesNotContain("'", value, StringComparison.Ordinal); // fixed test data; MSI SQL has no quote escaping
        Execute(database, $"INSERT INTO `Property` (`Property`,`Value`) VALUES ('{property}','{value}')");
    }
    private static void Execute(uint database, string query)
    {
        Ok(MsiDatabaseOpenView(database, query, out uint view));
        try { Ok(MsiViewExecute(view, 0)); }
        finally { Ok(MsiCloseHandle(view)); }
    }
    private static void Ok(uint code) => Assert.Equal(0u, code);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiOpenDatabaseW")]
    private static extern uint MsiOpenDatabase(string path, IntPtr persist, out uint database);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiDatabaseOpenViewW")]
    private static extern uint MsiDatabaseOpenView(uint database, string query, out uint view);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiViewExecute(uint view, uint record);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiGetSummaryInformationW")]
    private static extern uint MsiGetSummaryInformation(uint database, string? path, uint count, out uint summary);
    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiSummaryInfoSetPropertyW")]
    private static extern uint MsiSummaryInfoSetProperty(uint summary, uint property, uint type, int integer, ref long fileTime, string value);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiSummaryInfoPersist(uint summary);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiDatabaseCommit(uint database);
    [DllImport("msi.dll", ExactSpelling = true)]
    private static extern uint MsiCloseHandle(uint handle);
}
