using System.Runtime.InteropServices;
using WinProvision.Core.Services;
using Xunit;

namespace WinProvision.Core.Tests;

public sealed class WingetArgumentTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Test.Package")]
    [InlineData("Source with spaces")]
    [InlineData("C:\\Folder with spaces\\")]
    [InlineData("value\\\" --ignore-security-hash injected")]
    [InlineData("\" --ignore-security-hash")]
    [InlineData("argument ending in \\\\")]
    public void QuotedValueRemainsOneLiteralWindowsArgument(string value)
    {
        nint arguments = CommandLineToArgvW("test.exe " + WingetExecutor.QuoteArgument(value), out int count);
        Assert.NotEqual(nint.Zero, arguments);
        try
        {
            Assert.Equal(2, count);
            Assert.Equal(value, Marshal.PtrToStringUni(Marshal.ReadIntPtr(arguments, nint.Size)));
        }
        finally { _ = LocalFree(arguments); }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint pointer);
}
