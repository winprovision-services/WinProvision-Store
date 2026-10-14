using System.Linq;
using System.Text.Json;
using WinProvision.Core.Models;

namespace WinProvision.Core.Services;

/// <summary>
/// Interpreta perfis individuais e backups com múltiplas coleções, preservando os contratos JSON legados.
/// </summary>
public static class ProfileManifestParser
{
    /// <summary>
    /// Faz o parse. Retorna null se o JSON não puder ser interpretado como nenhum dos dois
    /// formatos (chamador decide como reportar isso).
    /// </summary>
    /// <param name="json">Conteúdo já lido (arquivo local ou baixado de uma URL).</param>
    /// <param name="fallbackName">
    /// Nome a usar quando o resultado vier de um <see cref="ProfileBackupSet"/> com mais de uma
    /// guia (não há um único nome de perfil nesse caso) — normalmente o nome do arquivo/URL de
    /// origem. Com exatamente uma guia, o nome dela prevalece.
    /// </param>
    public static ProfileManifest? Parse(string json, string? fallbackName = null, bool preserveDuplicateApps = false)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        bool looksLikeBackupSet = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("tabs", out var tabsElement)
            && tabsElement.ValueKind == JsonValueKind.Array;

        if (!looksLikeBackupSet)
        {
            return WinProvision.Core.Services.AotJson.Deserialize<ProfileManifest>(json, WinProvisionJsonOptions.Profile);
        }

        var backupSet = WinProvision.Core.Services.AotJson.Deserialize<ProfileBackupSet>(json, WinProvisionJsonOptions.Profile);
        if (backupSet is null)
        {
            return null;
        }

        var flattenedApps = backupSet.Tabs.SelectMany(tab => tab.Apps).ToList();
        var mergedApps = preserveDuplicateApps
            ? flattenedApps
            : flattenedApps.GroupBy(app => app.Id, System.StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First()).ToList();

        return new ProfileManifest
        {
            SchemaVersion = backupSet.SchemaVersion,
            CreatedAt = backupSet.CreatedAt,
            Name = backupSet.Tabs.Count == 1 ? backupSet.Tabs[0].Name : fallbackName,
            Apps = mergedApps,
            Provisioning = backupSet.Provisioning,
        };
    }
}
