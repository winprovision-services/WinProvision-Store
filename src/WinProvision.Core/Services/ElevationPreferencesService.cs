using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public sealed class ElevationPreferencesService
{
    // O arquivo é publicado por renomeação (AtomicFile), mas antivírus, indexadores e outra instância
    // (interface x CLI) podem segurá-lo por instantes. Leituras transitórias são repetidas antes de
    // falhar fechado, para não bloquear a elevação por um conflito de acesso passageiro.
    private const int ReadAttempts = 4;
    private readonly string _path;
    private Dictionary<string, bool> _settings = new(StringComparer.OrdinalIgnoreCase);
    private bool _legacyAllowElevation = true;
    public bool AllowElevation => GetBoolean("allowElevation", defaultValue: true);
    public string FilePath => _path;

    /// <summary>
    /// True quando o arquivo existe mas não pôde ser lido ou validado (corrompido, em uso ou sem permissão).
    /// A política falha fechada (<see cref="AllowElevation"/> = false), mas a mensagem distingue isso de um bloqueio escolhido.
    /// </summary>
    public bool ReadFailed { get; private set; }

    public ElevationPreferencesService(string? path = null)
    {
        _path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinProvisionStoreWinUI3", "elevation-preferences.json");
        for (int attempt = 1; attempt <= ReadAttempts; attempt++)
        {
            try
            {
                var stored = JsonSerializer.Deserialize(File.ReadAllText(_path), ElevationPreferencesJsonContext.Default.ElevationPreferences);
                _legacyAllowElevation = stored?.AllowElevation ?? true;
                _settings = stored?.Settings is null
                    ? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, bool>(stored.Settings, StringComparer.OrdinalIgnoreCase);
                ReadFailed = stored is null;
                return;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt < ReadAttempts) { Thread.Sleep(40 * attempt); continue; }
                FailClosed(ex);
                return;
            }
            catch (JsonException ex)
            {
                FailClosed(ex);
                return;
            }
        }
    }

    private void FailClosed(Exception ex)
    {
        _legacyAllowElevation = false;
        _settings.Clear();
        ReadFailed = true;
        CoreLog.Warning(ex, "ElevationPreferences.Read");
    }

    public void SetAllowed(bool allowed)
        => SetBoolean("allowElevation", allowed);

    public bool GetBoolean(string key, bool defaultValue)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (_settings.TryGetValue(key, out bool value))
            return value;
        return key.Equals("allowElevation", StringComparison.OrdinalIgnoreCase)
            ? _legacyAllowElevation
            : defaultValue;
    }

    public bool HasValue(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _settings.ContainsKey(key);
    }

    public void SetBoolean(string key, bool value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var settings = new Dictionary<string, bool>(_settings, StringComparer.OrdinalIgnoreCase)
        {
            [key] = value
        };
        bool legacyAllowElevation = key.Equals("allowElevation", StringComparison.OrdinalIgnoreCase)
            ? value
            : _legacyAllowElevation;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        string json = JsonSerializer.Serialize(new ElevationPreferences(legacyAllowElevation, settings),
            ElevationPreferencesJsonContext.Default.ElevationPreferences);
        AtomicFile.WriteAllText(_path, json);
        _settings = settings;
        _legacyAllowElevation = legacyAllowElevation;
        ReadFailed = false;
    }

    public void EnsureAllowed()
    {
        if (ReadFailed)
            throw new Win32Exception(1625, "Não foi possível ler a política do WinProvision Elevator (arquivo corrompido, em uso ou sem permissão); a elevação foi bloqueada por segurança. Salve a opção novamente em Configurações → WinProvision Elevator.");
        if (!AllowElevation) throw new Win32Exception(1625, "A elevação pelo WinProvision Elevator está bloqueada nas Configurações.");
    }
}

internal sealed record ElevationPreferences(
    bool AllowElevation = true,
    Dictionary<string, bool>? Settings = null);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ElevationPreferences))]
internal partial class ElevationPreferencesJsonContext : JsonSerializerContext;
