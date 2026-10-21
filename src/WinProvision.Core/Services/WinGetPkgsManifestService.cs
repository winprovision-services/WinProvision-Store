using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using WinProvision.Core.Models;
using YamlDotNet.RepresentationModel;

namespace WinProvision.Core.Services;

/// <summary>Consulta manifestos de instalador publicados no repositório oficial winget-pkgs.</summary>
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
        string manifestUrl = $"https://raw.githubusercontent.com/microsoft/winget-pkgs/master/{path}/{Uri.EscapeDataString(requestedVersion)}/{file}";
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
            || Child(root, "Installers") is not YamlSequenceNode entries)
            throw new InvalidDataException("O manifesto de instalador não corresponde ao pacote e à versão solicitados.");

        var manifest = new PackageManifest { Id = packageId, Version = version, Source = "winget" };
        foreach (var entry in entries.Children.OfType<YamlMappingNode>())
        {
            string type = (Scalar(entry, "InstallerType") ?? Scalar(root, "InstallerType") ?? "").ToLowerInvariant();
            string? silent = Scalar(Child(entry, "InstallerSwitches"), "Silent")
                ?? Scalar(Child(root, "InstallerSwitches"), "Silent");
            silent ??= type switch
            {
                "msi" or "wix" => "/qn /norestart",
                "inno" => "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-",
                "nullsoft" => "/S",
                "burn" => "/quiet /norestart",
                _ => null
            };
            bool supportsSilentMode = (Child(entry, "InstallModes") ?? Child(root, "InstallModes")) is not YamlSequenceNode modes
                || modes.Children.OfType<YamlScalarNode>().Any(mode => mode.Value is "silent" or "silentWithProgress");
            string url = Scalar(entry, "InstallerUrl") ?? "";
            string extension = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                ? Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() : "";
            bool extensionMatches = type is "msi" or "wix" ? extension == ".msi" : extension == ".exe";
            bool supported = type is "msi" or "wix" or "inno" or "nullsoft" or "burn" or "exe"
                && supportsSilentMode && extensionMatches
                && !string.IsNullOrWhiteSpace(silent)
                && Child(root, "Dependencies") is null && Child(entry, "Dependencies") is null
                && !silent.Any(char.IsControl) && !silent.Contains('<') && !silent.Contains('>');
            manifest.Installers.Add(new CatalogInstaller
            {
                Architecture = Scalar(entry, "Architecture"),
                Type = type,
                Scope = Scalar(entry, "Scope") ?? Scalar(root, "Scope"),
                ElevationRequirement = Scalar(entry, "ElevationRequirement") ?? Scalar(root, "ElevationRequirement"),
                Url = url,
                Sha256 = Scalar(entry, "InstallerSha256"),
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
