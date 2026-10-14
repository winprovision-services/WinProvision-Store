namespace WinProvision.Core.Services;

/// <summary>Códigos Win32/MSI e HRESULTs publicados pelo WinGet; outros CLIs mantêm seu contrato próprio.</summary>
public static class InstallerExitCodes
{
    public static bool IsSuccess(int code) => code is 0 or 3010 or 1641;
    public static bool IsWingetSuccess(int code) => IsSuccess(code)
        || unchecked((uint)code) is 0x8A150109 or 0x8A15010B;
    public static bool RequiresReboot(int code) => code is 3010 or 1641
        || unchecked((uint)code) is 0x8A150109 or 0x8A15010A or 0x8A15010B;
    public static bool RebootInitiated(int code) => code == 1641 || unchecked((uint)code) == 0x8A15010B;

    public static WingetFailureReason Classify(int code) => unchecked((uint)code) switch
    {
        5 or 740 or 0x80070005 or 0x800702E4 => WingetFailureReason.ElevationRequired,
        1223 or 1602 or 0x800704C7 or 0x80070642 or 0x8A150005 or 0x8A15010C => WingetFailureReason.OperationCanceled,
        1618 or 0x80070652 or 0x8A150102 => WingetFailureReason.InstallationInProgress,
        32 or 33 or 0x8A150101 or 0x8A150103 or 0x8A150111 => WingetFailureReason.FilesInUse,
        112 or 0x80070070 or 0x8A150105 => WingetFailureReason.DiskFull,
        1625 or 0x80070659 or 0x8A15001B or 0x8A15001C or 0x8A15003A or 0x8A15010F => WingetFailureReason.BlockedByPolicy,
        0x8A15010A => WingetFailureReason.RebootRequiredBeforeInstall,
        1603 or 1619 or 1620 or 1638 or 0x80070643 or 0x8A150108 or 0x8A150110 or 0x8A150049 => WingetFailureReason.InstallError,
        _ => WingetFailureReason.Unknown,
    };

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
