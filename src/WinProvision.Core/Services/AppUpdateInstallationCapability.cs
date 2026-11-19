using System.Text.Json;

namespace WinProvision.Core.Services;

public static class AppUpdateInstallationCapability
{
    private const string CapabilityFileName = "update-capability.json";

    public static bool IsSupported(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
            return false;

        try
        {
            string directory = Path.GetDirectoryName(Path.GetFullPath(executablePath))
                ?? throw new ArgumentException("O executável não tem uma pasta válida.", nameof(executablePath));
            string capabilityPath = Path.Combine(directory, CapabilityFileName);
            if (!File.Exists(capabilityPath))
                return false;

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(capabilityPath));
            JsonElement root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("schemaVersion", out JsonElement schema)
                && schema.TryGetInt32(out int schemaVersion)
                && schemaVersion == 1
                && root.TryGetProperty("supportsSelfUpdate", out JsonElement supported)
                && supported.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException or ArgumentException or NotSupportedException)
        {
            CoreLog.Warning(exception, "AppUpdateInstallationCapability.Read");
            return false;
        }
    }
}
