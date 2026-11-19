using System.Security.AccessControl;
using System.Security.Principal;

namespace WinProvision.Core.Services.Office;

/// <summary>Creates a private, disposable directory for Office configuration and download staging.</summary>
internal sealed class OfficeTemporaryDirectory : IDisposable
{
    private bool _disposed;

    private OfficeTemporaryDirectory(string path) => Path = path;

    public string Path { get; }

    public static OfficeTemporaryDirectory Create(string parent, string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(parent);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        string root = System.IO.Path.GetFullPath(parent);
        Directory.CreateDirectory(root);
        string path = System.IO.Path.Combine(root, $"{prefix}-{Guid.NewGuid():N}");
        DirectorySecurity security = CreatePrivateAcl();
        Directory.CreateDirectory(path);
        new DirectoryInfo(path).SetAccessControl(security);
        return new(path);
    }

    public static void EnsurePrivate(string path)
    {
        Directory.CreateDirectory(path);
        DirectoryInfo directory = new(path);
        DirectorySecurity security = CreatePrivateAcl();
        directory.SetAccessControl(security);
    }

    public static void Delete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { CoreLog.Warning(exception, "Office.TemporaryCleanup"); }
    }

    private static DirectorySecurity CreatePrivateAcl()
    {
        SecurityIdentifier user = WindowsIdentity.GetCurrent().User
            ?? throw new UnauthorizedAccessException("Não foi possível identificar a conta atual.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const FileSystemRights rights = FileSystemRights.FullControl;
        const InheritanceFlags inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(user, rights, inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), rights,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), rights,
            inheritance, PropagationFlags.None, AccessControlType.Allow));
        security.SetOwner(user);
        return security;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Delete(Path);
    }
}
