using System;

namespace WinProvision.Core.Models;

#if WINDOWS
[WinRT.GeneratedBindableCustomProperty]
#endif
public sealed partial record OperationHistoryEntry(
    Guid OperationId,
    string AppName,
    OperationKind Kind,
    OperationState State,
    DateTimeOffset FinishedAt,
    WingetMethod Method,
    DateTimeOffset? StartedAt = null,
    string? Details = null)
{
    public string KindLabel => Kind switch
    {
        OperationKind.Install => "Instalação",
        OperationKind.Update => "Atualização",
        OperationKind.Uninstall => "Desinstalação",
        _ => "Operação"
    };

    public string StateLabel => State switch
    {
        OperationState.Completed => "Concluída",
        OperationState.Failed => "Falhou",
        OperationState.Canceled => "Cancelada",
        _ => "Finalizada"
    };

    public string FinishedAtLocal => FinishedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm");

    public string StartedAtLocal => StartedAt?.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss") ?? "Não registrado";
    public string EndedAtLocal => FinishedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string DurationText => StartedAt is { } started
        ? $"{Math.Max(0, (int)(FinishedAt - started).TotalSeconds)} s"
        : "—";
    public string StartLine => $"Início: {StartedAtLocal}";
    public string EndLine => $"Fim: {EndedAtLocal}";
    public string DurationLine => $"Duração: {DurationText}";
    public string MethodLine => $"Método: {MethodLabel}";
    public string LogDetails => string.IsNullOrWhiteSpace(Details) ? "Nenhuma saída detalhada registrada." : Details;

    public string MethodLabel => Method switch
    {
        WingetMethod.ComApi => "API do WinGet",
        WingetMethod.OwnApi => "API da Store",
        WingetMethod.WinGetPkgsViper => "WinGet-Pkgs + Viper",
        WingetMethod.WingetExe => "WinGet CLI",
        _ => "Método não identificado"
    };
}
