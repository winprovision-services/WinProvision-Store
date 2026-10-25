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
        if (!IsWinGet(source)) return null;
        lock (_sync)
            return _options.GetValueOrDefault(GetKey(id)) is { } options
                ? options with { IgnoreSecurityHash = false, RunInstallHooks = false }
                : null;
    }

    public void Save(string id, string? source, PackageInstallOptions options)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("O ID do pacote é obrigatório.", nameof(id));
        if (!IsWinGet(source))
            throw new InvalidOperationException("Opções reutilizáveis estão disponíveis apenas para pacotes WinGet.");

        var normalized = options with
        {
            IgnoreSecurityHash = false,
            RunInstallHooks = false,
            Scope = NormalizeScope(options.Scope),
            Architecture = NormalizeArchitecture(options.Architecture),
            InstallLocation = string.IsNullOrWhiteSpace(options.InstallLocation) ? null : options.InstallLocation.Trim(),
            CustomArguments = NormalizeArguments(options.CustomArguments),
            PreInstallScript = NormalizeScript(options.PreInstallScript),
            PostInstallScript = NormalizeScript(options.PostInstallScript)
        };

        lock (_sync)
        {
            _options[GetKey(id)] = normalized;
            Persist();
        }
    }

    public void Remove(string id, string? source)
    {
        if (!IsWinGet(source)) return;
        lock (_sync)
        {
            if (_options.Remove(GetKey(id)))
                Persist();
        }
    }

    private void Persist()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        AtomicFile.WriteAllText(_filePath, WinProvision.Core.Services.AotJson.Serialize(_options, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string GetKey(string id) => $"winget|{id.Trim()}";

    private static bool IsWinGet(string? source) =>
        string.Equals(source?.Trim(), "winget", StringComparison.OrdinalIgnoreCase)
        || source?.Trim().StartsWith("WinGet:", StringComparison.OrdinalIgnoreCase) == true;

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
}
