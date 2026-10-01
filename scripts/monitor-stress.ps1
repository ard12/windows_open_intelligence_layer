[CmdletBinding()]param(
    [ValidateRange(10,7200)][int]$Seconds = 120,
    [ValidateRange(0,120)][int]$WarmupSeconds = 0,
    [string]$Output = '.artifacts/validation/monitor-stress.json'
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$worker = Join-Path $root 'dist/WindowsOpenIntelligence/Woi.Worker.exe'
if (!(Test-Path -LiteralPath $worker)) { throw 'Publish the self-contained application first.' }
$destination = [IO.Path]::GetFullPath((Join-Path $root $Output))
if (!$destination.StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Monitor report must stay in the workspace.'
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
if (Test-Path -LiteralPath $destination) {
    $previous = $destination + '.previous-' + [Guid]::NewGuid().ToString('N')
    if (![IO.Path]::GetFullPath($previous).StartsWith($root + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Previous report path escaped the workspace.'
    }
    Move-Item -LiteralPath $destination -Destination $previous
}
function RequestMonitor($process, [string]$method) {
    $requestId = [Guid]::NewGuid().ToString('N')
    $process.StandardInput.WriteLine((@{id=$requestId;version=1;method=$method;parameters=@{}} | ConvertTo-Json -Compress))
    $process.StandardInput.Flush()
    $task = $process.StandardOutput.ReadLineAsync()
    if (!$task.Wait(10000)) { throw "Monitor request timed out: $method" }
    if (!$task.Result) { throw "Monitor closed its response stream: $method" }
    $reply = $task.Result | ConvertFrom-Json
    if ($reply.id -ne $requestId -or $reply.version -ne 1 -or $reply.kind -ne 'result') {
        throw "Monitor request failed: $method ($($reply.code))"
    }
    return $reply.result
}
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
$stderr = $child.StandardError.ReadToEndAsync()
$clock = [Diagnostics.Stopwatch]::StartNew()
try {
    $baselineCpuSeconds = 0.0
    $baselineSnapshots = 0L
    $baselineMissedGaps = 0L
    $warmupElapsed = 0.0
    if ($WarmupSeconds -gt 0) {
        Start-Sleep -Seconds $WarmupSeconds
        if ($child.HasExited) { throw 'Monitor exited during warm-up.' }
        if ($clock.Elapsed.TotalSeconds -gt $WarmupSeconds + 10) {
            throw 'Warm-up elapsed-time budget exceeded; possible suspend/resume or interrupted test.'
        }
        $baseline = RequestMonitor $child 'monitor.status'
        $child.Refresh()
        $baselineCpuSeconds = $child.TotalProcessorTime.TotalSeconds
        $baselineSnapshots = $baseline.statistics.totalSnapshots
        $baselineMissedGaps = $baseline.statistics.missedTickGaps
        $warmupElapsed = $clock.Elapsed.TotalSeconds
    }
    $measurement = if ($WarmupSeconds -gt 0) { [Diagnostics.Stopwatch]::StartNew() } else { $clock }
    Start-Sleep -Seconds $Seconds
    if ($child.HasExited) { throw 'Monitor exited during measurement.' }
    if ($measurement.Elapsed.TotalSeconds -gt $Seconds + 10) {
        throw 'Measurement elapsed-time budget exceeded; possible suspend/resume or interrupted test.'
    }
    $view = RequestMonitor $child 'monitor.status'
    $child.Refresh()
    $cpuSeconds = $child.TotalProcessorTime.TotalSeconds
    $peakBytes = $child.PeakWorkingSet64
    $measurementSeconds = $measurement.Elapsed.TotalSeconds
    $totalSeconds = $clock.Elapsed.TotalSeconds
    $cpuPercent = ($cpuSeconds - $baselineCpuSeconds) / $measurementSeconds / [Environment]::ProcessorCount * 100
    $stop = RequestMonitor $child 'monitor.stop'
    if (!$child.WaitForExit(5000) -or !$stop.stopped -or $child.ExitCode -ne 0) { throw 'Monitor failed to stop cleanly.' }
    $result = [pscustomobject]@{
        schema = 2; success = $true
        requestedMeasurementSeconds = $Seconds; warmupSeconds = $WarmupSeconds
        warmupElapsedSeconds = $warmupElapsed; durationSeconds = [math]::Round($measurementSeconds,3)
        totalElapsedSeconds = [math]::Round($totalSeconds,3); logicalProcessors = [Environment]::ProcessorCount
        measurementIncludesStartup = ($WarmupSeconds -eq 0)
        startupAndWarmupCpuSeconds = if ($WarmupSeconds -gt 0) { $baselineCpuSeconds } else { $null }
        totalCpuSeconds = $cpuSeconds
        measurementCpuSeconds = $cpuSeconds - $baselineCpuSeconds
        workerCpuPercentOfMachine = [math]::Round($cpuPercent,3)
        startupInclusiveCpuPercentOfMachine = [math]::Round($cpuSeconds / $totalSeconds / [Environment]::ProcessorCount * 100,3)
        cpuUnderOnePercent = ($cpuPercent -lt 1)
        snapshotCount = $view.snapshotCount; totalSnapshots = $view.statistics.totalSnapshots
        measuredSnapshots = $view.statistics.totalSnapshots - $baselineSnapshots
        cumulativeMissedTickGapEpisodes = $view.statistics.missedTickGaps
        measuredMissedTickGapEpisodes = $view.statistics.missedTickGaps - $baselineMissedGaps
        samplingFailures = $view.statistics.samplingFailures; invalidIntervals = $view.statistics.invalidIntervals
        retainedSeconds = $view.retainedSeconds; retainedProcessRows = $view.retainedProcessRows
        estimatedRetainedBytes = $view.estimatedRetainedBytes
        peakWorkerMiB = [math]::Round($peakBytes / 1MB,2); peakMemoryIncludesStartupAndWarmup = $true
        samplingDiagnostics = $view.diagnostics; latestUnreadableProcesses = $view.history[-1].unreadableProcesses
        gapKinds = @($view.recentGaps | Group-Object kind | ForEach-Object { [pscustomobject]@{kind=$_.Name;count=$_.Count} })
        limitations = @('Shared-machine headless collector measurement; the environment is not an isolated idle workload.',
            'Only boundary status queries are sent; periodic WPF polling, rendering, ETL analysis and privileged capture have separate costs.',
            'Sampling diagnostics summarize retained history, while cumulative counters survive eviction.',
            'A short warmed window does not establish two-hour, sleep/resume, physical-write or observer-effect acceptance.')
    }
    $enoughTicks = $Seconds -gt 120 -or $result.measuredSnapshots -ge [math]::Floor($Seconds * .8)
    $result.success = $enoughTicks
    $result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $destination -Encoding utf8
    $result | ConvertTo-Json -Depth 6
    if (!$enoughTicks) { throw 'Too many measurement ticks were missed.' }
} catch {
    if (!(Test-Path -LiteralPath $destination)) {
        [pscustomobject]@{schema=2;success=$false;error=$_.Exception.Message;elapsedSeconds=$clock.Elapsed.TotalSeconds} |
            ConvertTo-Json | Set-Content -LiteralPath $destination -Encoding utf8
    }
    throw
} finally {
    if (!$child.HasExited) { $child.Kill(); $child.WaitForExit(5000) | Out-Null }
    $child.Dispose()
}
