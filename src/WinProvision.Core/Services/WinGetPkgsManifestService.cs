using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using WinProvision.Core.Models;
using YamlDotNet.RepresentationModel;

namespace WinProvision.Core.Services;

/// <summary>Consulta manifestos de instalador publicados no repositório oficial catálogo de pacotes.</summary>
public sealed class WinGetPkgsManifestService(HttpClient? client = null)
{
    private static readonly HttpClient SharedClient = CreateClient();
    private readonly HttpClient _client = client ?? SharedClient;
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private static readonly Regex SafeId = new("^[A-Za-z0-9][A-Za-z0-9._-]{1,199}$", RegexOptions.CultureInvariant);

    public async Task<PackageManifest> GetAsync(string packageId, string requestedVersion, CancellationToken ct)
    {
        if (!SafeId.IsMatch(packageId) || !packageId.Contains('.'))
            throw new ArgumentException("ID WinGet inválido.", nameof(packageId));
        if (string.IsNullOrWhiteSpace(requestedVersion) || requestedVersion.Length > 128
            || requestedVersion is "." or ".." || requestedVersion.Any(char.IsControl)
            || requestedVersion.Contains('/') || requestedVersion.Contains('\\'))
            throw new ArgumentException("A versão do snapshot WinGet é inválida.", nameof(requestedVersion));
        string path = $"manifests/{char.ToLowerInvariant(packageId[0])}/{packageId.Replace('.', '/')}";
        string file = $"{packageId}.installer.yaml";
        string manifestUrl = $"{NetworkEndpoints.WingetPackagesRawBaseUrl}/{path}/{Uri.EscapeDataString(requestedVersion)}/{file}";
        string yaml = await GetTextAsync(manifestUrl, ct).ConfigureAwait(false);
        return ParseInstallerManifest(yaml, packageId, requestedVersion);
    }

    internal static PackageManifest ParseInstallerManifest(string yaml, string packageId, string version)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root
            || !string.Equals(Scalar(root, "PackageIdentifier"), packageId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Scalar(root, "PackageVersion"), version, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Scalar(root, "ManifestType"), "installer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(Scalar(root, "ManifestVersion"))
            || Child(root, "Installers") is not YamlSequenceNode entries)
            throw new InvalidDataException("O manifesto de instalador não corresponde ao pacote e à versão solicitados.");

