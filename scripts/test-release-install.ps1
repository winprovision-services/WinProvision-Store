param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$AppVersion,
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$ManifestPath
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne 'winprovision-services/WinProvision-Store') { throw 'Este teste de instalação exige um runner descartável do repositório oficial.' }
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{71B03A9D-2C64-4E81-BF6D-3A90257F1C49}_is1'
if (Test-Path -LiteralPath $taskKey) { throw 'O teste exige uma máquina sem instalação anterior.' }
$taskInstaller = (Resolve-Path -LiteralPath $InstallerPath).Path
$taskBeforePath = [Environment]::GetEnvironmentVariable('Path', 'User')
& winget settings --enable LocalManifestFiles
if ($LASTEXITCODE -ne 0) { throw 'Não foi possível habilitar manifestos locais no runner.' }
& winget install --manifest $ManifestPath --silent --accept-package-agreements --accept-source-agreements --disable-interactivity
if ($LASTEXITCODE -ne 0) { throw "A instalação pelo manifesto WinGet falhou: $LASTEXITCODE" }
$taskInstalled = Get-ItemProperty -LiteralPath $taskKey
if ($taskInstalled.DisplayName -ne 'WinProvision Store' -or $taskInstalled.Publisher -ne 'WinProvision Store' -or $taskInstalled.DisplayVersion -ne $AppVersion) { throw 'Os dados de desinstalação não correspondem ao manifesto.' }
$taskAppDirectory = [IO.Path]::GetFullPath($taskInstalled.InstallLocation).TrimEnd('\')
$taskExpectedDirectory = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'Programs/WinProvision Store')).TrimEnd('\')
if ($taskAppDirectory -ne $taskExpectedDirectory) { throw "Diretório de instalação inesperado: $taskAppDirectory" }
$taskCliDirectory = Join-Path $taskAppDirectory 'cli'
$taskUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (@($taskUserPath -split ';' | Where-Object { $_.TrimEnd('\') -eq $taskCliDirectory }).Count -ne 1) { throw 'A CLI não foi adicionada exatamente uma vez ao PATH do usuário.' }
foreach ($taskFile in @('WinProvision.Store.exe','cli/winprovision.exe')) {
    $taskFileInfo = (Get-Item -LiteralPath (Join-Path $taskAppDirectory $taskFile)).VersionInfo
    if ($taskFileInfo.ProductName.TrimEnd() -ne 'WinProvision Store' -or $taskFileInfo.ProductVersion.TrimEnd() -ne "$AppVersion.0") { throw "Metadados instalados divergentes: $taskFile" }
}
& (Join-Path $taskCliDirectory 'winprovision.exe') --help
if ($LASTEXITCODE -ne 0) { throw 'A CLI instalada não iniciou.' }
$taskProcess = Start-Process -FilePath $taskInstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /MERGETASKS="clipath"' -WindowStyle Hidden -PassThru -Wait
if ($taskProcess.ExitCode -notin @(0,3010)) { throw 'A reinstalação falhou.' }
$taskUserPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (@($taskUserPath -split ';' | Where-Object { $_.TrimEnd('\') -eq $taskCliDirectory }).Count -ne 1) { throw 'A reinstalação duplicou o PATH.' }
$taskUninstaller = Join-Path $taskAppDirectory 'unins000.exe'
$taskProcess = Start-Process -FilePath $taskUninstaller -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -WindowStyle Hidden -PassThru -Wait
if ($taskProcess.ExitCode -notin @(0,3010)) { throw 'A desinstalação silenciosa falhou.' }
if (Test-Path -LiteralPath $taskKey) { throw 'O registro da aplicação permaneceu após a desinstalação.' }
$taskAfterPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ([string]$taskAfterPath -ne [string]$taskBeforePath) { throw 'O PATH do usuário não foi restaurado após a desinstalação.' }
if (Test-Path -LiteralPath (Join-Path $taskAppDirectory 'WinProvision.Store.exe')) { throw 'O executável permaneceu após a desinstalação.' }
$taskReport = Join-Path $taskRoot 'artifacts/winget-install-test.json'
@{ version=$AppVersion; installerSha256=(Get-FileHash -LiteralPath $taskInstaller -Algorithm SHA256).Hash.ToLowerInvariant(); manifestInstall=$true; silentInstall=$true; metadata=$true; cliStarts=$true; pathAdded=$true; reinstallWithoutDuplicatePath=$true; silentUninstall=$true; pathRestored=$true } |
    ConvertTo-Json | Set-Content -LiteralPath $taskReport -Encoding utf8NoBOM
Write-Output 'Instalação, reinstalação, CLI, PATH e desinstalação conferidos.'
