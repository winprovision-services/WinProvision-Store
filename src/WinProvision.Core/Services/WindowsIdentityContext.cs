using System.Security.Principal;

namespace WinProvision.Core.Services;

public static class WindowsIdentityContext
{
    public static bool IsRunningAsSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) == true;
    }
}
