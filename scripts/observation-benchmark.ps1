[CmdletBinding()]param(
    [ValidateRange(2,20)][int]$Trials = 3,
    [ValidateRange(1,60)][int]$WarmupSeconds = 10,
    [string]$OutputDirectory = '.artifacts/validation/observation-benchmark'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if (!$output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Benchmark output must stay in the workspace.' }
$workload = Join-Path $root '.artifacts/workload/Woi.Workload.exe'
$worker = Join-Path $root 'dist/WindowsOpenIntelligence/Woi.Worker.exe'
if (!(Test-Path -LiteralPath $workload) -or !(Test-Path -LiteralPath $worker)) { throw 'Publish the app and fixed-work workload first.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
function Launch([string]$executable, [string[]]$arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    return [Diagnostics.Process]::Start($start)
}
function Request($child, [string]$method) {
    $child.StandardInput.WriteLine((@{ id=[Guid]::NewGuid().ToString('N'); version=1; method=$method; parameters=@{} } | ConvertTo-Json -Compress))
    $child.StandardInput.Flush()
    $task = $child.StandardOutput.ReadLineAsync()
    if (!$task.Wait(10000)) { throw 'Monitor protocol request timed out.' }
    $reply = $task.Result | ConvertFrom-Json
    if ($reply.kind -ne 'result') { throw "Monitor error: $($reply.error)" }
    return $reply.result
}
$cases = [Collections.Generic.List[object]]::new()
for ($trial = 0; $trial -lt $Trials; $trial++) {
    $order = if ($trial % 2 -eq 0) { @($false,$true) } else { @($true,$false) }
    foreach ($observing in $order) {
        $monitor = $null; $job = $null; $clock = [Diagnostics.Stopwatch]::StartNew()
        $caseDirectory = Join-Path $output ('trial-' + $trial + '-' + $observing + '-' + [Guid]::NewGuid().ToString('N'))
        try {
            if ($observing) {
                $monitor = Launch $worker @('--monitor','--parent',[string]$PID,'--parent-start',[string]([Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks))
                $monitorError = $monitor.StandardError.ReadToEndAsync()
                $warmupClock = [Diagnostics.Stopwatch]::StartNew()
                Start-Sleep -Seconds $WarmupSeconds
                if ($warmupClock.Elapsed.TotalSeconds -gt $WarmupSeconds + 5) {
                    throw 'Warm-up exceeded its elapsed-time budget; possible suspend/resume or interrupted test. This trial cannot establish observer cost.'
                }
            }
            $job = Launch $workload @('--fixed-work',$caseDirectory)
            $jobOut = $job.StandardOutput.ReadToEndAsync(); $jobError = $job.StandardError.ReadToEndAsync()
            if (!$job.WaitForExit(120000)) { throw 'Fixed-work workload timed out.' }
            if ($job.ExitCode -ne 0) { throw "Fixed-work workload failed: $($jobError.GetAwaiter().GetResult())" }
            $metadata = Get-Content -LiteralPath (Join-Path $caseDirectory 'workload.json') -Raw | ConvertFrom-Json
            if (!$metadata.fixedWork -or $metadata.cpuIterationsPerWorker -ne 500000000 -or $metadata.cpuResultChecksum -le 0) { throw 'The workload does not provide the required fixed amount of work.' }
            $cpuPhaseMs = $metadata.phases | Where-Object name -eq "CPU burst" | ForEach-Object { $_.endMs - $_.startMs }
            $measurementSeconds = $clock.Elapsed.TotalSeconds
            $expectedSeconds = $metadata.durationMs / 1000 + $(if ($observing) { $WarmupSeconds } else { 0 })
            if ($measurementSeconds -gt $expectedSeconds + 30) {
                throw 'Measurement elapsed-time budget exceeded; possible suspend/resume or interrupted test. Preserve this failure and restart a new run.'
            }
            if ($cpuPhaseMs -lt 2000) { throw "The CPU phase lasted less than two seconds; increase fixed work before interpreting observer effect." }
            $view = if ($monitor) { Request $monitor 'monitor.status' } else { $null }
            $record = [pscustomobject]@{
                elapsedMeasurementSeconds=$measurementSeconds; trial=$trial; observing=$observing; orderIndex=[array]::IndexOf($order,$observing); directory=$caseDirectory
                durationMs=$metadata.durationMs
                cpuWorkMs=($metadata.phases | Where-Object name -eq 'CPU burst' | ForEach-Object { $_.endMs - $_.startMs })
                cpuIterationsPerWorker=$metadata.cpuIterationsPerWorker; cpuWorkers=$metadata.cpuWorkers; cpuResultChecksum=$metadata.cpuResultChecksum
                logicalProcessors=$metadata.logicalProcessors; workloadExpected=$metadata.expected
                peakMonitorMiB=if($monitor){$monitor.PeakWorkingSet64 / 1MB}else{$null}
                monitorCpuPercentOfMachine=if($monitor){$monitor.TotalProcessorTime.TotalSeconds / $clock.Elapsed.TotalSeconds / [Environment]::ProcessorCount * 100}else{$null}
                monitorSnapshotCount=if($monitor){$view.snapshotCount}else{$null}; samplingDiagnostics=if($monitor){$view.diagnostics}else{$null}
            }
            $cases.Add($record)
            if ($monitor) { $stop = Request $monitor 'monitor.stop'; if (!$stop.stopped -or !$monitor.WaitForExit(5000) -or $monitor.ExitCode -ne 0) { throw 'Monitor did not stop cleanly.' } }
        } catch {
            $cases.Add([pscustomobject]@{trial=$trial;observing=$observing;failure=$_.Exception.ToString();directory=$caseDirectory})
            $cases | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'cases.json') -Encoding utf8
            throw
        } finally {
            foreach ($child in @($job,$monitor)) { if ($child) { if (!$child.HasExited) { $child.Kill($true); $child.WaitForExit(5000) | Out-Null }; $child.Dispose() } }
        }
    }
}
function Median([double[]]$values) { $sorted=@($values | Sort-Object); $middle=[int][math]::Floor($sorted.Count/2); if($sorted.Count%2){return $sorted[$middle]}; return ($sorted[$middle-1]+$sorted[$middle])/2 }
$off=@($cases | Where-Object observing -eq $false); $on=@($cases | Where-Object observing -eq $true)
$offCpu=Median $off.cpuWorkMs; $onCpu=Median $on.cpuWorkMs
$offTotal=Median $off.durationMs; $onTotal=Median $on.durationMs
$result=[pscustomobject]@{
    schema=1; utc=[DateTime]::UtcNow; machine=[Environment]::MachineName; os=[Environment]::OSVersion.VersionString; logicalProcessors=[Environment]::ProcessorCount
    trialsPerCondition=$Trials; monitorWarmupSeconds=$WarmupSeconds; ordering='counterbalanced alternating'; cases=$cases
    medianCpuWorkOffMs=$offCpu; medianCpuWorkOnMs=$onCpu; cpuWorkSlowdownPercent=($onCpu/$offCpu-1)*100
    medianTaskOffMs=$offTotal; medianTaskOnMs=$onTotal; medianTaskSlowdownPercent=($onTotal/$offTotal-1)*100
    cpuWorkRangeOffMs=@(($off.cpuWorkMs | Measure-Object -Minimum).Minimum,($off.cpuWorkMs | Measure-Object -Maximum).Maximum)
    cpuWorkRangeOnMs=@(($on.cpuWorkMs | Measure-Object -Minimum).Minimum,($on.cpuWorkMs | Measure-Object -Maximum).Maximum)
    provisionalTaskTargetMet=($onTotal/$offTotal-1)*100 -lt 3
    limitations=@('Shared-machine activity and cache effects remain confounders.','Small trial counts are exploratory; record variance and repeat.','This measures the isolated monitor, not the WPF UI, ETL analysis, native tracing, sleep/resume, or 24-hour behavior.','CPU phase uses a fixed iteration count; the original time-bounded capture workload cannot measure CPU throughput slowdown.')
}
$result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
$result | Select-Object trialsPerCondition,medianCpuWorkOffMs,medianCpuWorkOnMs,cpuWorkSlowdownPercent,medianTaskSlowdownPercent,provisionalTaskTargetMet | ConvertTo-Json
