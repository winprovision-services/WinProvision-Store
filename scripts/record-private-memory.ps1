param(
    [string]$OutputPath = (Join-Path (Get-Location) 'WinProvision-memory.csv'),
    [string]$ProcessName = 'WinProvision.Store'
)

$ErrorActionPreference = 'Stop'

if (-not ('WinProvision.Memory.ProcessMemoryReader' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinProvision.Memory {
    public static class ProcessMemoryReader {
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCountersEx2 {
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
            public UIntPtr SharedCommitUsage;
        }

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCountersEx2 counters, uint size);

        public static ulong GetPrivateWorkingSetSize(IntPtr process) {
            var counters = new ProcessMemoryCountersEx2();
            counters.Cb = (uint)Marshal.SizeOf<ProcessMemoryCountersEx2>();
            if (!GetProcessMemoryInfo(process, out counters, counters.Cb))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return counters.PrivateWorkingSetSize.ToUInt64();
        }
    }
}
'@
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$directory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
if (-not [System.IO.Directory]::Exists($directory)) {
    [System.IO.Directory]::CreateDirectory($directory) | Out-Null
}

'TimestampUtc,PID,ProcessName,PrivateWorkingSetBytes,PrivateWorkingSetMiB,WorkingSet64Bytes,WorkingSet64MiB,PrivateMemorySize64Bytes,PrivateMemorySize64MiB' |
    Set-Content -LiteralPath $resolvedOutput -Encoding utf8

Write-Host "Gravando processos '$ProcessName' em $resolvedOutput a cada 2 segundos. Ctrl+C encerra."
while ($true) {
    $loop = [System.Diagnostics.Stopwatch]::StartNew()
    $timestamp = [DateTimeOffset]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    foreach ($process in (Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Sort-Object Id)) {
        try {
            $process.Refresh()
            $privateWorkingSetBytes = [WinProvision.Memory.ProcessMemoryReader]::GetPrivateWorkingSetSize($process.Handle)
            $workingSetBytes = [long]$process.WorkingSet64
            $privateMemoryBytes = [long]$process.PrivateMemorySize64
            $row = [pscustomobject]@{
                TimestampUtc = $timestamp
                PID = $process.Id
                ProcessName = $process.ProcessName
                PrivateWorkingSetBytes = $privateWorkingSetBytes
                PrivateWorkingSetMiB = [math]::Round($privateWorkingSetBytes / 1MB, 2).ToString([Globalization.CultureInfo]::InvariantCulture)
                WorkingSet64Bytes = $workingSetBytes
                WorkingSet64MiB = [math]::Round($workingSetBytes / 1MB, 2).ToString([Globalization.CultureInfo]::InvariantCulture)
                PrivateMemorySize64Bytes = $privateMemoryBytes
                PrivateMemorySize64MiB = [math]::Round($privateMemoryBytes / 1MB, 2).ToString([Globalization.CultureInfo]::InvariantCulture)
            }
            $csvLine = $row | ConvertTo-Csv -NoTypeInformation | Select-Object -Last 1
            Add-Content -LiteralPath $resolvedOutput -Value $csvLine -Encoding utf8
        }
        catch [System.InvalidOperationException] {
            # The process may exit between enumeration and sampling; the next interval re-enumerates.
        }
        finally {
            $process.Dispose()
        }
    }
    $remainingMilliseconds = 2000 - [int]$loop.ElapsedMilliseconds
    if ($remainingMilliseconds -gt 0) {
        Start-Sleep -Milliseconds $remainingMilliseconds
    }
}
