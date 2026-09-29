[CmdletBinding()]param(
    [ValidateRange(10,7200)][int]$Seconds = 120,
    [string]$Output = '.artifacts/validation/monitor-stress.json'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$worker = Join-Path $root 'dist/WindowsOpenIntelligence/Woi.Worker.exe'
if (!(Test-Path -LiteralPath $worker)) { throw 'Publish the self-contained application first.' }
$start = [Diagnostics.ProcessStartInfo]::new($worker)
$start.UseShellExecute = $false
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.CreateNoWindow = $true
$start.ArgumentList.Add('--monitor')
$start.ArgumentList.Add('--parent')
$start.ArgumentList.Add([string]$PID)
$start.ArgumentList.Add('--parent-start')
$start.ArgumentList.Add([string]([Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks))
$child = [Diagnostics.Process]::Start($start)
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    Start-Sleep -Seconds $Seconds
    if ($child.HasExited) { throw "Monitor exited early: $($child.StandardError.ReadToEnd())" }
    $child.StandardInput.WriteLine('{"id":"status","version":1,"method":"monitor.status","parameters":{}}')
    $line = $child.StandardOutput.ReadLine()
    if (!$line) { throw "Monitor gave no status: $($child.StandardError.ReadToEnd())" }
    $view = ($line | ConvertFrom-Json).result
    $cpuSeconds = $child.TotalProcessorTime.TotalSeconds
    $peakBytes = $child.PeakWorkingSet64
    $child.StandardInput.WriteLine('{"id":"stop","version":1,"method":"monitor.stop","parameters":{}}')
    $stop = $child.StandardOutput.ReadLine() | ConvertFrom-Json
    if (!$child.WaitForExit(5000) -or !$stop.result.stopped -or $child.ExitCode -ne 0) {
        throw 'Monitor failed to stop cleanly.'
    }
    $result = [pscustomobject]@{
        durationSeconds = [math]::Round($clock.Elapsed.TotalSeconds,3)
        snapshotCount = $view.snapshotCount
        retainedSeconds = $view.retainedSeconds
        retainedProcessRows = $view.retainedProcessRows
        estimatedRetainedBytes = $view.estimatedRetainedBytes
        peakWorkerMiB = [math]::Round($peakBytes / 1MB,2)
        workerCpuPercentOfMachine = [math]::Round($cpuSeconds / $clock.Elapsed.TotalSeconds / [Environment]::ProcessorCount * 100,3)
        latestUnreadableProcesses = $view.history[-1].unreadableProcesses
        gapKinds = @($view.recentGaps | Group-Object kind | ForEach-Object { [pscustomobject]@{kind=$_.Name;count=$_.Count} })
    }
    $destination = [IO.Path]::GetFullPath((Join-Path $root $Output))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $destination -Encoding utf8
    $result | ConvertTo-Json -Depth 5
    if ($view.snapshotCount -lt [math]::Floor($Seconds * .8)) { throw 'Too many sampling ticks were missed.' }
} finally {
    if (!$child.HasExited) { $child.Kill($true); $child.WaitForExit(5000) | Out-Null }
    $child.Dispose()
}
