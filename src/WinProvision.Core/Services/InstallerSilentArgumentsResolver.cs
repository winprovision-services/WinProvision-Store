using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

public enum InstallerSilentArgumentSource
{
    Unsupported,
    Manual,
    Manifest,
    TypeDefault,
    Detected,
    Server,
    Zip,
    Collection
}

public sealed record InstallerSilentArgumentResolution(
    string? Arguments,
    InstallerSilentArgumentSource Source,
    string? RejectionReason = null);

public static class InstallerSilentArgumentsResolver
{
    public const string InternetRequiredMessage =
        "Este instalador baixa componentes ao executar; a máquina alvo precisa de internet";

    private static readonly IReadOnlyDictionary<string, string> SilentDefaults =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["nullsoft"] = "/S",
            ["inno"] = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
            ["wix"] = "/qn /norestart",
            ["wixmsi"] = "/qn /norestart",
            ["msi"] = "/qn /norestart",
            ["burn"] = "/quiet /norestart"
        };

    public static IReadOnlyDictionary<string, string> DefaultArgumentsByInstallerType => SilentDefaults;

    public static string? DefaultInstallArguments(string? installerType) =>
        installerType?.Trim() is { Length: > 0 } type && SilentDefaults.TryGetValue(type, out string? arguments)
            ? arguments
            : null;

    public static InstallerSilentArgumentResolution Resolve(
        string? manualArguments,
        string? manifestSilent,
        string? manifestSilentWithProgress,
        string? manifestInstallerType,
        string? detectedInstallerType = null,
        InstallerSilentArgumentSource providedSource = InstallerSilentArgumentSource.Manifest,
        bool allowManualArguments = true,
        bool allowExternalArguments = true)
    {
        if (allowManualArguments)
        {
            InstallerSilentArgumentResolution? manual = ResolveProvided(manualArguments, InstallerSilentArgumentSource.Manual);
            if (manual is not null)
                return manual;
        }

        if (allowExternalArguments && IsExternalSource(providedSource))
        {
            InstallerSilentArgumentResolution? external = ResolveProvided(manifestSilent, providedSource)
                ?? ResolveProvided(manifestSilentWithProgress, providedSource);
            if (external is not null)
                return external;
        }

        string? declaredDefault = DefaultInstallArguments(manifestInstallerType);
        if (declaredDefault is not null)
            return new(declaredDefault, InstallerSilentArgumentSource.TypeDefault);

        string? detectedDefault = DefaultInstallArguments(detectedInstallerType);
        return detectedDefault is null
            ? new(null, InstallerSilentArgumentSource.Unsupported)
            : new(detectedDefault, InstallerSilentArgumentSource.Detected);
    }

    public static bool RequiresInternet(CatalogInstaller installer, long downloadedBytes, long? expectedSizeBytes = null)
    {
        if (installer.RequiresInternet)
            return true;

        return (installer.ExpectedSizeBytes ?? expectedSizeBytes) is long expectedSize
            && expectedSize >= 20 * 1024 * 1024
            && downloadedBytes > 0
            && downloadedBytes <= expectedSize / 10;
    }

    public static string SourceName(InstallerSilentArgumentSource source) => source switch
    {
        InstallerSilentArgumentSource.Manual => "manual",
        InstallerSilentArgumentSource.Manifest => "manifest",
        InstallerSilentArgumentSource.Server => "server",
        InstallerSilentArgumentSource.Zip => "zip",
        InstallerSilentArgumentSource.Collection => "collection",
        InstallerSilentArgumentSource.TypeDefault => "type-default",
        InstallerSilentArgumentSource.Detected => "detected",
        _ => "unsupported"
    };

    public static InstallerSilentArgumentSource ParseSourceName(string? source) =>
        source?.Trim().ToLowerInvariant() switch
        {
            "manual" => InstallerSilentArgumentSource.Manual,
            "manifest" => InstallerSilentArgumentSource.Manifest,
            "server" => InstallerSilentArgumentSource.Server,
            "zip" => InstallerSilentArgumentSource.Zip,
            "collection" => InstallerSilentArgumentSource.Collection,
            "type-default" => InstallerSilentArgumentSource.TypeDefault,
            "detected" => InstallerSilentArgumentSource.Detected,
            "none" => InstallerSilentArgumentSource.Manifest,
            "unsupported" => InstallerSilentArgumentSource.Unsupported,
            _ => InstallerSilentArgumentSource.Manifest
        };

    public static bool IsExternalSource(InstallerSilentArgumentSource source) =>
        source is InstallerSilentArgumentSource.Manifest
            or InstallerSilentArgumentSource.Server
            or InstallerSilentArgumentSource.Zip
            or InstallerSilentArgumentSource.Collection;

    private static InstallerSilentArgumentResolution? ResolveProvided(string? value, InstallerSilentArgumentSource source)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string arguments = value.Trim();
        return LanInstallerValidation.IsSafeSilentArguments(arguments)
            ? new(arguments, source)
            : new(null, InstallerSilentArgumentSource.Unsupported, "Os argumentos silenciosos foram rejeitados pela validação de segurança.");
    }
}