        var manifest = new PackageManifest { Id = packageId, Version = version, Source = "winget" };
        foreach (var entry in entries.Children.OfType<YamlMappingNode>())
        {
            string type = (Scalar(entry, "InstallerType") ?? Scalar(root, "InstallerType") ?? "").ToLowerInvariant();
            var switches = ReadStringMap(Child(entry, "InstallerSwitches") ?? Child(root, "InstallerSwitches"));
            string? silent = switches.GetValueOrDefault("Silent");
            silent ??= type switch
            {
                "msi" or "wix" => "/qn /norestart",
                "inno" => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
                "nullsoft" => "/S",
                "burn" => "/quiet /norestart",
                _ => null
            };
            var installModes = ReadStrings(Child(entry, "InstallModes") ?? Child(root, "InstallModes"));
            bool supportsSilentMode = installModes.Count == 0
                || installModes.Any(mode => mode is "silent" or "silentWithProgress");
            string url = Scalar(entry, "InstallerUrl") ?? "";
            string? architecture = Scalar(entry, "Architecture");
            string? sha256 = Scalar(entry, "InstallerSha256");
            if (string.IsNullOrWhiteSpace(architecture)
                || !Uri.TryCreate(url, UriKind.Absolute, out var installerUri)
                || installerUri.Scheme != Uri.UriSchemeHttps
                || !IsSha256(sha256))
                throw new InvalidDataException("O instalador do manifesto não possui arquitetura, URL HTTPS ou SHA-256 válidos.");
            string extension = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() : "";
            bool extensionMatches = type is "msi" or "wix" or "wixmsi" ? extension == ".msi"
                : type is "zip" ? extension == ".zip"
                : type is "msix" or "appx" or "msixbundle" or "appxbundle" ? extension is ".msix" or ".appx" or ".msixbundle" or ".appxbundle"
                : type is "font" ? extension is ".ttf" or ".otf" or ".ttc" or ".fon"
                : extension == ".exe";
            var nestedFiles = ReadStrings(Child(entry, "NestedInstallerFiles") ?? Child(root, "NestedInstallerFiles"));
            string? nestedType = Scalar(entry, "NestedInstallerType") ?? Scalar(root, "NestedInstallerType");
            bool nestedExecutable = type == "zip" && nestedType is not null
                && nestedType is ("msi" or "wix" or "wixmsi" or "inno" or "nullsoft" or "burn" or "exe");
            bool supported = type is "msi" or "wix" or "wixmsi" or "inno" or "nullsoft" or "burn" or "exe" or "portable"
                || nestedExecutable;
            supported = supported
                && supportsSilentMode && extensionMatches
                && !string.IsNullOrWhiteSpace(silent)
                && Child(root, "Dependencies") is null && Child(entry, "Dependencies") is null
                && !silent.Any(char.IsControl) && !silent.Contains('<') && !silent.Contains('>');
            if (type == "zip" && string.Equals(nestedType, "portable", StringComparison.OrdinalIgnoreCase))
                supported = nestedFiles.Count == 1 && extensionMatches;
            manifest.Installers.Add(new CatalogInstaller
            {
                Architecture = architecture,
                Type = type,
                NestedType = nestedType,
                NestedInstallerFiles = nestedFiles,
                NestedInstallerFile = nestedFiles.Count == 1 ? nestedFiles[0] : null,
                Scope = Scalar(entry, "Scope") ?? Scalar(root, "Scope"),
                ElevationRequirement = Scalar(entry, "ElevationRequirement") ?? Scalar(root, "ElevationRequirement"),
                Locale = Scalar(entry, "InstallerLocale") ?? Scalar(root, "InstallerLocale"),
                Platforms = ReadStrings(Child(entry, "Platform") ?? Child(root, "Platform")),
                MinimumOsVersion = Scalar(entry, "MinimumOSVersion") ?? Scalar(root, "MinimumOSVersion"),
                InstallModes = installModes,
                InstallerSwitches = switches,
                UpgradeBehavior = Scalar(entry, "UpgradeBehavior") ?? Scalar(root, "UpgradeBehavior"),
                Dependencies = ReadDependencyIds(Child(entry, "Dependencies") ?? Child(root, "Dependencies")),
                UnsupportedArguments = ReadStrings(Child(entry, "UnsupportedArguments") ?? Child(root, "UnsupportedArguments")),
                DownloadCommandProhibited = ReadBoolean(entry, "DownloadCommandProhibited")
                    ?? ReadBoolean(root, "DownloadCommandProhibited") ?? false,
                AppsAndFeaturesEntries = ReadStringList(Child(entry, "AppsAndFeaturesEntries") ?? Child(root, "AppsAndFeaturesEntries")),
                Commands = ReadStrings(Child(entry, "Commands") ?? Child(root, "Commands")),
                FileExtensions = ReadStrings(Child(entry, "FileExtensions") ?? Child(root, "FileExtensions")),
                Protocols = ReadStrings(Child(entry, "Protocols") ?? Child(root, "Protocols")),
                Capabilities = ReadStrings(Child(entry, "Capabilities") ?? Child(root, "Capabilities")),
                UnsupportedOsArchitectures = ReadStrings(Child(entry, "UnsupportedOSArchitectures") ?? Child(root, "UnsupportedOSArchitectures")),
                SuccessCodes = ReadIntegers(Child(entry, "InstallerSuccessCodes") ?? Child(root, "InstallerSuccessCodes")),
                ErrorCodes = ReadIntegers(Child(entry, "InstallerErrorCodes") ?? Child(root, "InstallerErrorCodes")),
                Url = url,
                Sha256 = sha256,
                SilentArgs = supported ? silent : null,
                SilentSource = supported ? "winget-pkgs" : "none",
                SilentSupported = supported
            });
        }
        if (manifest.Installers.Count == 0)
            throw new InvalidDataException("O manifesto não possui instaladores.");
        return manifest;
    }

    private static YamlNode? Child(YamlNode? node, string name)
    {
        if (node is not YamlMappingNode mapping) return null;
        foreach (var item in mapping.Children)
            if (item.Key is YamlScalarNode key && key.Value == name) return item.Value;
        return null;
    }

    private static string? Scalar(YamlNode? node, string name) =>
        Child(node, name) is YamlScalarNode value ? value.Value : null;

    private static List<string> ReadStrings(YamlNode? node) =>
        node is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlScalarNode>()
                .Select(item => item.Value?.Trim())
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList()
            : node is YamlScalarNode scalar && !string.IsNullOrWhiteSpace(scalar.Value)
                ? [scalar.Value.Trim()]
                : [];

    private static List<CatalogAppsAndFeaturesEntry> ReadStringList(YamlNode? node) =>
        node is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlMappingNode>()
                .Select(item => new CatalogAppsAndFeaturesEntry
                {
                    DisplayName = Scalar(item, "DisplayName"),
                    Publisher = Scalar(item, "Publisher"),
                    DisplayVersion = Scalar(item, "DisplayVersion"),
                    ProductCode = Scalar(item, "ProductCode"),
                    UpgradeCode = Scalar(item, "UpgradeCode"),
                    InstallerType = Scalar(item, "InstallerType"),
                    Language = Scalar(item, "Language")
                })
                .ToList()
            : [];

    private static List<string> ReadDependencyIds(YamlNode? node) =>
        node is YamlMappingNode mapping && Child(mapping, "PackageDependencies") is YamlSequenceNode dependencies
            ? dependencies.Children.OfType<YamlMappingNode>()
                .Select(item => Scalar(item, "PackageIdentifier"))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!)
                .ToList()
            : [];

    private static Dictionary<string, string> ReadStringMap(YamlNode? node) =>
        node is YamlMappingNode mapping
            ? mapping.Children
                .OfType<KeyValuePair<YamlNode, YamlNode>>()
                .Where(item => item.Key is YamlScalarNode && item.Value is YamlScalarNode)
                .ToDictionary(
                    item => ((YamlScalarNode)item.Key).Value ?? string.Empty,
                    item => ((YamlScalarNode)item.Value).Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

    private static bool? ReadBoolean(YamlNode? node, string name)
    {
        string? value = Scalar(node, name);
        return bool.TryParse(value, out bool result) ? result : null;
    }

    private static List<int> ReadIntegers(YamlNode? node) =>
        node is YamlSequenceNode sequence
            ? sequence.Children.OfType<YamlScalarNode>()
                .Select(item => int.TryParse(item.Value, out int value) ? (int?)value : null)
                .Where(item => item.HasValue)
                .Select(item => item!.Value)
                .ToList()
            : [];

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxMetadataBytes)
            throw new InvalidDataException("O manifesto excede o limite de tamanho.");
        await using var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        byte[] bytes = new byte[64 * 1024];
        int read;
        while ((read = await input.ReadAsync(bytes, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxMetadataBytes)
                throw new InvalidDataException("O manifesto excede o limite de tamanho.");
            buffer.Write(bytes, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinProvision", "1.0"));
        return client;
    }

}
