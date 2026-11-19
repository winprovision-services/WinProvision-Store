using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32.SafeHandles;

namespace WinProvision.Core.Services;

/// <summary>Travessia conservadora: mantém ancestrais abertos sem compartilhar escrita ou exclusão.</summary>
[SupportedOSPlatform("windows")]
internal static partial class TemporaryDirectoryTraversal
{
    // FILE_READ_DATA / FILE_LIST_DIRECTORY participa da verificação de compartilhamento;
    // FILE_READ_ATTRIBUTES sozinho não impede escritores existentes.
    private const uint ReadDataAndAttributes = 0x81;
    private const uint DeleteAccess = 0x10000;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
    private const int MaximumDepth = 256;

    internal sealed class Result
    {
        public long FileCount { get; set; }
        public long Bytes { get; set; }
        public long SkippedCount { get; set; }
    }

    internal static Result Run(string root, DateTime cutoff, bool delete, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string volumeRoot = Path.GetPathRoot(root) ?? throw new ArgumentException("Pasta temporária inválida.", nameof(root));
        // Não percorra compartilhamentos de rede ou nomes de dispositivos em uma operação elevada.
        if (volumeRoot.Length != 3 || volumeRoot[1] != ':')
            throw new NotSupportedException("A limpeza exige uma pasta temporária em unidade local.");
        if (string.Equals(root, Path.TrimEndingDirectorySeparator(volumeRoot), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A raiz da unidade não pode ser limpa.", nameof(root));

        var result = new Result();
        var ancestors = new Stack<SafeFileHandle>();
        try
        {
            // Inclui todos os componentes: um link acima de Temp também deve ser rejeitado.
            string current = volumeRoot;
            ancestors.Push(OpenPinnedDirectory(current));
            foreach (string component in root[volumeRoot.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                token.ThrowIfCancellationRequested();
                current = Path.Combine(current, component);
                ancestors.Push(OpenPinnedDirectory(current));
            }
            string finalRoot = GetFinalPath(ancestors.Peek());
            Visit(root, finalRoot, cutoff, delete, token, result, 0);
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            result.SkippedCount++;
        }
        finally
        {
            while (ancestors.TryPop(out SafeFileHandle? handle)) handle.Dispose();
        }
        return result;
    }

    internal static SafeFileHandle OpenPinnedDirectory(string path)
    {
        SafeFileHandle handle = OpenValidated(path, false, out FileInformation info);
        if ((info.Attributes & FileAttributes.Directory) != 0) return handle;
        handle.Dispose();
        throw new IOException("A pasta temporária deixou de ser um diretório.");
    }

    private static void Visit(string directory, string finalDirectory, DateTime cutoff, bool delete,
        CancellationToken token, Result result, int depth)
    {
        if (depth >= MaximumDepth) { result.SkippedCount++; return; }
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using SafeFileHandle handle = OpenValidated(entry, delete, out FileInformation info);
                string finalEntry = GetFinalPath(handle);
                string expected = finalDirectory.TrimEnd('\\') + "\\" + Path.GetFileName(entry);
                if (!string.Equals(finalEntry, expected, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("O destino do arquivo mudou durante a operação.");

                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    Visit(entry, finalEntry, cutoff, delete, token, result, depth + 1);
                    token.ThrowIfCancellationRequested();
                    // Releia a data após remover filhos: a remoção altera a data do diretório.
                    FileInformation current = GetInformation(handle);
                    if (delete && IsOld(current, cutoff) && !Directory.EnumerateFileSystemEntries(entry).Any())
                        MarkForDeletion(handle);
                    continue;
                }

                handle.Dispose();
                using SafeFileHandle file = OpenValidated(entry, delete, out info, FileShare.None);
                if (!string.Equals(GetFinalPath(file), expected, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("O destino do arquivo mudou durante a operação.");
                if ((info.Attributes & FileAttributes.Directory) != 0)
                    throw new IOException("O arquivo temporário passou a ser um diretório.");
                if (!IsOld(info, cutoff)) continue;
                // Não mexa em arquivos com outros nomes (hard links), mesmo dentro de Temp.
                if (info.NumberOfLinks != 1) { result.SkippedCount++; continue; }
                token.ThrowIfCancellationRequested();
                if (delete) MarkForDeletion(file);
                result.FileCount++;
                result.Bytes += ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            }
            catch (Exception exception) when (IsExpected(exception)) { result.SkippedCount++; }
        }
    }

    private static bool IsOld(FileInformation info, DateTime cutoff) =>
        DateTime.FromFileTimeUtc(((long)info.LastWriteTimeHigh << 32) | info.LastWriteTimeLow) < cutoff;

    private static SafeFileHandle OpenValidated(string path, bool delete, out FileInformation info, FileShare share = FileShare.Read)
    {
        SafeFileHandle handle = CreateFile(path, ReadDataAndAttributes | (delete ? DeleteAccess : 0),
            (uint)share, IntPtr.Zero, OpenExisting, OpenReparsePoint | BackupSemantics, IntPtr.Zero);
        try
        {
            if (handle.IsInvalid) throw NativeFailure("Não foi possível abrir o item temporário.");
            info = GetInformation(handle);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Links e pontos de redirecionamento são ignorados.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static unsafe FileInformation GetInformation(SafeFileHandle handle)
    {
        FileInformation info;
        if (!GetFileInformationByHandle(handle, &info))
            throw NativeFailure("Não foi possível verificar o item temporário.");
        return info;
    }

    private static unsafe string GetFinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[32768];
        fixed (char* pointer = buffer)
        {
            uint length = GetFinalPathNameByHandle(handle, pointer, (uint)buffer.Length, 0);
            if (length == 0) throw NativeFailure("Não foi possível validar o destino temporário.");
            if (length >= buffer.Length) throw new IOException("Caminho temporário muito extenso.");
            return new string(pointer, 0, (int)length);
        }
    }

    private static void MarkForDeletion(SafeFileHandle handle)
    {
        byte deleteFile = 1; // FILE_DISPOSITION_INFO usa BOOLEAN de um byte.
        if (!SetFileInformationByHandle(handle, 4, ref deleteFile, 1))
            throw NativeFailure("Não foi possível excluir o item temporário.");
    }

    private static IOException NativeFailure(string message) =>
        new(message, new Win32Exception(Marshal.GetLastPInvokeError()));

    private static bool IsExpected(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public uint CreationTimeLow, CreationTimeHigh;
        public uint LastAccessTimeLow, LastAccessTimeHigh;
        public uint LastWriteTimeLow, LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh, FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh, FileIndexLow;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetFileInformationByHandle(SafeFileHandle handle, FileInformation* information);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(SafeFileHandle handle, char* path, uint length, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref byte information, uint size);
}
