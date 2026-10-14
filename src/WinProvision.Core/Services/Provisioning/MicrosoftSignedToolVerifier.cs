using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace WinProvision.Core.Services.Provisioning;

/// <summary>Validação nativa Authenticode, incluindo o publicador do certificado que assinou o arquivo.</summary>
[SupportedOSPlatform("windows")]
public static partial class MicrosoftSignedToolVerifier
{
    public static void Verify(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("Ferramenta não encontrada.", path);
        nint name = Marshal.StringToCoTaskMemUni(path);
        nint filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, StateAction = 1, File = filePointer, ProviderFlags = 0x1000 };
        Guid action = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            Marshal.StructureToPtr(new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = name }, filePointer, false);
            int result = WinVerifyTrust(new nint(-1), ref action, ref data);
            if (result != 0) throw new InvalidDataException($"Assinatura da ferramenta não validada (0x{result:X8}).", new Win32Exception(result));
            nint provider = WTHelperProvDataFromStateData(data.State);
            nint signerPointer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
            if (signerPointer == 0) throw new InvalidDataException("Assinante da ferramenta não encontrado.");
            Signer signer = Marshal.PtrToStructure<Signer>(signerPointer);
            if (signer.CertificateCount == 0 || signer.Certificates == 0) throw new InvalidDataException("Certificado do assinante não encontrado.");
            ProviderCertificate certificate = Marshal.PtrToStructure<ProviderCertificate>(signer.Certificates);
            CertificateContext context = Marshal.PtrToStructure<CertificateContext>(certificate.Certificate);
            byte[] encoded = new byte[context.Length];
            Marshal.Copy(context.Encoded, encoded, 0, encoded.Length);
            using X509Certificate2 signingCertificate = X509CertificateLoader.LoadCertificate(encoded);
            string subject = signingCertificate.SubjectName.Decode(X500DistinguishedNameFlags.UseNewLines);
            if (!subject.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(line => line.Trim() == "O=Microsoft Corporation"))
                throw new InvalidDataException("A ferramenta não foi assinada pela Microsoft Corporation.");
        }
        finally
        {
            data.StateAction = 2;
            _ = WinVerifyTrust(new nint(-1), ref action, ref data);
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeCoTaskMem(name);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct TrustFile { public uint Size; public nint Path, File, KnownSubject; }
    [StructLayout(LayoutKind.Sequential)] private struct TrustData
    {
        public uint Size; public nint Policy, Sip; public uint UiChoice, RevocationChecks, UnionChoice;
        public nint File; public uint StateAction; public nint State, Url; public uint ProviderFlags, UiContext; public nint SignatureSettings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Signer { public uint Size; public System.Runtime.InteropServices.ComTypes.FILETIME VerifyTime; public uint CertificateCount; public nint Certificates; }
    [StructLayout(LayoutKind.Sequential)] private struct ProviderCertificate { public uint Size; public nint Certificate; }
    [StructLayout(LayoutKind.Sequential)] private struct CertificateContext { public uint Encoding; public nint Encoded; public int Length; public nint Info, Store; }
    [LibraryImport("wintrust.dll")] private static partial int WinVerifyTrust(nint window, ref Guid action, ref TrustData data);
    [LibraryImport("wintrust.dll")] private static partial nint WTHelperProvDataFromStateData(nint state);
    [LibraryImport("wintrust.dll")] private static partial nint WTHelperGetProvSignerFromChain(nint provider, uint signer, int counterSigner, uint counterSignerIndex);
}
