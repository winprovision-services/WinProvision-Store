param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z]+(\.[0-9A-Za-z]+)*)?$')][string]$AppVersion)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskCandidates = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'artifacts/release-candidate') -Directory)
if ($taskCandidates.Count -ne 1) { throw 'A release exige exatamente um candidato nesta execução.' }
$taskCandidate = $taskCandidates[0].FullName
$taskMetadata = Get-Content -LiteralPath (Join-Path $taskCandidate 'candidate.json') -Raw | ConvertFrom-Json
$taskCommit = (& git -C $taskRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $taskMetadata.version -ne $AppVersion -or $taskMetadata.sourceCommit -ne $taskCommit) { throw 'A origem do candidato diverge da release.' }
$taskOutput = Join-Path $taskRoot 'artifacts/release'
New-Item -ItemType Directory -Path $taskOutput -ErrorAction Stop | Out-Null
$taskNames = @('WinProvision.Store-Setup.exe','WinProvision.Store-Setup.exe.sha256','WinProvision.Store-win-x64.zip')
foreach ($taskName in $taskNames) {
    $taskSource = Join-Path $taskCandidate $taskName
    $taskExpected = @($taskMetadata.files | Where-Object name -eq $taskName)
    if ($taskExpected.Count -ne 1 -or (Get-FileHash -LiteralPath $taskSource -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskExpected[0].sha256) { throw "Hash do candidato divergente: $taskName" }
    Copy-Item -LiteralPath $taskSource -Destination $taskOutput
}
$taskFiles = @($taskNames | ForEach-Object {
    $taskFile = Get-Item -LiteralPath (Join-Path $taskOutput $_)
    @{ name = $_; bytes = $taskFile.Length; sha256 = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
@{ schema = 1; product = 'WinProvision Store'; version = $AppVersion; sourceCommit = $taskCommit; files = $taskFiles } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $taskOutput 'release.json') -Encoding utf8NoBOM
@($taskNames + 'release.json' | ForEach-Object {
    $taskHash = (Get-FileHash -LiteralPath (Join-Path $taskOutput $_) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$taskHash  $_"
}) | Set-Content -LiteralPath (Join-Path $taskOutput 'SHA256SUMS') -Encoding ascii
Write-Output "Arquivos preparados: $taskOutput"
