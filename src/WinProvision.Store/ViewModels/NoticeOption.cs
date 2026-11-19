using CommunityToolkit.Mvvm.Input;
using WinProvision.Core.Services;

namespace WinProvision.Store.ViewModels;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class NoticeOption
{
    private readonly Action<NoticeOption> _dismiss;

    public NoticeDefinition Definition { get; }
    public string Title { get; }
    public string Message { get; }
    public string? Secondary { get; }
    public string IconGlyph => Definition.Severity.ToLowerInvariant() switch
    {
        "info" => "\uE946",
        "critical" => "\uEA39",
        _ => "\uE7BA"
    };
    public bool IsDismissible => Definition.Dismissible
        && !Definition.Severity.Equals("critical", StringComparison.OrdinalIgnoreCase)
        && !Definition.DismissPersistence.Equals("none", StringComparison.OrdinalIgnoreCase);
    public bool HasSecondary => !string.IsNullOrWhiteSpace(Secondary);
    public bool HasAction => !string.IsNullOrWhiteSpace(Definition.ActionKey);
    public RelayCommand DismissCommand { get; }

    public NoticeOption(
        NoticeDefinition definition,
        string title,
        string message,
        string? secondary,
        Action<NoticeOption> dismiss)
    {
        Definition = definition;
        Title = title;
        Message = message;
        Secondary = secondary;
        _dismiss = dismiss;
        DismissCommand = new RelayCommand(() => _dismiss(this), () => IsDismissible);
    }

    public void InvokeDismiss() => DismissCommand.Execute(null);
}
