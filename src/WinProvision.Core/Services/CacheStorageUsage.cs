namespace WinProvision.Core.Services;

public sealed record CacheUsage(long Bytes = 0, int Files = 0, bool Partial = false)
{
    public static CacheUsage operator +(CacheUsage a, CacheUsage b) => new(a.Bytes + b.Bytes, a.Files + b.Files, a.Partial || b.Partial);
}

public static class CacheStorageUsage
{
    public static CacheUsage Measure(string folder, string pattern = "*", bool recursive = true, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        long size = 0;
        int count = 0;
        bool partial = false;
        try
        {
            if (!Directory.Exists(folder)) return new();
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) return new(Partial: true);
            foreach (string file in Directory.EnumerateFiles(folder, pattern, new EnumerationOptions
            {
                RecurseSubdirectories = recursive, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false
            }))
            {
                token.ThrowIfCancellationRequested();
                try { size += new FileInfo(file).Length; count++; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { partial = true; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { partial = true; }
        return new(size, count, partial);
    }
}
