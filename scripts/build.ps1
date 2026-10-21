param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug', [switch]$Validation)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src\WinProvision.Store\WinProvision.Store.csproj'
dotnet build $taskProject -c $Configuration -p:Platform=x64 "-p:EnableValidationHarness=$($Validation.ToString().ToLowerInvariant())"
if ($LASTEXITCODE -ne 0) { throw 'A compilação falhou.' }
$taskGuiOutput = Join-Path $taskRoot "src/WinProvision.Store/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64"
if ($Validation) { $taskGuiOutput = Join-Path $taskRoot "artifacts/validation/bin/x64/$Configuration/net10.0-windows10.0.26100.0/win-x64" }
if ($Configuration -eq 'Release') {
    dotnet build (Join-Path $taskRoot 'src\WinProvision.Cli\WinProvision.Cli.csproj') -c $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'A compilação da CLI falhou.' }
    $taskCliOutput = Join-Path $taskRoot "src/WinProvision.Cli/bin/$Configuration/net10.0-windows10.0.26100.0"
    New-Item -ItemType Directory -Path (Join-Path $taskGuiOutput 'cli') -Force | Out-Null
    Copy-Item -Path (Join-Path $taskCliOutput '*') -Destination (Join-Path $taskGuiOutput 'cli') -Recurse -Force
}
foreach ($taskWorkerFile in @('cli/winprovision.exe', 'cli/Microsoft.Management.Deployment.winmd',
        'cli/Tool/winget-com/WindowsPackageManager.dll', 'cli/Tool/winget-com/Microsoft.Management.Deployment.InProc.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskGuiOutput $taskWorkerFile))) {
        throw "Host COM elevado incompleto: $taskWorkerFile"
    }
}
