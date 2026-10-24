using WinProvision.Core.Models.Provisioning;

namespace WinProvision.Core.Models;

/// <summary>
/// Backup de coleções e configurações opcionais de provisionamento do Windows.
/// </summary>
public class ProfileBackupSet
{
    public int SchemaVersion { get; set; } = 1;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ProfileManifest> Tabs { get; set; } = new();

    /// <summary>
    /// Configurações de provisionamento do Windows — null quando não exportadas. Fica no
    /// nível do ProfileBackupSet (não dentro de cada guia) porque é um ajuste da MÁQUINA,
    /// não de uma seleção de pacotes específica; ver
    /// <see cref="Services.Provisioning.ProvisioningService.Current"/>, a origem deste
    /// valor (aplicado de fato OU só exportado/importado pela tela Provisionamento). Um
    /// único arquivo (local ou Gist) já cobre apps + provisionamento — não existe um
    /// segundo arquivo/formato separado para isso.
    /// </summary>
    public ProvisioningManifest? Provisioning { get; set; }
}
