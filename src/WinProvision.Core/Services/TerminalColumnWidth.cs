using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace WinProvision.Core.Services;

/// <summary>Conta células de terminal por grafema, como as tabelas do gerenciador de pacotes.</summary>
internal static partial class TerminalColumnWidth
{
    public static int Measure(string text)
    {
        int width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext()) width += ElementWidth(text.AsSpan(elements.ElementIndex));
        return width;
    }

    public static int[] GetStringOffsets(string text, int[] columns)
    {
        var offsets = new int[columns.Length];
        int column = 0, next = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            int index = elements.ElementIndex;
            while (next < columns.Length && columns[next] <= column) offsets[next++] = index;
            column += ElementWidth(text.AsSpan(index));
        }
        while (next < columns.Length) offsets[next++] = text.Length;
        return offsets;
    }

    private static int ElementWidth(ReadOnlySpan<char> element)
    {
        if (element[0] < 0x80) return 1;
        Rune.DecodeFromUtf16(element, out var rune, out _);
        int value = rune.Value;
        // gerenciador de pacotes also uses ICU East_Asian_Width for the first code point of each grapheme.
        // The system library is available without COM initialization starting in Windows 10 1903.
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            int width = GetIntPropertyValue(value, 0x1004); // UCHAR_EAST_ASIAN_WIDTH
            return width is 3 or 5 ? 2 : 1; // Fullwidth or Wide
        }
        // Portable fallback for older Windows and non-Windows Core consumers.
        return value is >= 0x1100 and <= 0x115F or 0x2329 or 0x232A
            or >= 0x2E80 and <= 0xA4CF and not 0x303F
            or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
            or >= 0xFE10 and <= 0xFE19 or >= 0xFE30 and <= 0xFE6F
            or >= 0xFF01 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6
            or >= 0x1F300 and <= 0x1FAFF or >= 0x20000 and <= 0x3FFFD ? 2 : 1;
    }

    [LibraryImport("icu.dll", EntryPoint = "u_getIntPropertyValue")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int GetIntPropertyValue(int codePoint, int property);
}
