using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public static class LanInstallerValidation
{
    private static readonly HashSet<string> SupportedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "nullsoft", "inno", "msi", "exe", "wix", "burn"
    };

    public static bool IsSafeFileName(string? file)
    {
        if (string.IsNullOrWhiteSpace(file) || file.Length > 255 || file is "." or ".."
            || file != file.Trim() || file.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0
            || file.EndsWith('.') || file.Any(char.IsControl))
            return false;

        if (!Path.GetFileName(file).Equals(file, StringComparison.Ordinal))
            return false;
        string stem = file.Split('.')[0];
        return !IsWindowsDeviceName(stem);
    }

    public static bool IsValidSha256(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    public static bool IsSafePackageId(string? packageId) =>
        packageId is { Length: > 0 and <= 256 }
        && char.IsAsciiLetterOrDigit(packageId[0])
        && packageId.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    public static bool IsSafeVersion(string? version) =>
        version is { Length: > 0 and <= 128 }
        && char.IsAsciiLetterOrDigit(version[0])
        && version.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '+' or '_');

    public static bool IsSafeSilentArguments(string? arguments) =>
        !string.IsNullOrWhiteSpace(arguments)
        && arguments.Length <= 4096
        && !arguments.Any(character => char.IsControl(character)
            || character is '&' or '|' or ';' or '`' or '$' or '<' or '>' or '\r' or '\n');

    private static bool IsWindowsDeviceName(string value) =>
        value.Equals("CON", StringComparison.OrdinalIgnoreCase)
        || value.Equals("PRN", StringComparison.OrdinalIgnoreCase)
        || value.Equals("AUX", StringComparison.OrdinalIgnoreCase)
        || value.Equals("NUL", StringComparison.OrdinalIgnoreCase)
        || value.Length == 4 && (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && value[3] is >= '1' and <= '9';

    public static LanInstallerAvailability ValidateInstaller(LanInstallerEntry installer)
    {
        ArgumentNullException.ThrowIfNull(installer);
        if (!IsSafeFileName(installer.File))
            throw new InvalidDataException("O nome do instalador precisa ser apenas um nome de arquivo, sem caminho.");
        if (!IsValidSha256(installer.Sha256))
            throw new InvalidDataException($"SHA-256 inválido para {installer.File}.");
        if (!IsSafePackageId(installer.PackageId))
            throw new InvalidDataException($"Identificador de pacote inválido para {installer.File}.");
        if (!IsSafeVersion(installer.Version))
            throw new InvalidDataException($"Versão inválida para {installer.File}.");
        if (installer.Scope is not null && installer.Scope is not ("user" or "machine"))
            throw new InvalidDataException($"Escopo inválido para {installer.File}.");
        if (!string.IsNullOrWhiteSpace(installer.SilentArgs) && !IsSafeSilentArguments(installer.SilentArgs))
            throw new InvalidDataException($"Argumentos silenciosos inválidos para {installer.File}.");

        if (installer.InstallerType is null)
            return new(installer, false, "Tipo de instalador não informado.");
        if (!SupportedTypes.Contains(installer.InstallerType))
            throw new InvalidDataException($"Tipo de instalador inválido para {installer.File}.");
        if (string.IsNullOrWhiteSpace(installer.SilentArgs))
            return new(installer, false, "Argumentos silenciosos não informados.");

        installer.InstallerType = installer.InstallerType.ToLowerInvariant();
        return new(installer, true, null);
    }

    public static LanInstallersModuleDocument DeserializeModule(ReadOnlySpan<byte> content, bool serverSource = false)
    {
        LanInstallersModuleDocument? module;
        try
        {
            module = System.Text.Json.JsonSerializer.Deserialize(content, CoreJsonContext.Default.LanInstallersModuleDocument);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("O módulo installers contém JSON inválido.", exception);
        }

        if (module is null || module.Schema != "winprovision.lan.installers" || module.SchemaVersion != 1
            || module.Installers is null || module.Installers.Any(installer => installer is null))
            throw new InvalidDataException("O módulo installers usa uma versão ou estrutura incompatível.");

        foreach (LanInstallerEntry installer in module.Installers)
        {
            if (serverSource && !IsValidSha256(installer.Sha256))
                installer.Sha256 = new string('0', 64);
            _ = ValidateInstaller(installer);
        }
        return module;
    }
}
