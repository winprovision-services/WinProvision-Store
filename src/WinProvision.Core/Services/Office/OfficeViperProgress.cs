using WinProvision.Core.Models;
using WinProvision.Viper;

namespace WinProvision.Core.Services.Office;

/// <summary>Agrega bytes dos fluxos do Office; não estima tamanhos nem progresso de instalação.</summary>
internal sealed class OfficeViperProgress(IReadOnlyDictionary<string, long?> lengths, Action<InstallProgressUpdate> report)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long?> _lengths = new(lengths, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _received = new(StringComparer.OrdinalIgnoreCase);
    private long _lastReport;

    public IProgress<ViperProgress> ForFile(string destination) => new FileProgress(this, destination);

    private void Update(string destination, ViperProgress value)
    {
        lock (_gate)
        {
            // Prefer the actual transfer length to the preliminary HEAD response.
            // This also recovers a total when HEAD was unavailable.
            if (value.TotalBytes is > 0) _lengths[destination] = value.TotalBytes;
            _received[destination] = Math.Max(0, value.DownloadedBytes);
            long now = Environment.TickCount64;
            if (now - _lastReport < 200 && value.Phase != ViperPhase.Completed) return;
            _lastReport = now;
            bool known = _lengths.Count > 0 && _lengths.Values.All(length => length is > 0);
            double? percent = null;
            if (known)
            {
                long total = _lengths.Values.Sum(length => length!.Value);
                long received = _lengths.Sum(entry => Math.Min(entry.Value!.Value, _received.GetValueOrDefault(entry.Key)));
                percent = Math.Clamp(received * 100d / total, 0, 100);
            }
            report(percent >= 100
                ? new(InstallProgressPhase.Preparing, Method: WingetMethod.OwnApi)
                : new(InstallProgressPhase.Downloading, percent, WingetMethod.OwnApi));
        }
    }

    private sealed class FileProgress(OfficeViperProgress owner, string destination) : IProgress<ViperProgress>
    {
        public void Report(ViperProgress value) => owner.Update(destination, value);
    }
}
