param(
    [string]$AppVersion,
    [string]$FileVersion,
    [ValidatePattern('^([a-fA-F0-9]{40}|[a-fA-F0-9]{64})$')][string]$SourceRevisionId,
    [switch]$Jit,
    [switch]$Validation
)

$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src\WinProvision.Store\WinProvision.Store.csproj'
$taskOutput = Join-Path $taskRoot 'artifacts\publish\win-x64'
$taskPublishRoot = Join-Path $taskRoot 'artifacts\publish'
if ($Validation) {
    $taskPublishRoot = Join-Path $taskRoot 'artifacts\validation\publish'
    $taskOutput = Join-Path $taskPublishRoot 'win-x64'
}
$taskStaging = Join-Path $taskPublishRoot ("staging-" + [Guid]::NewGuid().ToString('N'))
$taskAot = (-not $Jit).ToString().ToLowerInvariant()

[xml]$project = Get-Content -LiteralPath $taskProject
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

$taskPublishMetadata = @()
if ($SourceRevisionId) { $taskPublishMetadata += "-p:SourceRevisionId=$SourceRevisionId" }

dotnet publish $taskProject -c Release -r win-x64 -p:Platform=x64 --self-contained true `
    "-p:PublishAot=$taskAot" "-p:EnableValidationHarness=$($Validation.ToString().ToLowerInvariant())" `
    "-p:Version=$AppVersion" "-p:AssemblyVersion=$FileVersion" "-p:FileVersion=$FileVersion" "-p:InformationalVersion=$FileVersion" "-p:IncludeSourceRevisionInInformationalVersion=false" `
    -o $taskStaging @taskPublishMetadata
if ($LASTEXITCODE -ne 0) { throw 'A publicação falhou.' }
dotnet publish (Join-Path $taskRoot 'src\WinProvision.Cli\WinProvision.Cli.csproj') -c Release -r win-x64 --self-contained true `
    "-p:PublishAot=$taskAot" "-p:Version=$AppVersion" "-p:AssemblyVersion=$FileVersion" "-p:FileVersion=$FileVersion" `
    "-p:InformationalVersion=$FileVersion" -p:IncludeSourceRevisionInInformationalVersion=false `
    -p:DebugType=none -o (Join-Path $taskStaging 'cli') @taskPublishMetadata
if ($LASTEXITCODE -ne 0) { throw 'A publicação da CLI falhou.' }
if (-not (Test-Path -LiteralPath (Join-Path $taskStaging 'cli\winprovision.exe'))) { throw 'Executável da CLI ausente.' }
if (-not (Test-Path -LiteralPath (Join-Path $taskStaging 'cli\Microsoft.Management.Deployment.winmd'))) { throw 'Metadados COM do WinGet ausentes na CLI.' }
$taskElevator = Get-Content (Join-Path $taskRoot 'Tool/gsudo/manifest.json') -Raw | ConvertFrom-Json
foreach ($taskHostDirectory in @($taskStaging, (Join-Path $taskStaging 'cli'))) {
    foreach ($taskNativeDll in @(
        @{ Name = 'WindowsPackageManager.dll'; Sha256 = '0fb0f8eee1214ae3c17abe740ea32964a7da8b01f829ffa66dec241b9fb3b384' },
        @{ Name = 'Microsoft.Management.Deployment.InProc.dll'; Sha256 = '5ab84530dc02019e06c6e482c61ceca3eea87c90cb0962b8a42a0e4c55b08fa3' }
    )) {
        $taskNativePath = Join-Path $taskHostDirectory ("Tool/winget-com/" + $taskNativeDll.Name)
        if (-not (Test-Path -LiteralPath $taskNativePath) -or
            (Get-FileHash -LiteralPath $taskNativePath).Hash.ToLowerInvariant() -ne $taskNativeDll.Sha256) {
            throw 'SDK COM no processo ausente ou divergente na publicação.'
        }
    }
    $taskHelperPath = Join-Path $taskHostDirectory 'Tool/gsudo/WinProvision Elevator.exe'
    if (-not (Test-Path -LiteralPath $taskHelperPath) -or
        (Get-FileHash -LiteralPath $taskHelperPath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskElevator.sha256 -or
        -not (Test-Path -LiteralPath (Join-Path $taskHostDirectory 'Tool/gsudo/LICENSE.txt'))) {
        throw 'Helper de elevação ausente, divergente ou sem a licença MIT.'
    }
}
foreach ($taskResource in @('WinProvision.Store.pri', 'App.xbf', 'MainWindow.xbf', 'Views\HomePage.xbf', 'Views\CollectionsPage.xbf', 'Views\UpdatesPage.xbf', 'Views\InstalledPackagesPage.xbf', 'Styles\InstalledPackages.xbf', 'Views\OfficePage.xbf', 'Views\AccountSyncPage.xbf', 'Views\SettingsPage.xbf', 'Views\MorePage.xbf', 'Controls\OperationsQueuePanel.xbf', 'Styles\OperationsQueue.xbf', 'Controls\AppDetailsOverlay.xbf', 'Styles\AppDetails.xbf', 'Styles\Updates.xbf', 'Styles\Office.xbf')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskStaging $taskResource))) { throw "Recurso WinUI ausente na publicação: $taskResource" }
}
if (-not $Jit) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskStaging 'Microsoft.Management.Deployment.winmd'))) { throw 'Metadados COM do WinGet ausentes na publicação.' }
    foreach ($taskManagedFile in @('coreclr.dll', 'WinProvision.Store.dll', 'WinProvision.Store.runtimeconfig.json')) {
        if (Test-Path -LiteralPath (Join-Path $taskStaging $taskManagedFile)) { throw "A publicação AOT contém um componente gerenciado inesperado: $taskManagedFile" }
    }
}
# Swap only validated output; a fresh diretório temporário prevents stale JIT files in AOT releases.
$taskAbsoluteOutput = [IO.Path]::GetFullPath($taskOutput)
$taskAbsoluteStaging = [IO.Path]::GetFullPath($taskStaging)
$taskAllowedRoot = [IO.Path]::GetFullPath($taskPublishRoot) + [IO.Path]::DirectorySeparatorChar
if (-not $taskAbsoluteOutput.StartsWith($taskAllowedRoot, [StringComparison]::OrdinalIgnoreCase) -or
    -not $taskAbsoluteStaging.StartsWith($taskAllowedRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Diretório de publicação fora de artifacts\publish.' }
if (Test-Path -LiteralPath $taskOutput) {
    Add-Type -AssemblyName Microsoft.VisualBasic
    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($taskOutput,
        [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
}
Move-Item -LiteralPath $taskStaging -Destination $taskOutput
Write-Output "Aplicativo publicado em $taskOutput"
