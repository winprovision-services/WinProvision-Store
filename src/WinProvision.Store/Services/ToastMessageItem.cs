using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace WinProvision.Store.Services;

public sealed partial class ToastMessageItem(
    string id,
    string dedupeKey,
    string title,
    string message,
    string iconGlyph,
    Brush accentBrush) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    public string DedupeKey { get; } = dedupeKey;
    public string Title { get; } = title;
    private readonly string _message = message;
    public string Message => Count > 1 ? $"{_message} ({Count})" : _message;
    public string IconGlyph { get; } = iconGlyph;
    public Brush AccentBrush { get; } = accentBrush;
    private int _count = 1;
    private int _pendingCount;
    public int Count
    {
        get => _count;
        set
        {
            if (_count == value) return;
            _count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Count)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Message)));
        }
    }
    public int PendingCount
    {
        get => _pendingCount;
        set
        {
            if (_pendingCount == value) return;
            _pendingCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PendingSummary)));
        }
    }

    public string PendingSummary => PendingCount switch
    {
        1 => "1 aviso novo",
        > 1 => $"{PendingCount} avisos novos",
        _ => string.Empty
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}
