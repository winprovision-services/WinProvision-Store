using System.Text.RegularExpressions;
using WinProvision.Providers.WinGet.Cli.Contracts;

namespace WinProvision.Providers.WinGet.Cli.Parsing;

public static partial class WinGetCliParsing
{
    public const int RawOutputLimit = 16_384;

    public static string BoundSummary(string value, int maximum = RawOutputLimit)
    {
        if (maximum < 64) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (value.Length <= maximum) return value;
        int half = (maximum - 32) / 2;
        return value[..half] + "\n… saída truncada …\n" + value[^half..];
    }

    public static WinGetCliEvent ParseProgressLine(string line, bool isError = false)
    {
        string clean = ControlChars().Replace(line, string.Empty).Trim();
        double? percent = null;
        var match = Percent().Match(clean);
        if (match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out double parsed) && parsed is >= 0 and <= 100)
            percent = parsed;
        WinGetCliPhase phase = clean.Contains("search", StringComparison.OrdinalIgnoreCase) || clean.Contains("busc", StringComparison.OrdinalIgnoreCase)
            ? WinGetCliPhase.Searching
            : clean.Contains("download", StringComparison.OrdinalIgnoreCase) || clean.Contains("baix", StringComparison.OrdinalIgnoreCase)
            ? WinGetCliPhase.Downloading
            : clean.Contains("hash", StringComparison.OrdinalIgnoreCase) || clean.Contains("verific", StringComparison.OrdinalIgnoreCase)
                ? WinGetCliPhase.Verifying
                : clean.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || clean.Contains("desinst", StringComparison.OrdinalIgnoreCase)
                    ? WinGetCliPhase.Uninstalling
                    : clean.Contains("install", StringComparison.OrdinalIgnoreCase) || clean.Contains("instal", StringComparison.OrdinalIgnoreCase)
                        ? WinGetCliPhase.Installing : WinGetCliPhase.Started;
        return new(phase, percent, clean, isError);
    }

    public static WinGetCliStatus MapExitCode(int code, WinGetCliOperation operation)
    {
        uint value = unchecked((uint)code);
        if (code == 0 || code is 3010 or 1641 || value is 0x8A150109 or 0x8A15010B) return WinGetCliStatus.Success;
        if (value == 0x8A150014) return operation is WinGetCliOperation.UpgradeList ? WinGetCliStatus.NoUpdates : WinGetCliStatus.PackageNotFound;
        if (value == 0x8A150045 || value == 0x8A15000F) return WinGetCliStatus.SourceFailed;
        if (value == 0x8A150011) return WinGetCliStatus.HashMismatch;
        return WinGetCliStatus.InstallerFailed;
    }

    [GeneratedRegex(@"\x1B\[[0-?]*[ -/]*[@-~]|[\u0000-\u0008\u000B\u000C\u000E-\u001F]")]
    private static partial Regex ControlChars();
    [GeneratedRegex(@"(?<!\d)(\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex Percent();
}
