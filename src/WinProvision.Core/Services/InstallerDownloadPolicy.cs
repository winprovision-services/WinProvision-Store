namespace WinProvision.Core.Services;

public static class InstallerDownloadPolicy
{
    public static string ResolveDirectory(string? explicitDirectory = null)
        => Path.GetFullPath(explicitDirectory ?? new OperationPreferencesService().Load().DownloadDirectory
            ?? Office.OfficeCliDownloadService.GetDownloadsFolder());

    public static string FileName(InstallerNameStyle style, string id, string? version, string? architecture, string original)
    {
        original = Path.GetFileName(original);
        string extension = Path.GetExtension(original);
        if (extension.Length == 0 || extension.Length > 12 || extension.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '.')) extension = ".bin";
        string stem = style switch
        {
            InstallerNameStyle.Original => Path.GetFileNameWithoutExtension(original),
            InstallerNameStyle.PackageVersion => $"{id}-{version ?? Path.GetFileNameWithoutExtension(original)}",
            _ => version is null || architecture is null ? $"{id}-{Path.GetFileNameWithoutExtension(original)}" : $"{id}-{version}-{architecture}"
        };
        stem = (style == InstallerNameStyle.Original
            ? string.Concat(stem.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c))
            : string.Concat(stem.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'))).Trim('.', ' ');
        if (string.IsNullOrWhiteSpace(stem)) stem = "instalador";
        if (stem.Length > 160) stem = stem[..160];
        if (stem.Split('.')[0].ToUpperInvariant() is "CON" or "PRN" or "AUX" or "NUL" or "COM1" or "COM2" or "COM3" or "COM4" or "COM5" or "COM6" or "COM7" or "COM8" or "COM9" or "LPT1" or "LPT2" or "LPT3" or "LPT4" or "LPT5" or "LPT6" or "LPT7" or "LPT8" or "LPT9") stem = "_" + stem;
        return stem + extension;
    }

    // File.Move without overwrite is the final collision guard, including concurrent downloads.
    public static string Publish(string source, string folder, string name)
    {
        Directory.CreateDirectory(folder);
        for (int suffix = 0; suffix < 10000; suffix++)
        {
            string path = Path.Combine(folder, suffix == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)} ({suffix}){Path.GetExtension(name)}");
            try { File.Move(source, path, false); return path; }
            catch (IOException) when (File.Exists(source) && File.Exists(path)) { }
        }
        throw new IOException("Há muitos arquivos com o mesmo nome na pasta escolhida.");
    }
}
