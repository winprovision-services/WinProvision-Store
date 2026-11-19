param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -ErrorAction Stop
$taskManifestPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'Tool\gsudo\manifest.json'
$taskManifest = Get-Content -LiteralPath $taskManifestPath -Raw | ConvertFrom-Json
$taskExpectedHash = ([string]$taskManifest.sha256).ToLowerInvariant()
$taskPath = [IO.Path]::GetFullPath($ExecutablePath)

if (-not (Test-Path -LiteralPath $taskPath -PathType Leaf)) {
    throw "WinProvision Elevator ausente: $taskPath"
}

$taskActualHash = (Get-FileHash -LiteralPath $taskPath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($taskActualHash -ne $taskExpectedHash) {
    throw "WinProvision Elevator divergente do manifesto; a marca de origem não será removida: $taskPath"
}

# O helper é deliberadamente distribuído sem Authenticode. Só removemos o MOTW
# do binário exato já fixado pelo hash revisado do manifesto do projeto.
Unblock-File -LiteralPath $taskPath
