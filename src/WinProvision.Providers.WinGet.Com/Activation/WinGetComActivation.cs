using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Microsoft.Management.Deployment;
using WinRT;

namespace WinProvision.Providers.WinGet.Com.Activation;

public enum WinGetComActivationStrategy { LowerTrust, Packaged, InProcCom }

internal sealed partial class WinGetComActivation
{
    private static readonly object StrategyGate = new();
    private static int _externalStrategy;
    private static bool _strategyConfirmed;
    private static bool _forceStrategy;
    private const uint ClsctxLocalServer = 0x4;
    private const uint ClsctxAllowLowerTrustRegistration = 0x04000000;
    private static readonly Guid PackageManagerClsid = new("C53A4F16-787E-42A4-B304-29EFFB4BF597");
    private static readonly Guid PackageManagerIid = new("B375E3B9-F2E0-5C93-87A7-B67497F7E593");
    private static readonly IReadOnlyDictionary<Type, (Guid Clsid, Guid Iid)> Classes = new Dictionary<Type, (Guid, Guid)>
    {
        [typeof(PackageManager)] = (PackageManagerClsid, PackageManagerIid),
        [typeof(FindPackagesOptions)] = (new("572DED96-9C60-4526-8F92-EE7D91D38C1A"), new("A5270EDD-7DA7-57A3-BACE-F2593553561F")),
        [typeof(InstallOptions)] = (new("1095F097-EB96-453B-B4E6-1613637F3B14"), new("6EE9DB69-AB48-5E72-A474-33A924CD23B3")),
        [typeof(PackageMatchFilter)] = (new("D02C9DAF-99DC-429C-B503-4E504E4AB000"), new("D981ECA3-4DE5-5AD7-967A-698C7D60FC3B")),
        [typeof(CreateCompositePackageCatalogOptions)] = (new("526534B8-7E46-47C8-8416-B1685C327D37"), new("21ABAA76-089D-51C5-A745-C85EEFE70116")),
        [typeof(UninstallOptions)] = (new("E1D9A11E-9F85-4D87-9C17-2B93143ADB8D"), new("3EBC67F0-8339-594B-8A42-F90B69D02BBE"))
    };

    private readonly ILogger? _logger;
    private readonly bool _inProc;

    internal WinGetComActivation(ILogger? logger = null, bool inProc = false)
    {
        _logger = logger;
        _inProc = inProc;
    }

    internal WinGetComActivationStrategy Strategy => _inProc ? WinGetComActivationStrategy.InProcCom
        : Volatile.Read(ref _externalStrategy) == 0 ? WinGetComActivationStrategy.LowerTrust : WinGetComActivationStrategy.Packaged;

    internal static void ConfigureExternalStrategy(WinGetComActivationStrategy? strategy)
    {
        lock (StrategyGate)
        {
            _externalStrategy = strategy == WinGetComActivationStrategy.Packaged ? 1 : 0;
            _forceStrategy = strategy is not null;
            _strategyConfirmed = false;
        }
    }

    internal static void ConfirmExternalStrategy()
    {
        lock (StrategyGate) _strategyConfirmed = true;
    }

    internal static bool TryAdvanceExternalStrategy(Exception exception)
    {
        if (exception is OperationCanceledException || !IsStrategyFailure(exception)) return false;
        lock (StrategyGate)
        {
            if (_strategyConfirmed || _forceStrategy || _externalStrategy != 0) return false;
            _externalStrategy = 1;
            return true;
        }
    }

    internal T Create<T>() where T : class
    {
        if (!Classes.TryGetValue(typeof(T), out var ids)) throw new NotSupportedException($"Unsupported WinGet COM activation type: {typeof(T).FullName}");
        if (_inProc) return InProcFactory.Create<T>(ids.Clsid, ids.Iid, _logger);
        int index = Volatile.Read(ref _externalStrategy);
        try
        {
            T value = CreateExternal<T>(ids.Clsid, ids.Iid, index == 0 ? ClsctxLocalServer | ClsctxAllowLowerTrustRegistration : ClsctxLocalServer);
            return value;
        }
        catch (Exception ex) when (index == 0 && !_forceStrategy && !_strategyConfirmed && IsStrategyFailure(ex))
        {
            _logger?.LogWarning(ex, "WinGet COM activation failed with LowerTrust; trying Packaged");
            lock (StrategyGate) _externalStrategy = 1;
            T value = CreateExternal<T>(ids.Clsid, ids.Iid, ClsctxLocalServer);
            return value;
        }
    }

