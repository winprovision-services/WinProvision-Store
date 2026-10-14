using System.Security.Cryptography;
using System.Text;

namespace WinProvision.Core.Services.Office;

/// <summary>Inventário de integridade do conteúdo ODT, lido e gravado por streaming.</summary>
public static class OfficeOfflinePayload
{
    public const string IndexFileName = "payload-sha256.txt";
    private const int MaximumFiles = 20000;

    public static string GetVersion(string root)
    {
        CheckDirectory(Path.Combine(root, "Office", "Data"));
        return Directory.EnumerateDirectories(Path.Combine(root, "Office", "Data"))
            .Select(path => Version.TryParse(Path.GetFileName(path), out var version) && version.Revision >= 0 ? version : null)
            .Where(version => version is not null).OrderDescending().FirstOrDefault()?.ToString()
            ?? throw new InvalidDataException("A versão dos arquivos locais do Office não foi encontrada.");
    }

    public static IReadOnlyList<string> GetFiles(string root, CancellationToken token = default)
    {
        string data = Path.Combine(root, "Office", "Data");
        CheckDirectory(root);
        CheckDirectory(Path.Combine(root, "Office"));
        CheckDirectory(data);
        var pending = new Stack<string>();
        var files = new List<string>();
        pending.Push(data);
        int directories = 1;
        while (pending.TryPop(out string? folder))
        {
            token.ThrowIfCancellationRequested();
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("O conteúdo Office não pode conter links de sistema de arquivos.");
                files.Add(file);
                if (files.Count > MaximumFiles) throw new InvalidDataException("Inventário Office excede o limite de arquivos.");
            }
            foreach (string child in Directory.EnumerateDirectories(folder))
            {
                if (++directories > MaximumFiles) throw new InvalidDataException("Conteúdo Office excede o limite de pastas.");
                CheckDirectory(child);
                pending.Push(child);
            }
        }
        if (!files.Any(file => Path.GetExtension(file).Equals(".cab", StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Length > 0)
            || !files.Any(file => Path.GetExtension(file).Equals(".dat", StringComparison.OrdinalIgnoreCase) && new FileInfo(file).Length > 0))
            throw new InvalidDataException("O ODT não preparou os arquivos CAB e DAT do Office.");
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return files;
    }

    public static async Task WriteIndexAsync(string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var files = GetFiles(root, token);
        await AtomicFile.WriteAsync(Path.Combine(root, IndexFileName), async (output, cancellation) =>
        {
            using var writer = new StreamWriter(output, new UTF8Encoding(false), leaveOpen: true);
            foreach (string file in files)
            {
                cancellation.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains('\t') || relative.Contains('\n') || relative.Contains('\r'))
                    throw new InvalidDataException("Nome inválido no conteúdo Office.");
                await writer.WriteLineAsync(($"{await HashAsync(file, cancellation)}\t{relative}").AsMemory(), cancellation);
            }
            await writer.FlushAsync(cancellation);
        }, token).ConfigureAwait(false);
    }

    public static async Task VerifyAsync(string root, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string index = Path.Combine(root, IndexFileName);
        if (new FileInfo(index).Length > 8 * 1024 * 1024 || (File.GetAttributes(index) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Inventário de integridade Office inválido.");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(index))
        {
            token.ThrowIfCancellationRequested();
            int separator = line.IndexOf('\t');
            if (separator != 64 || !line.AsSpan(0, 64).ToString().All(Uri.IsHexDigit))
                throw new InvalidDataException("SHA256 inválido no inventário Office.");
            string relative = line[(separator + 1)..];
            if (!relative.StartsWith("Office/Data/", StringComparison.OrdinalIgnoreCase)
                || relative.Contains(':') || relative.Contains('\\')
                || relative.Split('/').Any(segment => segment is "" or "." or ".."))
                throw new InvalidDataException("Caminho inválido no inventário Office.");
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!expected.Add(path) || expected.Count > MaximumFiles)
                throw new InvalidDataException("Inventário Office duplicado ou excessivo.");
            // GetFiles verifies the directory tree before any payload file is opened.
        }
        var actual = GetFiles(root, token);
        if (!expected.SetEquals(actual)) throw new InvalidDataException("O conteúdo Office difere do inventário de integridade.");
        foreach (string line in File.ReadLines(index))
        {
            string path = Path.Combine(root, line[65..]);
            if (!string.Equals(line[..64], await HashAsync(path, token).ConfigureAwait(false), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SHA256 divergente nos arquivos locais do Office.");
        }
    }

    private static void CheckDirectory(string path)
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Pasta de conteúdo Office ausente ou inválida.");
    }

    private static async Task<string> HashAsync(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false));
    }
}
