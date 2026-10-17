param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug', [switch]$Validation)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskProject = Join-Path $taskRoot 'src\WinProvision.Store\WinProvision.Store.csproj'
dotnet build $taskProject -c $Configuration -p:Platform=x64 "-p:EnableValidationHarness=$($Validation.ToString().ToLowerInvariant())"
if ($LASTEXITCODE -ne 0) { throw 'A compilação falhou.' }
dotnet build (Join-Path $taskRoot 'src\WinProvision.Cli\WinProvision.Cli.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'A compilação da CLI falhou.' }
