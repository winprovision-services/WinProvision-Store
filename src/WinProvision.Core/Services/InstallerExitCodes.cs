using System.Collections.Frozen;

namespace WinProvision.Core.Services;

/// <summary>Códigos Win32/MSI e HRESULTs publicados pelo gerenciador de pacotes; outros CLIs mantêm seu contrato próprio.</summary>
public static class InstallerExitCodes
{
    // Exit codes Win32/MSI e HRESULTs gerenciador de pacotes. Os códigos gerenciador de pacotes abaixo são
    // confirmados pelo AppInstallerErrors.h mantido no projeto e/ou pelo
    // tratamento do gerenciador de pacotes no outras interfaces local.
    public static bool IsSuccess(int code) => code is 0 or 3010 or 1641;
    public static bool IsWingetSuccess(int code) => IsSuccess(code)
        || unchecked((uint)code) is 0x8A150109 or 0x8A15010B;
    public static bool RequiresReboot(int code) => code is 3010 or 1641
        || unchecked((uint)code) is 0x8A150109 or 0x8A15010A or 0x8A15010B;
    public static bool RebootInitiated(int code) => code == 1641 || unchecked((uint)code) == 0x8A15010B;

    private static readonly FrozenDictionary<uint, WingetFailureReason> FailureReasons =
        new Dictionary<uint, WingetFailureReason>
        {
            // Win32 / MSI
            [5] = WingetFailureReason.ElevationRequired,
            [740] = WingetFailureReason.ElevationRequired,
            [0x80070005] = WingetFailureReason.ElevationRequired,
            [0x800702E4] = WingetFailureReason.ElevationRequired,
            [1223] = WingetFailureReason.OperationCanceled,
            [1602] = WingetFailureReason.OperationCanceled,
            [0x800704C7] = WingetFailureReason.OperationCanceled,
            [0x80070642] = WingetFailureReason.OperationCanceled,
            [1618] = WingetFailureReason.InstallationInProgress,
            [0x80070652] = WingetFailureReason.InstallationInProgress,
            [0x8A150102] = WingetFailureReason.InstallationInProgress,
            [32] = WingetFailureReason.FilesInUse,
            [33] = WingetFailureReason.FilesInUse,
            [0x8A150101] = WingetFailureReason.FilesInUse,
            [0x8A150103] = WingetFailureReason.FilesInUse,
            [0x8A150111] = WingetFailureReason.FilesInUse,
            [112] = WingetFailureReason.DiskFull,
            [0x80070070] = WingetFailureReason.DiskFull,
            [1625] = WingetFailureReason.BlockedByPolicy,
            [0x80070659] = WingetFailureReason.BlockedByPolicy,
            [1603] = WingetFailureReason.InstallError,
            [1619] = WingetFailureReason.InstallError,
            [1620] = WingetFailureReason.InstallError,
            [1638] = WingetFailureReason.InstallError,
            [0x80070643] = WingetFailureReason.InstallError,

            // gerenciador de pacotes CLI: HRESULTs conhecidos com interpretação explícita
            // no código local do gerenciador de pacotes ou do outras interfaces.
            [0x8A150001] = WingetFailureReason.InternalError,
            // COM InstallResult uses this HRESULT for InvalidOptions (pre-execution).
            [0x8A150002] = WingetFailureReason.InternalError,
            [0x8A150005] = WingetFailureReason.OperationCanceled,
            [0x8A150008] = WingetFailureReason.DownloadError,
            [0x8A150010] = WingetFailureReason.NoApplicableInstallers,
            [0x8A150011] = WingetFailureReason.InstallerHashMismatch,
            // 0x8A150014 fica fora do mapa: também indica ausência de pacote instalado.
            [0x8A15000F] = WingetFailureReason.CatalogError,
            [0x8A150019] = WingetFailureReason.ElevationRequired,
            [0x8A15001B] = WingetFailureReason.BlockedByPolicy,
            [0x8A15001C] = WingetFailureReason.BlockedByPolicy,
            [0x8A15002B] = WingetFailureReason.NoApplicableUpgrade,
            [0x8A150030] = WingetFailureReason.UninstallCommandFailed,
            [0x8A15003A] = WingetFailureReason.BlockedByPolicy,
            [0x8A150041] = WingetFailureReason.PackageAgreementsNotAccepted,
            [0x8A150045] = WingetFailureReason.CatalogError,
            [0x8A150049] = WingetFailureReason.InstallError,
            [0x8A150056] = WingetFailureReason.ElevationProhibited,
            [0x8A150077] = WingetFailureReason.OperationCanceled,
            [0x8A150105] = WingetFailureReason.DiskFull,
            [0x8A150108] = WingetFailureReason.InstallError,
            [0x8A15010A] = WingetFailureReason.RebootRequiredBeforeInstall,
            [0x8A15010C] = WingetFailureReason.OperationCanceled,
            [0x8A15010F] = WingetFailureReason.BlockedByPolicy,
            [0x8A150110] = WingetFailureReason.InstallError,
            [0x80073D28] = WingetFailureReason.ElevationRequired,
        }.ToFrozenDictionary();

    public static WingetFailureReason Classify(int code) =>
        FailureReasons.TryGetValue(unchecked((uint)code), out WingetFailureReason reason)
            ? reason
            : WingetFailureReason.Unknown;

    public static string Describe(int code) => code switch
    {
        0 => "Operação concluída.",
        3010 => "Operação concluída; reinicie o Windows para finalizar.",
        1641 => "Operação concluída; o instalador iniciou a reinicialização.",
        _ when unchecked((uint)code) == 0x8A150109 => "Reinicie o Windows para concluir a instalação.",
        _ when unchecked((uint)code) == 0x8A15010B => "O instalador iniciou a reinicialização.",
        _ => WingetErrorTranslator.ToMessage(Classify(code), "executar", "instalador"),
    };
}
