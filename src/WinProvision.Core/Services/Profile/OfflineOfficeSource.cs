using System.Runtime.Versioning;
using System.Security.Cryptography;
using WinProvision.Core.Models;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Provisioning;
using WixToolset.Dtf.Compression.Cab;

namespace WinProvision.Core.Services.Profile;

public interface IOfflineOfficeSource
{
    /// <summary>Copies a verified plataforma do sistema ODT executable; returns its independently verified SHA256.</summary>
    Task<string> CopySetupAsync(string destination, IProgress<OfflineCollectionProgress>? progress, CancellationToken token);
    Task DownloadPayloadAsync(string setup, string configuration, IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
        => throw new NotSupportedException("Este provedor não oferece download offline dos arquivos Office.");
}

[SupportedOSPlatform("windows")]
public sealed class OfflineOfficeSource(WinProvisionApiService api) : IOfflineOfficeSource
{
    public async Task DownloadPayloadAsync(string setup, string configuration, IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
    {
        MicrosoftSignedToolVerifier.Verify(setup);
        progress?.Report(new("Baixando os arquivos completos do Office para a pasta local. Esta etapa pode levar vários minutos..."));
        int code = await OdtProcessRunner.RunDownloadAsync(setup, configuration,
            line => progress?.Report(new(line)), token).ConfigureAwait(false);
        if (code != 0) throw new InvalidDataException($"O ODT não concluiu o download do Office (código {code}). Os arquivos não serão publicados.");
    }
    public async Task<string> CopySetupAsync(string destination, IProgress<OfflineCollectionProgress>? progress, CancellationToken token)
    {
        string scratch = Path.Combine(Path.GetDirectoryName(destination)!, ".odt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            progress?.Report(new("Obtendo o ODT oficial e verificando SHA256…"));
            PackageManifest manifest = await api.GetPackageAsync(OfficeDeploymentToolService.OdtWingetPackageId, token, "winget").ConfigureAwait(false)
                ?? throw new InvalidDataException("ODT não encontrado no catálogo.");
            if (manifest.Id != OfficeDeploymentToolService.OdtWingetPackageId || manifest.Source != "winget")
                throw new InvalidDataException("O catálogo não retornou o ODT solicitado.");
            CatalogInstaller installer = manifest.Installers.FirstOrDefault(item =>
                Uri.TryCreate(item.Url, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https" &&
                uri.Host.Equals("download.microsoft.com", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(uri.UserInfo) &&
                (item.Sha256 is { Length: 64 } && item.Sha256.All(Uri.IsHexDigit))) ?? throw new InvalidDataException("O ODT não possui download oficial com SHA256.");
            string extractor = Path.Combine(scratch, "odt.exe");
            await api.DownloadVerifiedInstallerAsync(installer, extractor, null, token).ConfigureAwait(false);
            MicrosoftSignedToolVerifier.Verify(extractor);
            string setup = Path.Combine(scratch, OfflineOfficeConfiguration.SetupFile);
            await ExtractSetupAsync(extractor, setup, token).ConfigureAwait(false);
            MicrosoftSignedToolVerifier.Verify(setup);
            await using var input = File.OpenRead(setup);
            string hash = Convert.ToHexString(await SHA256.HashDataAsync(input, token).ConfigureAwait(false));
            input.Position = 0;
            await AtomicFile.WriteAsync(destination, (output, cancellation) => input.CopyToAsync(output, cancellation), token).ConfigureAwait(false);
            return hash;
        }
        finally { Directory.Delete(scratch, true); }
    }

    internal static Task ExtractSetupAsync(string extractor, string setup, CancellationToken token) => Task.Run(() =>
    {
        token.ThrowIfCancellationRequested();
        var cabinet = new CabInfo(extractor);
        // DTF searches for the embedded CAB offset. Only this exact file is mapped to the output.
        string name = cabinet.GetFiles().Single(file => file.Name.Equals(OfflineOfficeConfiguration.SetupFile, StringComparison.OrdinalIgnoreCase)).Name;
        cabinet.UnpackFile(name, setup);
        token.ThrowIfCancellationRequested();
    }, token);
}
