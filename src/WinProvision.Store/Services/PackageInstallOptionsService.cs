using WinProvision.Core.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Store.Services;

/// <summary>Persiste opções reutilizáveis por ID e origem do pacote.</summary>
public sealed partial class PackageInstallOptionsService
{
    private const string GlobalDefaultsKey = "winget|@global-defaults";
    private const string ChocolateyDefaultsKey = "chocolatey|@global-defaults";
    private readonly string _filePath;
    private readonly System.Threading.Lock _sync = new();
    private Dictionary<string, PackageInstallOptions> _options = new(StringComparer.OrdinalIgnoreCase);

    public PackageInstallOptionsService(string? storagePath = null)
    {
        _filePath = storagePath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinProvisionStore", "package-install-options.json");
        try
        {
            if (!File.Exists(_filePath)) return;
            var saved = WinProvision.Core.Services.AotJson.Deserialize<Dictionary<string, PackageInstallOptions>>(File.ReadAllText(_filePath));
            if (saved is not null)
                _options = new Dictionary<string, PackageInstallOptions>(saved, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception recoveryException)
        {
            WinProvision.Core.Services.CoreLog.Warning(recoveryException, "PackageInstallOptionsService.PackageInstallOptionsService");

            // Um arquivo de preferências inválido não deve impedir a inicialização da Store.
            _options = new Dictionary<string, PackageInstallOptions>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public PackageInstallOptions? Get(string id, string? source)
    {
        if (IsChocolatey(source))
        {
            lock (_sync)
            {
                PackageInstallOptions? package = _options.GetValueOrDefault(GetKey(id, source));
                PackageInstallOptions? effective = package?.OverridesGlobalOptions == true
                    ? package
                    : _options.GetValueOrDefault(ChocolateyDefaultsKey);
                return effective is null ? new PackageInstallOptions(RequiresElevation: true) : ClearTransientOptions(effective, preserveIgnoreSecurityHash: true);
            }
        }
        if (!IsWinGet(source)) return null;
        lock (_sync)
        {
            PackageInstallOptions? package = _options.GetValueOrDefault(GetKey(id, source));
            PackageInstallOptions? effective = package?.OverridesGlobalOptions == true
                ? package
                : _options.GetValueOrDefault(GlobalDefaultsKey);
            return effective is null ? null : ClearTransientOptions(effective);
        }
    }

    public PackageInstallOptions? GetPackageOverride(string id, string? source)
    {
        if (!IsWinGet(source) && !IsChocolatey(source)) return null;
        lock (_sync)
            return _options.GetValueOrDefault(GetKey(id, source)) is { OverridesGlobalOptions: true } options
                ? ClearTransientOptions(options, preserveIgnoreSecurityHash: IsChocolatey(source))
                : null;
    }

    public PackageInstallOptions GetGlobalDefaults()
        => GetGlobalDefaults("winget");

    public PackageInstallOptions GetGlobalDefaults(string? source)
    {
        lock (_sync)
        {
            string key = IsChocolatey(source) ? ChocolateyDefaultsKey : GlobalDefaultsKey;
            PackageInstallOptions fallback = IsChocolatey(source) ? new PackageInstallOptions(RequiresElevation: true) : new PackageInstallOptions();
            return ClearTransientOptions(_options.GetValueOrDefault(key) ?? fallback, preserveIgnoreSecurityHash: IsChocolatey(source));
        }
    }

    public void SaveGlobalDefaults(PackageInstallOptions options)
        => SaveGlobalDefaults(options, "winget");

    public void SaveGlobalDefaults(PackageInstallOptions options, string? source)
    {
        ArgumentNullException.ThrowIfNull(options);
        bool chocolatey = IsChocolatey(source);
        PackageInstallOptions normalized = Normalize(options, chocolatey) with { OverridesGlobalOptions = false };
        lock (_sync)
        {
            _options[chocolatey ? ChocolateyDefaultsKey : GlobalDefaultsKey] = normalized;
            Persist();
        }
    }

    public void Save(string id, string? source, PackageInstallOptions options)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("O ID do pacote é obrigatório.", nameof(id));
        if (!IsWinGet(source) && !IsChocolatey(source))
            throw new InvalidOperationException("Este gerenciador não oferece opções reutilizáveis por pacote.");

        var chocolatey = IsChocolatey(source);
        var normalized = Normalize(options, chocolatey) with { OverridesGlobalOptions = true };

        lock (_sync)
        {
            _options[GetKey(id, source)] = normalized;
            Persist();
        }
    }

    public void Remove(string id, string? source)
    {
        if (!IsWinGet(source) && !IsChocolatey(source)) return;
        lock (_sync)
        {
            if (_options.Remove(GetKey(id, source)))
                Persist();
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        AtomicFile.WriteAllText(_filePath, WinProvision.Core.Services.AotJson.Serialize(_options, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static PackageInstallOptions ClearTransientOptions(PackageInstallOptions options, bool preserveIgnoreSecurityHash = false) => options with
    {
        IgnoreSecurityHash = preserveIgnoreSecurityHash && options.IgnoreSecurityHash,
        RunInstallHooks = false,
        RunUpdateHooks = false,
        RunUninstallHooks = false
    };

    private static PackageInstallOptions Normalize(PackageInstallOptions options, bool chocolatey = false) => options with
    {
        IgnoreSecurityHash = chocolatey && options.IgnoreSecurityHash,
        RunInstallHooks = false,
        RunUpdateHooks = false,
        RunUninstallHooks = false,
        Scope = NormalizeScope(options.Scope),
        Architecture = NormalizeArchitecture(options.Architecture),
        InstallLocation = string.IsNullOrWhiteSpace(options.InstallLocation) ? null : options.InstallLocation.Trim(),
        CustomArguments = NormalizeArguments(options.CustomArguments),
        OverrideArguments = NormalizeArguments(options.OverrideArguments),
        UpdateArguments = NormalizeArguments(options.UpdateArguments),
        UninstallArguments = NormalizeArguments(options.UninstallArguments),
        PreInstallScript = NormalizeScript(options.PreInstallScript),
        PostInstallScript = NormalizeScript(options.PostInstallScript),
        PreUpdateScript = NormalizeScript(options.PreUpdateScript),
        PostUpdateScript = NormalizeScript(options.PostUpdateScript),
        PreUninstallScript = NormalizeScript(options.PreUninstallScript),
        PostUninstallScript = NormalizeScript(options.PostUninstallScript),
        ProcessesToClose = NormalizeProcessNames(options.ProcessesToClose)
    };

    private static string GetKey(string id, string? source) => $"{(IsChocolatey(source) ? "chocolatey" : "winget")}|{id.Trim()}";

    private static bool IsWinGet(string? source) =>
        string.Equals(source?.Trim(), "winget", StringComparison.OrdinalIgnoreCase)
        || source?.Trim().StartsWith("WinGet:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsChocolatey(string? source) =>
        string.Equals(source?.Trim(), "chocolatey", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeScope(string? scope) => scope?.Trim().ToLowerInvariant() switch
    {
        "user" => "user",
        "machine" => "machine",
        _ => null
    };

    private static string? NormalizeArchitecture(string? architecture) => architecture?.Trim().ToLowerInvariant() switch
    {
        "x86" => "x86",
        "x64" => "x64",
        "arm" => "arm",
        "arm64" => "arm64",
        _ => null
    };

    private static string? NormalizeScript(string? script)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        if (Encoding.UTF8.GetByteCount(script) > 32_768)
            throw new ArgumentException("Cada script de instalação deve ter no máximo 32 KiB.", nameof(script));
        return script.Trim();
    }

    private static string? NormalizeArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        if (Encoding.UTF8.GetByteCount(arguments) > 4_096 || arguments.Any(char.IsControl))
            throw new ArgumentException("Os argumentos adicionais são inválidos ou excedem 4 KiB.", nameof(arguments));
        return arguments.Trim();
    }

    private static string[] NormalizeProcessNames(IEnumerable<string>? names)
    {
        if (names is null) return [];
        var normalized = new List<string>();
        foreach (string name in names.Select(value => Path.GetFileNameWithoutExtension(value) ?? string.Empty)
                     .Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            string value = name.Trim();
            if (value.Length > 128 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
                throw new ArgumentException("Os nomes de processo aceitam apenas letras, números, ponto, hífen e sublinhado.", nameof(names));
            if (!normalized.Contains(value, StringComparer.OrdinalIgnoreCase)) normalized.Add(value);
            if (normalized.Count > 32) throw new ArgumentException("É possível configurar até 32 processos por operação.", nameof(names));
        }
        return [.. normalized];
    }
}
