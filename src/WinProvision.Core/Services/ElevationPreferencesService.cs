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
    public bool AllowElevation { get; private set; } = true;

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
                AllowElevation = stored?.AllowElevation ?? false;
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
        AllowElevation = false;
        ReadFailed = true;
        CoreLog.Warning(ex, "ElevationPreferences.Read");
    }

    public void SetAllowed(bool allowed)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new ElevationPreferences(allowed), ElevationPreferencesJsonContext.Default.ElevationPreferences));
        AllowElevation = allowed;
        ReadFailed = false;
    }

    public void EnsureAllowed()
    {
        if (ReadFailed)
            throw new Win32Exception(1625, "Não foi possível ler a política do WinProvision Elevator (arquivo corrompido, em uso ou sem permissão); a elevação foi bloqueada por segurança. Salve a opção novamente em Configurações → WinProvision Elevator.");
        if (!AllowElevation) throw new Win32Exception(1625, "A elevação pelo WinProvision Elevator está bloqueada nas Configurações.");
    }
}

internal sealed record ElevationPreferences(bool AllowElevation = false);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ElevationPreferences))]
internal partial class ElevationPreferencesJsonContext : JsonSerializerContext;
