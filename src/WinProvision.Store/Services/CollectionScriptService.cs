using System.Text;
using System.Text.Json;
using WinProvision.Core.Models;
using WinProvision.Core.Models.Office;
using WinProvision.Core.Services.Office;
using WinProvision.Core.Services.Profile;
using WinProvision.Core.Services;

namespace WinProvision.Store.Services;

public sealed class CollectionScriptService(PackageInstallOptionsService? packageInstallOptionsService)
{
    private readonly PackageInstallOptionsService? _packageInstallOptionsService = packageInstallOptionsService;
    public string Build(IEnumerable<AppEntry> apps, Func<AppEntry, string?>? pinnedVersion = null)
    {
        const string catalogBaseUrl = CatalogEndpoints.CatalogBaseUrl;
        var scriptEntries = new List<Dictionary<string, object?>>();
        foreach (AppEntry app in apps)
        {
            if (app.Office is { } officeOptions)
            {
                OfficePlan? plan = OfficePlanCatalog.ByProductId(officeOptions.ProductId);
                OfficePlan[] additionalProducts = (officeOptions.AdditionalProductIds ?? [])
                    .Select(OfficePlanCatalog.ByProductId).Where(product => product is not null).Cast<OfficePlan>().ToArray();
                if (plan is null || additionalProducts.Length != (officeOptions.AdditionalProductIds?.Count ?? 0))
                    throw new InvalidOperationException($"O plano de Office de \"{app.Name}\" contém um produto que não existe no catálogo local.");

                var officeRequest = new OfficeInstallRequest(
                    plan,
                    officeOptions.Architecture,
                    officeOptions.LanguageId,
                    officeOptions.ExcludedApps ?? [],
                    DisplayNone: officeOptions.Silent,
                    AdditionalLanguageIds: officeOptions.AdditionalLanguageIds,
                    DisplayLevel: officeOptions.Silent ? OfficeDisplayLevel.Silent : OfficeDisplayLevel.Visible,
                    ChannelOverride: officeOptions.ChannelOverride,
                    AutoUpdatesEnabled: officeOptions.AutoUpdatesEnabled,
                    AdditionalProducts: additionalProducts, DownloadBeforeInstall: officeOptions.DownloadBeforeInstall);
                if (OfficeConfigXmlBuilder.ValidateRequest(officeRequest) is { } officeValidationError)
                    throw new InvalidOperationException($"Configuração de Office inválida para \"{app.Name}\": {officeValidationError}");

                string officeXml = OfficeConfigXmlBuilder.Build(officeRequest).ToString(System.Xml.Linq.SaveOptions.None);
                scriptEntries.Add(Entry("office", app.Id, app.Name,
                    OfficeConfigurationBase64: Convert.ToBase64String(Encoding.UTF8.GetBytes(officeXml))));
                continue;
            }

            PackageInstallOptions? options = IsMsStoreSource(app.Source)
                ? null
                : _packageInstallOptionsService?.Get(app.Id, app.Source);
            scriptEntries.Add(Entry(
                "package", app.Id, app.Name,
                IsMsStoreSource(app.Source) ? "msstore" : "winget",
                new Dictionary<string, object?>
                {
                    ["Interactive"] = IsMsStoreSource(app.Source) || options?.Interactive == true,
                    ["Scope"] = options?.Scope, ["Architecture"] = options?.Architecture,
                    ["IgnoreSecurityHash"] = false,
                    ["InstallLocation"] = options?.InstallLocation,
                    ["Version"] = pinnedVersion?.Invoke(app) ?? options?.Version
                }));
        }

        string packageJson = AotJson.Serialize(scriptEntries, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        string encodedPackages = Convert.ToBase64String(Encoding.UTF8.GetBytes(packageJson));
        var builder = new StringBuilder();
        builder.AppendLine("[CmdletBinding()]");
        builder.AppendLine("param()");
        builder.AppendLine("Set-StrictMode -Version Latest");
        builder.AppendLine("$ErrorActionPreference = 'Stop'");
        builder.AppendLine("[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12");
        builder.AppendLine($"$catalogBaseUrl = {ToPowerShellLiteral(catalogBaseUrl)}");
        builder.AppendLine($"$packages = @([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String({ToPowerShellLiteral(encodedPackages)})) | ConvertFrom-Json)");
        builder.AppendLine("$failedPackages = [System.Collections.Generic.List[string]]::new()");
        builder.AppendLine("$curlCommand = Get-Command curl.exe -ErrorAction SilentlyContinue");
        builder.AppendLine("foreach ($package in $packages) {");
        builder.AppendLine("    $workingDirectory = Join-Path ([IO.Path]::GetTempPath()) ('WinProvision-' + [Guid]::NewGuid().ToString('N'))");
        builder.AppendLine("    if ($package.Kind -eq 'office') {");
        builder.AppendLine("        $officeStep = 'obtenção dos dados do ODT pela API WinProvision'");
        builder.AppendLine("        $setupPath = $null");
        builder.AppendLine("        $officeOffline = $false");
        builder.AppendLine("        try {");
        builder.AppendLine("            try {");
        builder.AppendLine("            if (-not $curlCommand) { throw 'curl.exe não está disponível.' }");
        builder.AppendLine("            [void][IO.Directory]::CreateDirectory($workingDirectory)");
        builder.AppendLine("            $configPath = Join-Path $workingDirectory 'configuration.xml'");
        builder.AppendLine("            [IO.File]::WriteAllBytes($configPath, [Convert]::FromBase64String([string]$package.OfficeConfigurationBase64))");
        builder.AppendLine("            $officeXml = [Xml.XmlDocument]::new(); $officeXml.XmlResolver = $null; $officeXml.Load($configPath)");
        builder.AppendLine("            $officeOffline = $officeXml.Configuration.Add.GetAttribute('AllowCdnFallback') -eq 'FALSE'");
        builder.AppendLine("            if ($officeOffline) {");
        builder.AppendLine("                $workingDirectory = Join-Path $env:LOCALAPPDATA ('WinProvision\\Office\\OfflineSources\\' + [Guid]::NewGuid().ToString('N')); [void][IO.Directory]::CreateDirectory($workingDirectory)");
        builder.AppendLine("                $configPath = Join-Path $workingDirectory 'configuration.xml'; $officeXml.Configuration.Add.SetAttribute('SourcePath', $workingDirectory); $officeXml.Save($configPath)");
        builder.AppendLine("            }");
        builder.AppendLine("            $odtId = 'Microsoft.OfficeDeploymentTool'; $odtSegments = @($odtId -split '\\.')");
        builder.AppendLine("            $odtPrefix = $odtSegments[0].Substring(0, 1).ToLowerInvariant(); $odtPathParts = @($odtSegments | ForEach-Object { [Uri]::EscapeDataString([Uri]::EscapeDataString($_.Normalize([Text.NormalizationForm]::FormKC).ToLowerInvariant())) })");
        builder.AppendLine("            $odtDetailPath = 'apps/' + $odtPrefix + '/' + ($odtPathParts -join '/') + '/app.json'; $odtManifestPath = Join-Path $workingDirectory 'odt-app.json'");
        builder.AppendLine("            $odtManifestUri = [Uri]::new([Uri]$catalogBaseUrl, $odtDetailPath)");
        builder.AppendLine("            & $curlCommand.Source '--fail' '--location' '--silent' '--show-error' '--compressed' '--retry' '3' '--connect-timeout' '25' '--output' $odtManifestPath $odtManifestUri.AbsoluteUri");
        builder.AppendLine("            if ($LASTEXITCODE -ne 0) { throw ('Falha ao baixar o manifesto do ODT; curl.exe retornou {0}.' -f $LASTEXITCODE) }");
        builder.AppendLine("            $odtManifest = Get-Content -LiteralPath $odtManifestPath -Raw | ConvertFrom-Json");
        builder.AppendLine("            if ($odtManifest.id -ne $odtId -or $odtManifest.source -ne 'winget') { throw 'O manifesto do ODT retornado pela API não corresponde ao pacote.' }");
        builder.AppendLine("            $odtInstallers = @($odtManifest.installers | Where-Object { $_.url -and $_.sha256 -match '^[A-Fa-f0-9]{64}$' }); if ($odtInstallers.Count -eq 0) { throw 'A API não retornou um instalador ODT válido com SHA-256.' }");
        builder.AppendLine("            $odtInstaller = $odtInstallers | Where-Object { -not $_.architecture } | Select-Object -First 1; if (-not $odtInstaller) { $odtInstaller = $odtInstallers | Select-Object -First 1 }");
        builder.AppendLine("            $odtUri = [Uri]$odtInstaller.url; if ($odtUri.Scheme -ne 'https' -or [IO.Path]::GetExtension($odtUri.AbsolutePath) -ine '.exe') { throw 'A API retornou uma URL de ODT inválida.' }");
        builder.AppendLine("            $odtDownloadPath = Join-Path $workingDirectory 'officedeploymenttool.exe'");
        builder.AppendLine("            $officeStep = 'download do instalador ODT pela API WinProvision'");
        builder.AppendLine("            & $curlCommand.Source '--fail' '--location' '--silent' '--show-error' '--compressed' '--retry' '3' '--connect-timeout' '25' '--output' $odtDownloadPath $odtUri.AbsoluteUri");
        builder.AppendLine("            if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $odtDownloadPath -PathType Leaf)) { throw ('Falha ao baixar o instalador ODT pela API; curl.exe retornou {0}.' -f $LASTEXITCODE) }");
        builder.AppendLine("            if ((Get-FileHash -LiteralPath $odtDownloadPath -Algorithm SHA256).Hash -ine $odtInstaller.sha256) { throw 'O SHA-256 do instalador ODT não confere com o catálogo da API.' }");
        builder.AppendLine("            $odtHeader = [IO.File]::ReadAllBytes($odtDownloadPath); if ($odtHeader[0] -ne 0x4D -or $odtHeader[1] -ne 0x5A) { throw 'O arquivo ODT retornado pela API não é um executável Windows válido.' }");
        builder.AppendLine("            $officeStep = 'extração local do ODT'");
        builder.AppendLine("            $signature = Get-AuthenticodeSignature -LiteralPath $odtDownloadPath; if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'O extrator ODT não possui assinatura válida da Microsoft.' }");
        builder.AppendLine("            & $odtDownloadPath '/quiet' ('/extract:{0}' -f $workingDirectory); if ($LASTEXITCODE -ne 0) { throw ('A extração local do ODT retornou código {0}.' -f $LASTEXITCODE) }");
        builder.AppendLine("            $setupPath = Join-Path $workingDirectory 'setup.exe'; if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) { throw 'O instalador ODT não criou setup.exe ao lado do configuration.xml.' }");
        builder.AppendLine("        } catch { $officeApiFailure = $_.Exception.Message; Write-Warning -Message ('API WinProvision não forneceu o ODT: {0}' -f $officeApiFailure) }");
        builder.AppendLine("        if (-not $setupPath) {");
        builder.AppendLine("            $officeStep = 'fallback pelo WinGet CLI'");
        builder.AppendLine("            try {");
        builder.AppendLine("                $wingetOdt = Get-Command winget.exe -ErrorAction SilentlyContinue; if (-not $wingetOdt) { throw ('winget.exe não está instalado; falha da API: {0}' -f $officeApiFailure) }");
        builder.AppendLine("                & $wingetOdt.Source 'install' '--id' 'Microsoft.OfficeDeploymentTool' '--exact' '--source' 'winget' '--accept-source-agreements' '--accept-package-agreements' '--disable-interactivity' '--silent'; if ($LASTEXITCODE -ne 0) { throw ('WinGet retornou código {0}.' -f $LASTEXITCODE) }");
        builder.AppendLine("                $knownOdtPaths = @((Join-Path $env:LOCALAPPDATA 'WinProvision\\OfficeDeploymentTool\\setup.exe'), (Join-Path $env:ProgramFiles 'OfficeDeploymentTool\\setup.exe'))");
        builder.AppendLine("                $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86); if ($programFilesX86) { $knownOdtPaths += Join-Path $programFilesX86 'OfficeDeploymentTool\\setup.exe' }");
        builder.AppendLine("                $setupPath = $knownOdtPaths | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1; if (-not $setupPath) { throw 'WinGet instalou o pacote, mas setup.exe não foi encontrado nos locais conhecidos.' }");
        builder.AppendLine("            } catch { throw ('API WinProvision: {0}; WinGet CLI: {1}' -f $officeApiFailure, $_.Exception.Message) }");
        builder.AppendLine("        }");
        builder.AppendLine("            $officeStep = 'execução do ODT (pode solicitar confirmação do UAC)'");
        builder.AppendLine("            $signature = Get-AuthenticodeSignature -LiteralPath $setupPath; if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') { throw 'setup.exe não possui assinatura válida da Microsoft.' }");
        builder.AppendLine("            if ($officeOffline) {");
        builder.AppendLine("                $localSetup = Join-Path $workingDirectory 'setup.exe'; if ([IO.Path]::GetFullPath($setupPath) -ne [IO.Path]::GetFullPath($localSetup)) { Copy-Item -LiteralPath $setupPath -Destination $localSetup -Force }; $setupPath = $localSetup");
        builder.AppendLine("                $officeStep = 'download dos arquivos locais do Office'; $download = Start-Process -FilePath $setupPath -ArgumentList ('/download \"{0}\"' -f $configPath) -WorkingDirectory $workingDirectory -WindowStyle Hidden -Wait -PassThru -ErrorAction Stop");
        builder.AppendLine("                if ($download.ExitCode -ne 0) { throw ('Download do Office incompleto: {0}. A instalação não será iniciada.' -f $download.ExitCode) }");
        builder.AppendLine("                $version = Get-ChildItem -LiteralPath (Join-Path $workingDirectory 'Office\\Data') -Directory | Where-Object { $_.Name -match '^\\d+\\.\\d+\\.\\d+\\.\\d+$' } | Sort-Object { [Version]$_.Name } -Descending | Select-Object -First 1; if (-not $version) { throw 'A versão local do Office não foi encontrada.' }");
        builder.AppendLine("                $officeXml.Configuration.Add.SetAttribute('Version', $version.Name); $officeXml.Save($configPath)");
        builder.AppendLine("            }");
        builder.AppendLine("            $officeProcess = Start-Process -FilePath $setupPath -ArgumentList ('/configure \"{0}\"' -f $configPath) -Verb RunAs -Wait -PassThru -ErrorAction Stop");
        builder.AppendLine("            if ($officeProcess.ExitCode -ne 0) { throw ('O ODT retornou código {0}.' -f $officeProcess.ExitCode) }");
        builder.AppendLine("            Write-Information -MessageData ('Office configurado: {0}' -f $package.Name) -InformationAction Continue");
        builder.AppendLine("        } catch { $officeFailure = '{0}: {1} (arquivos preservados em {2})' -f $officeStep, $_.Exception.Message, $workingDirectory; $failedPackages.Add(('Microsoft.OfficeDeploymentTool: {0}' -f $officeFailure)); Write-Warning -Message ('Falha no Office {0}: {1}' -f $package.Name, $officeFailure); continue }");
        builder.AppendLine("        if (-not $officeOffline -and (Test-Path -LiteralPath $workingDirectory)) { Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue }");
        builder.AppendLine("        continue");
        builder.AppendLine("    }");
        builder.AppendLine("    $apiInstalled = $false");
        builder.AppendLine("    $apiFailure = 'O catálogo da API não está disponível.'");
        builder.AppendLine("    try {");
        builder.AppendLine("        if ($package.InstallOptions.Version) { throw 'A versão fixada será instalada pelo WinGet.' }");
        builder.AppendLine("        if (-not $curlCommand) { throw 'curl.exe não está disponível.' }");
        builder.AppendLine("        if ($package.Source -eq 'msstore') { throw 'A API não instala aplicativos da Microsoft Store.' }");
        builder.AppendLine("        $segments = @([string]$package.Id -split '\\.'); if ($segments.Count -eq 1) { $segments = @($segments[0], $segments[0]) }");
        builder.AppendLine("        $prefix = $segments[0].Substring(0, 1).ToLowerInvariant(); if ($prefix -notmatch '^[0-9a-z]$') { $prefix = '_' }");
        builder.AppendLine("        $pathParts = @($segments | ForEach-Object { [Uri]::EscapeDataString([Uri]::EscapeDataString($_.Normalize([Text.NormalizationForm]::FormKC).ToLowerInvariant())) })");
        builder.AppendLine("        $detailPath = 'apps/' + $prefix + '/' + ($pathParts -join '/') + '/app.json'");
        builder.AppendLine("        [void][IO.Directory]::CreateDirectory($workingDirectory)");
        builder.AppendLine("        $manifestUri = [Uri]::new([Uri]$catalogBaseUrl, $detailPath)");
        builder.AppendLine("        $manifestPath = Join-Path $workingDirectory 'app.json'");
        builder.AppendLine("        & $curlCommand.Source '--fail' '--location' '--silent' '--show-error' '--compressed' '--retry' '3' '--connect-timeout' '25' '--output' $manifestPath $manifestUri.AbsoluteUri");
        builder.AppendLine("        if ($LASTEXITCODE -ne 0) { throw ('Falha ao baixar o manifesto; curl.exe retornou {0}.' -f $LASTEXITCODE) }");
        builder.AppendLine("        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json");
        builder.AppendLine("        if ($manifest.id -ne $package.Id -or $manifest.source -ne 'winget') { throw 'O manifesto retornado não corresponde ao pacote.' }");
        builder.AppendLine("        $installers = @($manifest.installers)");
        builder.AppendLine("        $preferredScope = [string]$package.InstallOptions.Scope");
        builder.AppendLine("        if ($preferredScope) { $scoped = @($installers | Where-Object { $_.scope -eq $preferredScope }); if ($scoped.Count -gt 0) { $installers = $scoped } }");
        builder.AppendLine("        $architecture = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITEW6432')");
        builder.AppendLine("        if (-not $architecture) { $architecture = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE') }");
        builder.AppendLine("        $architectureOrder = switch -Regex ($architecture) { '^ARM64$' { @('arm64', 'x64', 'x86'); break } '^AMD64$' { @('x64', 'x86'); break } '^ARM$' { @('arm', 'x86'); break } default { @('x86') } }");
        builder.AppendLine("        $preferredArchitecture = [string]$package.InstallOptions.Architecture");
        builder.AppendLine("        if ($preferredArchitecture) { $architectureOrder = @($preferredArchitecture) + @($architectureOrder | Where-Object { $_ -ne $preferredArchitecture }) }");
        builder.AppendLine("        $installer = $null");
        builder.AppendLine("        foreach ($architectureName in $architectureOrder) { $installer = $installers | Where-Object { $_.architecture -eq $architectureName } | Sort-Object { [int]($_.silentSupported -eq $true) } -Descending | Select-Object -First 1; if ($installer) { break } }");
        builder.AppendLine("        if (-not $installer) { $installer = $installers | Where-Object { -not $_.architecture } | Sort-Object { [int]($_.silentSupported -eq $true) } -Descending | Select-Object -First 1 }");
        builder.AppendLine("        if (-not $installer) { throw 'Não há instalador compatível com esta arquitetura.' }");
        builder.AppendLine("        $portable = $installer.type -eq 'portable' -or ($installer.type -eq 'zip' -and $installer.nestedType -eq 'portable')");
        builder.AppendLine("        if (-not $installer.silentSupported -and -not $portable) { throw 'O catálogo não declara instalação silenciosa para este instalador.' }");
        builder.AppendLine("        if ($installer.sha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'O catálogo não contém SHA-256 válido.' }");
        builder.AppendLine("        $installerUri = [Uri]$installer.url");
        builder.AppendLine("        if ($installerUri.Scheme -ne 'https') { throw 'A URL do instalador não usa HTTPS.' }");
        builder.AppendLine("        $installerPath = Join-Path $workingDirectory ('installer' + [IO.Path]::GetExtension($installerUri.AbsolutePath))");
        builder.AppendLine("        & $curlCommand.Source '--fail' '--location' '--silent' '--show-error' '--compressed' '--retry' '3' '--connect-timeout' '25' '--output' $installerPath $installerUri.AbsoluteUri");
        builder.AppendLine("        if ($LASTEXITCODE -ne 0) { throw ('Falha no download; curl.exe retornou {0}.' -f $LASTEXITCODE) }");
        builder.AppendLine("        if ((Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash -ine $installer.sha256) { throw 'O SHA-256 do instalador não confere.' }");
        builder.AppendLine("        if ($portable) {");
        builder.AppendLine("            $safeId = [Regex]::Replace([string]$package.Id, '[^A-Za-z0-9._-]', '_').Trim('.')");
        builder.AppendLine("            $portableRoot = Join-Path $env:LOCALAPPDATA 'WinProvisionStore\\PortablePackages'");
        builder.AppendLine("            [void][IO.Directory]::CreateDirectory($portableRoot)");
        builder.AppendLine("            $portableStage = Join-Path $portableRoot ('.' + $safeId + '-' + [Guid]::NewGuid().ToString('N'))");
        builder.AppendLine("            [void][IO.Directory]::CreateDirectory($portableStage)");
        builder.AppendLine("            if ($installer.type -eq 'zip') { Expand-Archive -LiteralPath $installerPath -DestinationPath $portableStage -Force; $portableRelative = ([string]$installer.nestedInstallerFile).Replace('/', '\\') } else { $portableRelative = ([Regex]::Replace([string]$installer.portableCommandAlias, '[^A-Za-z0-9._-]', '_') + [IO.Path]::GetExtension($installerPath)); if ($portableRelative -eq [IO.Path]::GetExtension($installerPath)) { $portableRelative = $safeId + $portableRelative }; Copy-Item -LiteralPath $installerPath -Destination (Join-Path $portableStage $portableRelative) -Force }");
        builder.AppendLine("            $portableExe = [IO.Path]::GetFullPath((Join-Path $portableStage $portableRelative))");
        builder.AppendLine("            if (-not $portableExe.StartsWith(([IO.Path]::GetFullPath($portableStage) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetExtension($portableExe) -ne '.exe' -or -not (Test-Path -LiteralPath $portableExe -PathType Leaf)) { throw 'Executável portátil inválido.' }");
        builder.AppendLine("            $portableDestination = Join-Path $portableRoot $safeId");
        builder.AppendLine("            if (Test-Path -LiteralPath $portableDestination) { Remove-Item -LiteralPath $portableDestination -Recurse -Force }");
        builder.AppendLine("            Move-Item -LiteralPath $portableStage -Destination $portableDestination");
        builder.AppendLine("            $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) (([IO.Path]::GetFileNameWithoutExtension($portableExe)) + '.lnk')))");
        builder.AppendLine("            $shortcut.TargetPath = Join-Path $portableDestination $portableRelative; $shortcut.WorkingDirectory = $portableDestination; $shortcut.Save()");
        builder.AppendLine("        } else {");
        builder.AppendLine("            $runPath = $installerPath; $runType = [string]$installer.type");
        builder.AppendLine("            if ($installer.type -eq 'zip' -and $installer.nestedInstallerFile) {");
        builder.AppendLine("                $extractPath = Join-Path $workingDirectory 'extract'; Expand-Archive -LiteralPath $installerPath -DestinationPath $extractPath -Force");
        builder.AppendLine("                $runPath = [IO.Path]::GetFullPath((Join-Path $extractPath ([string]$installer.nestedInstallerFile).Replace('/', '\\')))");
        builder.AppendLine("                if (-not $runPath.StartsWith(([IO.Path]::GetFullPath($extractPath) + [IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $runPath -PathType Leaf)) { throw 'Instalador aninhado inválido.' }");
        builder.AppendLine("                $runType = [string]$installer.nestedType");
        builder.AppendLine("            }");
        builder.AppendLine("            $arguments = [string]$installer.silentArgs; $fileName = $runPath");
        builder.AppendLine("            if ($runType -in @('msi', 'wixmsi') -or [IO.Path]::GetExtension($runPath) -eq '.msi') { $fileName = Join-Path $env:SystemRoot 'System32\\msiexec.exe'; $arguments = ('/i \"{0}\" {1}' -f $runPath, $arguments).Trim() }");
        builder.AppendLine("            $start = @{ FilePath = $fileName; ArgumentList = $arguments; Wait = $true; PassThru = $true; ErrorAction = 'Stop' }");
        builder.AppendLine("            if ($installer.scope -eq 'machine' -and -not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { $start.Verb = 'RunAs' }");
        builder.AppendLine("            $process = Start-Process @start");
        builder.AppendLine("            if (@(0, 3010, 1641) + @($installer.successCodes) -notcontains $process.ExitCode) { throw ('Instalador retornou código {0}.' -f $process.ExitCode) }");
        builder.AppendLine("        }");
        builder.AppendLine("        $apiInstalled = $true");
        builder.AppendLine("        Write-Information -MessageData ('Instalado pela API: {0}' -f $package.Name) -InformationAction Continue");
        builder.AppendLine("    } catch { $apiFailure = $_.Exception.Message }");
        builder.AppendLine("    if (Test-Path -LiteralPath $workingDirectory) { Remove-Item -LiteralPath $workingDirectory -Recurse -Force -ErrorAction SilentlyContinue }");
        builder.AppendLine("    if (-not $apiInstalled) {");
        builder.AppendLine("        $winget = Get-Command winget.exe -ErrorAction SilentlyContinue");
        builder.AppendLine("        if (-not $winget) { $failedPackages.Add([string]$package.Id); Write-Warning -Message ('{0}: API falhou ({1}) e winget.exe não está instalado.' -f $package.Id, $apiFailure); continue }");
        builder.AppendLine("        $wingetArgs = @('install', '--id', $package.Id, '--exact', '--source', $package.Source, '--accept-source-agreements', '--accept-package-agreements', '--force')");
        builder.AppendLine("        if ($package.InstallOptions.Interactive) { $wingetArgs += '--interactive' } else { $wingetArgs += @('--disable-interactivity', '--silent') }");
        builder.AppendLine("        if ($package.InstallOptions.Scope) { $wingetArgs += @('--scope', $package.InstallOptions.Scope) }");
        builder.AppendLine("        if ($package.InstallOptions.Architecture) { $wingetArgs += @('--architecture', $package.InstallOptions.Architecture) }");
        builder.AppendLine("        if ($package.InstallOptions.InstallLocation) { $wingetArgs += @('--location', $package.InstallOptions.InstallLocation) }");
        builder.AppendLine("        if ($package.InstallOptions.Version) { $wingetArgs += @('--version', [string]$package.InstallOptions.Version) }");
        builder.AppendLine("        & $winget.Source @wingetArgs; $wingetExitCode = $LASTEXITCODE");
        builder.AppendLine("        if ($wingetExitCode -ne 0) { $failedPackages.Add([string]$package.Id); Write-Warning -Message ('WinGet falhou em {0}, código {1}.' -f $package.Id, $wingetExitCode) }");
        builder.AppendLine("    }");
        builder.AppendLine("}");
        builder.AppendLine("if ($failedPackages.Count -gt 0) { throw ('Falha na instalação de {0} pacote(s): {1}' -f $failedPackages.Count, ($failedPackages -join ', ')) }");
        builder.AppendLine("Write-Information -MessageData 'Instalação concluída.' -InformationAction Continue");
        return builder.ToString();
    }
    private static Dictionary<string, object?> Entry(string Kind, string Id, string Name,
        string? Source = null, object? InstallOptions = null, string? OfficeConfigurationBase64 = null) => new()
        { ["Kind"] = Kind, ["Id"] = Id, ["Name"] = Name, ["Source"] = Source,
          ["InstallOptions"] = InstallOptions, ["OfficeConfigurationBase64"] = OfficeConfigurationBase64 };
    private static string ToPowerShellLiteral(string value) =>
        $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
    private static string ToPowerShellNullableLiteral(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "$null" : ToPowerShellLiteral(value);
    private static bool IsMsStoreSource(string? source) =>
        string.Equals(source?.Trim(), "msstore", StringComparison.OrdinalIgnoreCase)
        || string.Equals(source?.Trim(), "Microsoft Store", StringComparison.OrdinalIgnoreCase);
    private static string ToPowerShellComment(string value) =>
        string.Concat(value.Where(character => !char.IsControl(character))).Trim();
}
