using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinProvision.Core.Services;

public sealed record AppLogSettings(AppLogLevel MinimumLevel, int RetentionDays, int MaximumTotalMegabytes)
{
    public static AppLogSettings Default { get; } = new(AppLogLevel.Info, 30, 100);

    public AppLogOptions ToOptions() => new(
        MinimumLevel,
        Math.Clamp(RetentionDays, 1, 3650),
        Math.Clamp(MaximumTotalMegabytes, 1, 10_240));
}

public sealed class AppLogSettingsStore
{
    private readonly object _gate = new();
    private readonly string _path;
    private AppLogSettings _settings;

    public AppLogSettingsStore(string? path = null)
    {
        _path = Path.GetFullPath(path ?? GetSettingsPath());
        _settings = Load(_path);
    }

    public AppLogSettings Current
    {
        get { lock (_gate) return _settings; }
    }

    public AppLogOptions CurrentOptions => Current.ToOptions();

    public void Save(AppLogSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.MinimumLevel))
            throw new ArgumentOutOfRangeException(nameof(settings), "O nível de log não é válido.");

        AppLogSettings normalized = settings with
        {
            RetentionDays = Math.Clamp(settings.RetentionDays, 1, 3650),
            MaximumTotalMegabytes = Math.Clamp(settings.MaximumTotalMegabytes, 1, 10_240)
        };

        lock (_gate)
        {
            string directory = Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException("O caminho das configurações de log não possui pasta.");
            Directory.CreateDirectory(directory);
            string temporaryPath = _path + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath,
                    JsonSerializer.Serialize(normalized, AppLogSettingsJsonContext.Default.AppLogSettings));
                File.Move(temporaryPath, _path, overwrite: true);
                _settings = normalized;
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static AppLogSettings Load(string path)
    {
        try
        {
            AppLogSettings? settings = JsonSerializer.Deserialize(
                File.ReadAllText(path), AppLogSettingsJsonContext.Default.AppLogSettings);
            return settings is not null && Enum.IsDefined(settings.MinimumLevel)
                ? settings with
                {
                    RetentionDays = Math.Clamp(settings.RetentionDays, 1, 3650),
                    MaximumTotalMegabytes = Math.Clamp(settings.MaximumTotalMegabytes, 1, 10_240)
                }
                : AppLogSettings.Default;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppLogSettings.Default;
        }
    }

    private static string GetSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinProvisionStore",
        "log-settings.json");
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(AppLogSettings))]
internal partial class AppLogSettingsJsonContext : JsonSerializerContext;
