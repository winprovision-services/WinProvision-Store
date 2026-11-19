namespace WinProvision.Elevation;

internal sealed class ElevationOutputCapture
{
    private const int MaximumCharacters = 256 * 1024;
    private readonly object _gate = new();
    private readonly System.Text.StringBuilder _text = new();

    public void Add(string line)
    {
        lock (_gate)
        {
            if (_text.Length > 0) _text.AppendLine();
            _text.Append(line);
            if (_text.Length > MaximumCharacters)
                _text.Remove(0, _text.Length - MaximumCharacters);
        }
    }

    public string GetText()
    {
        lock (_gate) return _text.ToString();
    }
}
