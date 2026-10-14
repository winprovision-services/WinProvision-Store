param([switch]$IncludePublished)
$ErrorActionPreference = 'Stop'
$taskRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$taskPrefix = $taskRoot + [IO.Path]::DirectorySeparatorChar
$taskProtected = @(Get-ChildItem -LiteralPath $taskRoot -Directory -Recurse -Force |
    Where-Object { $_.Name -like 'brag*' -or $_.Name -eq 'brand-kit' } |
    Select-Object -ExpandProperty FullName)
$taskTargets = @(Get-ChildItem -LiteralPath (Join-Path $taskRoot 'artifacts') -Force |
    Where-Object { $_.Name -notlike 'brag*' -and $_.Name -ne 'brand-kit' -and ($IncludePublished -or $_.Name -ne 'publish') })
$taskTargets += Get-ChildItem -LiteralPath (Join-Path $taskRoot 'src'),(Join-Path $taskRoot 'tests') -Directory -Recurse -Force |
    Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' }
$taskIdeCache = Join-Path $taskRoot '.vs'
if (Test-Path -LiteralPath $taskIdeCache) { $taskTargets += Get-Item -LiteralPath $taskIdeCache -Force }
Add-Type -AssemblyName Microsoft.VisualBasic
$taskRemovedFiles = 0; $taskRemovedBytes = 0L
foreach ($taskTarget in ($taskTargets | Sort-Object FullName -Unique)) {
    if (-not (Test-Path -LiteralPath $taskTarget.FullName)) { continue }
    $taskPath = (Resolve-Path -LiteralPath $taskTarget.FullName).Path
    if (-not $taskPath.StartsWith($taskPrefix,[StringComparison]::OrdinalIgnoreCase) -or
        $taskTarget.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Destino não validado: $taskPath" }
    $taskTouchesProtected = $false
    foreach ($taskKeep in $taskProtected) {
        if ($taskPath.Equals($taskKeep,[StringComparison]::OrdinalIgnoreCase) -or
            $taskPath.StartsWith($taskKeep + '\',[StringComparison]::OrdinalIgnoreCase) -or
            $taskKeep.StartsWith($taskPath + '\',[StringComparison]::OrdinalIgnoreCase)) { $taskTouchesProtected = $true }
    }
    if ($taskTouchesProtected) { continue }
    if ($taskTarget.PSIsContainer) {
        $taskFiles = @(Get-ChildItem -LiteralPath $taskPath -File -Recurse -Force)
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($taskPath,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
    } else {
        $taskFiles = @($taskTarget)
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($taskPath,
            [Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin)
    }
    $taskRemovedFiles += $taskFiles.Count
    $taskRemovedBytes += ($taskFiles | Measure-Object Length -Sum).Sum
}
Write-Output "$taskRemovedFiles arquivos ($([Math]::Round($taskRemovedBytes/1MB,1)) MiB) enviados à Lixeira. Brand-kit e brag preservados."
