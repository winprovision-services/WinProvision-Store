param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$')][string]$AppVersion,
    [Parameter(Mandatory)][string]$InstallerPath,
    [switch]$Submit
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskPackageId = 'WinProvision.Store'
$taskRepository = 'https://github.com/winprovision-services/WinProvision-Store'
$taskInstallerUrl = "$taskRepository/releases/download/v$AppVersion/WinProvision.Store-Setup.exe"
$taskInstaller = (Resolve-Path -LiteralPath $InstallerPath).Path
$taskHash = (Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
$taskOutput = Join-Path $taskRoot "artifacts/winget/$taskPackageId/$AppVersion"
New-Item -ItemType Directory -Path $taskOutput -Force | Out-Null
$taskHeader = "PackageIdentifier: $taskPackageId`nPackageVersion: $AppVersion"
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json
$taskHeader
DefaultLocale: pt-BR
ManifestType: version
ManifestVersion: 1.12.0
"@ | Set-Content -LiteralPath (Join-Path $taskOutput "$taskPackageId.yaml") -Encoding utf8NoBOM
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json
$taskHeader
InstallerLocale: pt-BR
MinimumOSVersion: 10.0.17763.0
InstallerType: inno
Scope: machine
InstallerSwitches:
  Custom: /ALLUSERS
UpgradeBehavior: install
ProductCode: '{71B03A9D-2C64-4E81-BF6D-3A90257F1C49}_is1'
AppsAndFeaturesEntries:
- DisplayName: WinProvision Store
  Publisher: WinProvision Store
  DisplayVersion: $AppVersion
  ProductCode: '{71B03A9D-2C64-4E81-BF6D-3A90257F1C49}_is1'
Installers:
- Architecture: x64
  InstallerUrl: $taskInstallerUrl
  InstallerSha256: $taskHash
ManifestType: installer
ManifestVersion: 1.12.0
"@ | Set-Content -LiteralPath (Join-Path $taskOutput "$taskPackageId.installer.yaml") -Encoding utf8NoBOM
@"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json
$taskHeader
PackageLocale: pt-BR
Publisher: WinProvision Store
PublisherUrl: $taskRepository
PublisherSupportUrl: $taskRepository/issues
PrivacyUrl: $taskRepository/blob/main/PRIVACY.md
PackageName: WinProvision Store
PackageUrl: $taskRepository
License: MIT
LicenseUrl: $taskRepository/blob/main/LICENSE
ShortDescription: Gerenciador de aplicativos para Windows com interface gráfica e linha de comando.
ReleaseNotesUrl: $taskRepository/releases/tag/v$AppVersion
ManifestType: defaultLocale
ManifestVersion: 1.12.0
"@ | Set-Content -LiteralPath (Join-Path $taskOutput "$taskPackageId.locale.pt-BR.yaml") -Encoding utf8NoBOM
if ($env:GITHUB_ACTIONS -eq 'true') { & (Join-Path $PSScriptRoot 'prepare-winget-validation.ps1') }
& winget validate --manifest $taskOutput --disable-interactivity
if ($LASTEXITCODE -ne 0) { throw 'O WinGet rejeitou os manifestos gerados.' }
if (-not $Submit) { Write-Output "Manifestos preparados: $taskOutput"; return }
if ($AppVersion.Contains('-')) { throw 'Envie ao WinGet somente versões estáveis.' }
if ($env:GITHUB_REPOSITORY -ne 'winprovision-services/WinProvision-Store' -or $env:GITHUB_REF -ne 'refs/heads/main') { throw 'Envie somente pela main do repositório oficial.' }
if (-not $env:WINGET_CREATE_GITHUB_TOKEN) { throw 'O secret WINGET_CREATE_GITHUB_TOKEN não foi configurado.' }
$taskDownload = Join-Path $env:RUNNER_TEMP ('winget-installer-' + [Guid]::NewGuid().ToString('N') + '.exe')
Invoke-WebRequest -Uri $taskInstallerUrl -OutFile $taskDownload
if ((Get-FileHash -LiteralPath $taskDownload -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskHash) { throw 'O instalador público diverge do instalador validado.' }
$taskCreator = Join-Path $env:RUNNER_TEMP 'wingetcreate-1.12.13.0.exe'
Invoke-WebRequest -Uri 'https://github.com/microsoft/winget-create/releases/download/v1.12.13.0/wingetcreate.exe' -OutFile $taskCreator
if ((Get-FileHash -LiteralPath $taskCreator -Algorithm SHA256).Hash.ToLowerInvariant() -ne '24042bd37915805615e6cf969ac57c6439124c3fe85823327f5f3fb24bd9ffea') { throw 'Hash do WinGet Create inválido.' }
& $taskCreator submit $taskOutput --prtitle "New version: $taskPackageId version $AppVersion" --no-open
if ($LASTEXITCODE -ne 0) { throw 'O envio com WinGet Create falhou. A release do GitHub continua disponível.' }
