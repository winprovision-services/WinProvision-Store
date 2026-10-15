param([string]$OutputDirectory = 'artifacts/site')
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskOutput = [IO.Path]::GetFullPath((Join-Path $taskRoot $OutputDirectory))
$taskArtifacts = [IO.Path]::GetFullPath((Join-Path $taskRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $taskOutput.StartsWith($taskArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'A saída do site deve estar dentro de artifacts.'
}
if (Test-Path -LiteralPath $taskOutput) { Remove-Item -LiteralPath $taskOutput -Recurse -Force }
New-Item -ItemType Directory -Path "$taskOutput/assets" -Force | Out-Null
Copy-Item -Path "$taskRoot/docs/site/*" -Destination $taskOutput -Recurse
Copy-Item -LiteralPath "$taskRoot/docs/icons" -Destination "$taskOutput/assets" -Recurse
Copy-Item -LiteralPath "$taskRoot/docs/screenshots" -Destination "$taskOutput/assets" -Recurse
Copy-Item -LiteralPath "$taskRoot/docs/visual/demonstração.mp4" -Destination "$taskOutput/assets/demo.mp4"
[IO.File]::WriteAllText("$taskOutput/.nojekyll", '')
foreach ($taskFile in Get-ChildItem -LiteralPath $taskOutput -File | Where-Object Extension -in '.html','.css','.js') {
    $taskText = [IO.File]::ReadAllText($taskFile.FullName)
    foreach ($taskMatch in [regex]::Matches($taskText, '(?:src|href)="(?<path>assets/[^"#]+)"')) {
        if (-not (Test-Path -LiteralPath (Join-Path $taskOutput $taskMatch.Groups['path'].Value))) {
            throw "Recurso do site ausente: $($taskMatch.Groups['path'].Value)"
        }
    }
}
Write-Output "Site preparado em $taskOutput"
