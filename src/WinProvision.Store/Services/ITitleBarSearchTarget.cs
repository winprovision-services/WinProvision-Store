namespace WinProvision.Store.Services;

internal interface ITitleBarSearchTarget
{
    bool SearchContextAvailable { get; }
    string SearchText { get; set; }
    Task SubmitSearchAsync(string query);
    void CancelPendingSearch() { }
    event EventHandler? SearchContextChanged;
}

internal sealed record TitleBarSearchPageDefinition(
    bool Enabled,
    string Placeholder,
    string Trigger,
    int DebounceMs,
    int MinLength,
    bool KeepQuery);
