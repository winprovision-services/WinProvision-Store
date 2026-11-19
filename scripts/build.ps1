param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('win-x64', 'win-arm64')][string]$RuntimeIdentifier = 'win-x64',
    [switch]$Validation
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src\WinProvision.Store\WinProvision.Store.csproj'
$taskPlatform = if ($RuntimeIdentifier -eq 'win-arm64') { 'ARM64' } else { 'x64' }
dotnet build $taskProject -c $Configuration "-p:Platform=$taskPlatform" "-p:RuntimeIdentifier=$RuntimeIdentifier" "-p:EnableValidationHarness=$($Validation.ToString().ToLowerInvariant())"
if ($LASTEXITCODE -ne 0) { throw 'A compilação falhou.' }
$taskGuiOutput = Join-Path $taskRoot "src/WinProvision.Store/bin/$taskPlatform/$Configuration/net10.0-windows10.0.26100.0/$RuntimeIdentifier"
if ($Validation) { $taskGuiOutput = Join-Path $taskRoot "artifacts/validation/bin/$taskPlatform/$Configuration/net10.0-windows10.0.26100.0/$RuntimeIdentifier" }
$taskWorkerOutput = Join-Path $taskGuiOutput 'cli'
dotnet publish (Join-Path $taskRoot 'src\WinProvision.Cli\WinProvision.Cli.csproj') -c $Configuration -r $RuntimeIdentifier --self-contained true `
    -p:SelfContained=true `
    -p:PublishAot=false -p:PublishSingleFile=false -o (Join-Path $taskGuiOutput 'cli')
if ($LASTEXITCODE -ne 0) { throw 'A publicação self-contained do worker falhou.' }
$taskWorkerFiles = @('winprovision.exe', 'Microsoft.Management.Deployment.winmd',
    'winprovision.runtimeconfig.json', 'winprovision.deps.json', 'hostfxr.dll',
    'hostpolicy.dll', 'coreclr.dll', 'Tool/winget-com/WindowsPackageManager.dll',
    'Tool/winget-com/Microsoft.Management.Deployment.InProc.dll')
foreach ($taskWorkerFile in $taskWorkerFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskWorkerOutput $taskWorkerFile))) {
        throw "Host COM elevado incompleto: $taskWorkerFile"
    }
}
$taskWorkerPath = Join-Path $taskWorkerOutput 'winprovision.exe'
if (($RuntimeIdentifier -eq 'win-x64' -and [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'X64') -or
    ($RuntimeIdentifier -eq 'win-arm64' -and [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64')) {
    $taskSelfTestOutput = & $taskWorkerPath --selftest 2>&1
    if ($LASTEXITCODE -ne 0 -or ($taskSelfTestOutput -join "`n") -notmatch 'WINPROVISION_SELFTEST_OK') {
        throw "O worker self-contained falhou no --selftest (código $LASTEXITCODE): $($taskSelfTestOutput -join ' ')"
    }
} else {
    Write-Warning "Cross-build de ${RuntimeIdentifier}: --selftest será executado no Windows de destino."
}
