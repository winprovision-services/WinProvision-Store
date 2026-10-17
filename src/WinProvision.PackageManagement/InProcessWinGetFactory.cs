using System.Runtime.InteropServices;
using System.Security.Principal;
using WinProvision.Core.Services;

namespace WinProvision.PackageManagement;

/// <summary>Official WinGet InProcCom, hosted exclusively in the elevated operation process.</summary>
public static class InProcessWinGetFactory
{
    public static bool Enabled { get; private set; }
    private static readonly Lazy<nint> Module = new(LoadModule);
    public static void Enable()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new InvalidOperationException("O host COM local exige elevação pelo WinProvision Elevator.");
        _ = Module.Value;
        Enabled = true;
    }

    private static nint LoadModule()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Tool", "winget-com");
        string engine = Path.Combine(root, "WindowsPackageManager.dll");
        string shim = Path.Combine(root, "Microsoft.Management.Deployment.InProc.dll");
        GsudoElevationService.VerifyHelper(engine, "0fb0f8eee1214ae3c17abe740ea32964a7da8b01f829ffa66dec241b9fb3b384");
        GsudoElevationService.VerifyHelper(shim, "5ab84530dc02019e06c6e482c61ceca3eea87c90cb0962b8a42a0e4c55b08fa3");
        // Load the pinned dependency by absolute path before the shim's import is resolved.
        _ = NativeLibrary.Load(engine);
        return NativeLibrary.Load(shim);
    }

    internal static unsafe T Create<T>(Guid outOfProcessClsid, Guid iid)
    {
        if (!Enabled) throw new InvalidOperationException("Host COM local não inicializado.");
        Guid clsid = outOfProcessClsid.ToString("D").ToLowerInvariant() switch
        {
            "c53a4f16-787e-42a4-b304-29effb4bf597" => new("2DDE4456-64D9-4673-8F7E-A4F19A2E6CC3"),
            "572ded96-9c60-4526-8f92-ee7d91d38c1a" => new("96B9A53A-9228-4DA0-B013-BB1B2031AB3D"),
            "1095f097-eb96-453b-b4e6-1613637f3b14" => new("E2AF3BA8-8A88-4766-9DDA-AE4013ADE286"),
            "d02c9daf-99dc-429c-b503-4e504e4ab000" => new("57DC8962-7343-42CD-B91C-04F6A25DB1D0"),
            "e1d9a11e-9f85-4d87-9c17-2b93143adb8d" => new("869CB959-EB54-425C-A1E4-1A1C291C64E9"),
            "526534b8-7e46-47c8-8416-b1685c327d37" => new("768318A6-2EB5-400D-84D0-DF3534C30F5D"),
            _ => throw new NotSupportedException("Classe COM não permitida no host elevado.")
        };
        var getFactory = (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(Module.Value, "DllGetClassObject");
        Guid iidFactory = new("00000001-0000-0000-C000-000000000046");
        nint factory = 0, instance = 0;
        try
        {
            Marshal.ThrowExceptionForHR(getFactory(&clsid, &iidFactory, &factory));
            var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
            Marshal.ThrowExceptionForHR(create(factory, 0, &iid, &instance));
            PackageDiagnostics.Write($"COM INPROC type={typeof(T).Name} pid={Environment.ProcessId} elevated=true");
            return WinRT.MarshalGeneric<T>.FromAbi(instance);
        }
        finally
        {
            if (instance != 0) Marshal.Release(instance);
            if (factory != 0) Marshal.Release(factory);
        }
    }
}
