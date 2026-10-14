using System.Diagnostics;
using Windows.System;

namespace WinProvision.Store.Services;

public interface IDesktopIntegrationService
{
    void OpenFolder(string path);
    void OpenFile(string path);
    Task OpenUriAsync(Uri uri);
}

public sealed partial class DesktopIntegrationService : IDesktopIntegrationService
{
    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"A pasta não existe: {path}");
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void OpenFile(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("O arquivo solicitado não foi encontrado.", path);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public async Task OpenUriAsync(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme is not ("https" or "http"))
            throw new ArgumentException("Somente endereços HTTP ou HTTPS podem ser abertos.", nameof(uri));
        if (!await Launcher.LaunchUriAsync(uri))
            throw new InvalidOperationException("O Windows não conseguiu abrir o endereço no navegador.");
    }
}
