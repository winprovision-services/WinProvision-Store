using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace WinProvision.Core.Services;

/// <summary>Shell links through their native COM vtables; no dynamic dispatch or runtime marshalling.</summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe partial class ShellShortcut : IDisposable
{
    private nint _link;
    private readonly bool _ownsApartment;
    public ShellShortcut()
    {
        int initialized = CoInitializeEx(0, 0);
        if (initialized < 0 && initialized != unchecked((int)0x80010106))
            Marshal.ThrowExceptionForHR(initialized);
        _ownsApartment = initialized >= 0;
        Guid clsid = new("00021401-0000-0000-C000-000000000046");
        Guid iid = new("000214F9-0000-0000-C000-000000000046");
        try { Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, 1, in iid, out _link)); }
        catch { if (_ownsApartment) CoUninitialize(); throw; }
    }
    public string ReadTarget(string path)
    {
        WithPersistFile(file =>
        {
            fixed (char* text = path)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, uint, int>)(*(nint**)file)[5])(file, text, 0));
        });
        char* target = stackalloc char[32768];
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, nint, uint, int>)(*(nint**)_link)[3])(_link, target, 32768, 0, 4));
        return new string(target);
    }
    public void Save(string path, string targetPath, string description)
    {
        SetText(20, targetPath);
        SetText(9, Path.GetDirectoryName(targetPath)!);
        SetText(7, description);
        fixed (char* target = targetPath)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(nint**)_link)[17])(_link, target, 0));
        WithPersistFile(file =>
        {
            fixed (char* text = path)
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int, int>)(*(nint**)file)[6])(file, text, 1));
        });
    }
    private void SetText(int slot, string value)
    {
        fixed (char* text = value)
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, char*, int>)(*(nint**)_link)[slot])(_link, text));
    }
    private void WithPersistFile(Action<nint> action)
    {
        Guid iid = new("0000010B-0000-0000-C000-000000000046");
        nint file;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)_link)[0])(_link, &iid, &file));
        try { action(file); }
        finally { Release(file); }
    }
    private static void Release(nint pointer)
        => ((delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)pointer)[2])(pointer);
    public void Dispose()
    {
        if (_link == 0) return;
        Release(_link);
        _link = 0;
        if (_ownsApartment) CoUninitialize();
    }
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);
    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint apartment);
    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();
}
