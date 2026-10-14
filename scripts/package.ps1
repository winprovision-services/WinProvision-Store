param(
    [string]$AppVersion,
    [string]$FileVersion,
    [switch]$SkipPublish,
    [string]$PublishedDirectory,
    [switch]$RequireSignature
)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $taskRoot 'src\WinProvision.Store\WinProvision.Store.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath
$defaultVersion = $project.Project.PropertyGroup |
    ForEach-Object { $_.Version } |
    Where-Object { $_ } |
    Select-Object -First 1
if (-not $AppVersion) { $AppVersion = $defaultVersion }
if ($AppVersion -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$') {
    throw "Versão do aplicativo inválida: '$AppVersion'."
}

$baseVersion = ($AppVersion -split '-', 2)[0]
if (-not $FileVersion) { $FileVersion = "$baseVersion.0" }
if ($FileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "FileVersion inválida: '$FileVersion'."
}

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$compiler = Join-Path $programFilesX86 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) { $compiler = $command.Source }
    else { throw 'Inno Setup 6.3+ não encontrado. Instale-o para gerar o instalador WinUI.' }
}

$publishScript = Join-Path $PSScriptRoot 'publish.ps1'
if (-not $SkipPublish) {
    if ($PublishedDirectory) { throw 'PublishedDirectory exige SkipPublish para preservar os binários fornecidos.' }
    & $publishScript -AppVersion $AppVersion -FileVersion $FileVersion
    if ($LASTEXITCODE -ne 0) { throw 'A publicação do aplicativo falhou.' }
}

$publishDir = Join-Path $taskRoot 'artifacts\publish\win-x64'
if ($PublishedDirectory) { $publishDir = (Resolve-Path -LiteralPath $PublishedDirectory).Path }
if (Test-Path (Join-Path $publishDir 'validation-harness.flag')) { throw 'Não empacote uma publicação de testes.' }
$packageExe = Join-Path $publishDir 'WinProvision.Store.exe'
if (-not (Test-Path -LiteralPath $packageExe)) { throw 'Executável da publicação não encontrado.' }
foreach ($packageBinary in @($packageExe, (Join-Path $publishDir 'cli\winprovision.exe'))) {
    if (-not (Test-Path -LiteralPath $packageBinary)) { throw "Executável ausente: $packageBinary" }
    if ($RequireSignature -and (Get-AuthenticodeSignature -LiteralPath $packageBinary).Status -ne 'Valid') {
        throw "O executável precisa de assinatura Authenticode válida antes de montar o instalador final: $packageBinary"
    }
}
$outputDir = Join-Path $taskRoot 'artifacts\installer'
$installerScript = Join-Path $taskRoot 'installer\WinProvision.Store.iss'
$setupPath = Join-Path $outputDir 'WinProvision.Store-Setup.exe'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

& $compiler "/DAppVersion=$AppVersion" "/DFileVersion=$FileVersion" "/DSourceDir=$publishDir" "/DOutputDir=$outputDir" $installerScript
if ($LASTEXITCODE -ne 0) { throw "ISCC falhou com o código $LASTEXITCODE." }
if (-not (Test-Path -LiteralPath $setupPath)) { throw "O instalador não foi gerado: '$setupPath'." }

$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  WinProvision.Store-Setup.exe" |
    Set-Content -LiteralPath "$setupPath.sha256" -Encoding ascii
Write-Output "Instalador WinUI criado: $setupPath"
Write-Output "SHA-256: $hash"
