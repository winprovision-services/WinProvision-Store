param(
    [ValidateRange(1, 100)][int]$Runs = 7,
    [ValidatePattern('^[A-Za-z0-9_-]+$')]
    [string]$Label = 'baseline',
    [string]$GcConcurrent = 'Default',
    [string]$GcConserveMemory = 'Default',
    [string]$Executable = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/publish/win-x64/WinProvision.Store.exe'),
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts/memory-milestones/Stage3/repeated')
)

$ErrorActionPreference = 'Stop'
if ($GcConcurrent -notin @('Default', '0', '1')) { throw 'GcConcurrent aceita Default, 0 ou 1.' }
if ($GcConserveMemory -notin @('Default', '3', '7')) { throw 'GcConserveMemory aceita Default, 3 ou 7.' }
$Executable = (Resolve-Path -LiteralPath $Executable).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

if (-not ('WinProvisionMemoryProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class WinProvisionMemoryProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCountersEx2
    {
        public uint Cb;
        public uint PageFaultCount;
        public UIntPtr PeakWorkingSetSize;
        public UIntPtr WorkingSetSize;
        public UIntPtr QuotaPeakPagedPoolUsage;
        public UIntPtr QuotaPagedPoolUsage;
        public UIntPtr QuotaPeakNonPagedPoolUsage;
        public UIntPtr QuotaNonPagedPoolUsage;
        public UIntPtr PagefileUsage;
        public UIntPtr PeakPagefileUsage;
        public UIntPtr PrivateUsage;
        public UIntPtr PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    [DllImport("psapi.dll", EntryPoint = "GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCountersEx2 counters, uint size);

    public static ulong GetPrivateWorkingSet(IntPtr process)
    {
        var counters = new ProcessMemoryCountersEx2 { Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>() };
        if (!GetProcessMemoryInfo(process, ref counters, counters.Cb))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return counters.PrivateWorkingSetSize.ToUInt64();
    }
}
'@
}

$environmentNames = @('WINPROVISION_MEMLOG', 'DOTNET_gcConcurrent', 'DOTNET_GCConserveMemory')
$originalEnvironment = @{}
foreach ($name in $environmentNames) { $originalEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$trialRows = [System.Collections.Generic.List[object]]::new()

try {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $null, 'Process') }
    if ($GcConcurrent -ne 'Default') { $env:DOTNET_gcConcurrent = $GcConcurrent }
    if ($GcConserveMemory -ne 'Default') { $env:DOTNET_GCConserveMemory = $GcConserveMemory }

    foreach ($run in 1..$Runs) {
        foreach ($mode in '1', '2') {
            if (Get-Process -Name 'WinProvision.Store' -ErrorAction SilentlyContinue) {
                throw 'Já existe WinProvision.Store em execução; feche-o antes de iniciar a série.'
            }

            $env:WINPROVISION_MEMLOG = $mode
            $process = Start-Process -FilePath $Executable -PassThru
            $logPath = Join-Path $env:TEMP "WinProvisionStore-memory-$($process.Id)-mode$mode.csv"
            $deadline = [DateTime]::UtcNow.AddMinutes(2)
            $lastMilestone = $null
            do {
                if ($process.HasExited) { throw "Store encerrou antes do marco final; PID $($process.Id)." }
                if (Test-Path -LiteralPath $logPath) {
                    $rows = Import-Csv -LiteralPath $logPath
                    $lastMilestone = $rows | Where-Object Milestone -eq 'installed_inventory_loaded' | Select-Object -Last 1
                }
                if ($lastMilestone) { break }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $deadline)

            if (-not $lastMilestone) {
                Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                throw "Marco installed_inventory_loaded não ocorreu em 2 minutos; PID $($process.Id)."
            }

            Start-Sleep -Seconds 10
            $process.Refresh()
            $privateWorkingSet = [WinProvisionMemoryProbe]::GetPrivateWorkingSet($process.Handle)
            $trialRows.Add([pscustomobject]@{
                Label = $Label
                Run = $run
                Mode = $mode
                ProcessId = $process.Id
                LastMilestoneUtc = $lastMilestone.TimestampUtc
                LastMilestonePwsBytes = [UInt64]$lastMilestone.PrivateWorkingSetBytes
                PwsAfter10SecondsBytes = $privateWorkingSet
                GcTotalMemoryFalseBytes = [UInt64]$lastMilestone.GcTotalMemoryFalseBytes
                GcHeapSizeBytes = [UInt64]$lastMilestone.GcHeapSizeBytes
                GcConcurrent = $GcConcurrent
                GcConserveMemory = $GcConserveMemory
            })

            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $process.Id -ErrorAction SilentlyContinue
            Start-Sleep -Milliseconds 500
        }
    }

    $rawPath = Join-Path $OutputDirectory "$Label-runs.csv"
    $summaryPath = Join-Path $OutputDirectory "$Label-summary.csv"
    $trialRows | Export-Csv -LiteralPath $rawPath -NoTypeInformation -Encoding utf8
    $summary = foreach ($mode in '1', '2') {
        $values = @($trialRows | Where-Object Mode -eq $mode | ForEach-Object { [double]$_.PwsAfter10SecondsBytes / 1MB } | Sort-Object)
        $middle = [int][Math]::Floor($values.Count / 2)
        $median = if ($values.Count % 2) { $values[$middle] } else { ($values[$middle - 1] + $values[$middle]) / 2 }
        [pscustomobject]@{
            Label = $Label
            Mode = $mode
            N = $values.Count
            MedianPwsMiB = [Math]::Round($median, 3)
            MinPwsMiB = [Math]::Round($values[0], 3)
            MaxPwsMiB = [Math]::Round($values[-1], 3)
            NoiseRangeMiB = [Math]::Round($values[-1] - $values[0], 3)
            GcConcurrent = $GcConcurrent
            GcConserveMemory = $GcConserveMemory
        }
    }
    $summary | Export-Csv -LiteralPath $summaryPath -NoTypeInformation -Encoding utf8
    $summary | Format-Table -AutoSize
    Write-Output "Runs: $rawPath"
    Write-Output "Summary: $summaryPath"
}
finally {
    foreach ($name in $environmentNames) {
        [Environment]::SetEnvironmentVariable($name, $originalEnvironment[$name], 'Process')
    }
}