    private static bool IsStrategyFailure(Exception ex) => ex is InvalidCastException || ex.HResult is
        unchecked((int)0x80073D54) or unchecked((int)0x80040154) or unchecked((int)0x80040155)
        or unchecked((int)0x80004002) or unchecked((int)0x80070005) or unchecked((int)0x80080005);

    private T CreateExternal<T>(Guid clsid, Guid iid, uint context) where T : class
    {
        nint unknown = 0;
        try
        {
            int hr = CoCreateInstance(ref clsid, 0, context, ref iid, out unknown);
            _logger?.LogDebug("WinGet CoCreateInstance clsid={Clsid} strategy={Strategy} hr=0x{HResult:X8}", clsid, Strategy, hr);
            Marshal.ThrowExceptionForHR(hr);
            return MarshalGeneric<T>.FromAbi(unknown);
        }
        finally { if (unknown != 0) Marshal.Release(unknown); }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);

    private static class InProcFactory
    {
        private static readonly Lazy<nint> Shim = new(LoadShim);
        private static readonly string EngineHash = "0fb0f8eee1214ae3c17abe740ea32964a7da8b01f829ffa66dec241b9fb3b384";
        private static readonly string ShimHash = "5ab84530dc02019e06c6e482c61ceca3eea87c90cb0962b8a42a0e4c55b08fa3";

        private static nint LoadShim()
        {
            string root = Path.Combine(AppContext.BaseDirectory, "Tool", "winget-com");
            string engine = Path.Combine(root, "WindowsPackageManager.dll");
            string shim = Path.Combine(root, "Microsoft.Management.Deployment.InProc.dll");
            Verify(engine, EngineHash); Verify(shim, ShimHash);
            _ = NativeLibrary.Load(engine);
            return NativeLibrary.Load(shim);
        }

        private static void Verify(string path, string expected)
        {
            using FileStream file = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), Convert.FromHexString(expected)))
                throw new CryptographicException($"Pinned WinGet COM binary hash mismatch: {Path.GetFileName(path)}");
        }

        internal static unsafe T Create<T>(Guid clsid, Guid iid, ILogger? logger) where T : class
        {
            nint module = Shim.Value, factory = 0, instance = 0;
            try
            {
                Guid localClsid = ToInProcClsid(clsid), iidFactory = new("00000001-0000-0000-C000-000000000046");
                var getFactory = (delegate* unmanaged[Stdcall]<Guid*, Guid*, nint*, int>)NativeLibrary.GetExport(module, "DllGetClassObject");
                Marshal.ThrowExceptionForHR(getFactory(&localClsid, &iidFactory, &factory));
                var create = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)(*(nint**)factory)[3];
                Marshal.ThrowExceptionForHR(create(factory, 0, &iid, &instance));
                logger?.LogDebug("WinGet in-process COM activated clsid={Clsid}", clsid);
                return MarshalGeneric<T>.FromAbi(instance);
            }
            finally { if (instance != 0) Marshal.Release(instance); if (factory != 0) Marshal.Release(factory); }
        }

        private static Guid ToInProcClsid(Guid clsid) => clsid.ToString("D").ToLowerInvariant() switch
        {
            "c53a4f16-787e-42a4-b304-29effb4bf597" => new("2DDE4456-64D9-4673-8F7E-A4F19A2E6CC3"),
            "572ded96-9c60-4526-8f92-ee7d91d38c1a" => new("96B9A53A-9228-4DA0-B013-BB1B2031AB3D"),
            "1095f097-eb96-453b-b4e6-1613637f3b14" => new("E2AF3BA8-8A88-4766-9DDA-AE4013ADE286"),
            "d02c9daf-99dc-429c-b503-4e504e4ab000" => new("57DC8962-7343-42CD-B91C-04F6A25DB1D0"),
            "e1d9a11e-9f85-4d87-9c17-2b93143adb8d" => new("869CB959-EB54-425C-A1E4-1A1C291C64E9"),
            "526534b8-7e46-47c8-8416-b1685c327d37" => new("768318A6-2EB5-400D-84D0-DF3534C30F5D"),
            _ => throw new NotSupportedException("WinGet COM class is not allowed for in-process activation.")
        };
    }
}
