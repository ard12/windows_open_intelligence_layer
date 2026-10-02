[CmdletBinding()]param(
    [ValidateSet('Start','Status')][string]$Mode='Start',
    [ValidateRange(10,86400)][int]$Seconds=7200,
    [string]$Output='.artifacts/validation/desktop-soak.json',
    [string]$PackageDirectory='dist/WindowsOpenIntelligence'
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$workspace=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)).TrimEnd([IO.Path]::DirectorySeparatorChar)
function WorkspacePath([string]$value) {
    $resolved=[IO.Path]::GetFullPath($value,$workspace)
    if (-not $resolved.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Soak paths must stay within the project workspace.'
    }
    return $resolved
}
function ReadJson([string]$path) {
    $file=Get-Item -LiteralPath $path -ErrorAction Stop
    if ($file.Length -gt 32MB) { throw 'Soak JSON exceeds the 32 MiB reporting limit.' }
    return Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
}
function Field($value,[string]$name,$fallback=$null) {
    if ($null -eq $value) { return $fallback }
    $property=$value.PSObject.Properties[$name]
    if ($null -eq $property) { return $fallback }
    return $property.Value
}
$outputPath=WorkspacePath $Output
$runPath=[IO.Path]::ChangeExtension($outputPath,'run.json')
$progressPath=$outputPath+'.progress.json'
if ($Mode -eq 'Status') {
    $run=ReadJson $runPath
    $recordedOutput=WorkspacePath ([string](Field $run 'output'))
    $recordedExecutable=WorkspacePath ([string](Field $run 'executable'))
    if ($recordedOutput -ne $outputPath -or [IO.Path]::GetFileName($recordedExecutable) -ne 'WindowsOpenIntelligence.exe') {
        throw 'Run metadata does not match this soak.'
    }
    $processId=[int](Field $run 'pid' 0)
    $identity='not recorded'; $live=$false
    if ($processId -gt 0) {
        $process=Get-CimInstance Win32_Process -Filter ('ProcessId='+$processId)
        if ($null -eq $process) { $identity='process absent' }
        elseif (-not $process.ExecutablePath) { $identity='cannot verify executable'; $live=$true }
        else {
            # ConvertFrom-Json can return an existing UTC DateTime. Preserve its Kind;
            # reparsing its culture-formatted string would shift the recorded instant.
            $expectedStart=([DateTime](Field $run 'startUtc')).ToUniversalTime()
            if ($process.ExecutablePath -eq $recordedExecutable -and
                [Math]::Abs(($process.CreationDate.ToUniversalTime()-$expectedStart).TotalSeconds) -lt .01) {
                $identity='verified'; $live=$true
            } else { $identity='PID reused or metadata mismatch' }
        }
    }
    $progress=if (Test-Path -LiteralPath $progressPath) { ReadJson $progressPath } else {$null}
    $result=if (Test-Path -LiteralPath $outputPath) { ReadJson $outputPath } else {$null}
    $status=if ($live -and $identity -eq 'verified') {'running'}
        elseif ($live) {'identity unverified'}
        elseif ($null -ne $result -and (Field $result 'success') -eq $true) {'completed'}
        elseif ($null -ne $result) {'failed'}
        elseif ($identity -eq 'PID reused or metadata mismatch') {'identity mismatch'}
        elseif (Field $run 'launchError') {'launch failed'}
        else {'exited without result'}
    $requested=[int](Field $run 'requestedSeconds' 0)
    $duration=Field $result 'durationSeconds'
    # A success-shaped result for a different duration does not prove this run.
    if ($status -eq 'completed' -and ($requested -lt 10 -or $null -eq $duration -or $duration -lt $requested)) {
        $status='incomplete result'
    }
    if ($progress -and ([int](Field $progress 'pid' 0) -ne $processId -or
        [int](Field $progress 'requestedSeconds' 0) -ne $requested)) {
        $status='progress identity mismatch'
    }
    $finalView=Field $result 'finalView'
    $statistics=if ($finalView) {Field $finalView 'statistics'} elseif ($progress) {Field $progress 'statistics'} else {$null}
    [pscustomobject]@{
        status=$status; processIdentity=$identity; pid=$processId; requestedSeconds=$requested
        elapsedSeconds=if ($duration) {$duration} elseif ($progress) {Field $progress 'elapsedSeconds'} else {$null}
        retainedSnapshotCount=if ($finalView) {Field $finalView 'snapshotCount'} elseif ($progress) {Field $progress 'snapshotCount'} else {$null}
        cumulativeStatistics=$statistics
        resultPresent=($null -ne $result); runMetadata=$runPath
        acceptance=if ($status -eq 'completed') {
            [pscustomobject]@{
                cumulativeCoverageTargetMet=Field $result 'cumulativeCoverageTargetMet'
                monitorMemoryTargetMet=Field $result 'monitorMemoryTargetMet'
                uiPlusMonitorMemoryTargetMet=Field $result 'uiPlusMonitorMemoryTargetMet'
                analysisWorkerStarted=Field $result 'analysisWorkerStarted'
            }
        } else {$null}
    } | ConvertTo-Json -Depth 6
    return
}
foreach ($path in @($outputPath,$progressPath,$runPath)) {
    if (Test-Path -LiteralPath $path) { throw 'Existing soak evidence or run metadata found. Use -Mode Status; use a fresh output name for a new run.' }
}
$directory=WorkspacePath $PackageDirectory
$executable=Join-Path $directory 'WindowsOpenIntelligence.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'Publish or extract the self-contained package first.' }
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($outputPath)) | Out-Null
# Reserve the run name exclusively before starting; concurrent launches cannot
# overwrite each other's evidence or start a second process for the same output.
$binaryHashes=[ordered]@{}
foreach ($name in @('WindowsOpenIntelligence.exe','WindowsOpenIntelligence.dll','Woi.Worker.exe','Woi.Worker.dll','Woi.Monitor.dll','Woi.Core.dll')) {
    $binaryHashes[$name]=(Get-FileHash -LiteralPath (Join-Path $directory $name)).Hash.ToLowerInvariant()
}
$previousTemp=$env:TEMP; $previousTmp=$env:TMP
$record=[ordered]@{schema=1;pid=0;startUtc=$null;executable=$executable;output=$outputPath;binaryHashes=$binaryHashes;
    requestedSeconds=$Seconds;executableSha256=(Get-FileHash -LiteralPath $executable).Hash.ToLowerInvariant();
    launchedUtc=[DateTime]::UtcNow.ToString('o');launchError=$null}
$reservation=[IO.File]::Open($runPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try {
    # These changes affect only this launch and its children, not user-wide settings.
    $env:TEMP=Join-Path $workspace '.artifacts/task-temp'
    $env:TMP=$env:TEMP
    [IO.Directory]::CreateDirectory($env:TEMP) | Out-Null
    $process=Start-Process -FilePath $executable -ArgumentList @('--observation-soak',$Seconds,('"'+$outputPath+'"')) -WindowStyle Hidden -PassThru
    $record.pid=$process.Id
    $record.startUtc=$process.StartTime.ToUniversalTime().ToString('o')
} catch {
    $record.launchError=$_.Exception.Message
    throw
} finally {
    $env:TEMP=$previousTemp; $env:TMP=$previousTmp
    $bytes=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Depth 4))
    $reservation.Write($bytes,0,$bytes.Length)
    $reservation.Flush($true)
    $reservation.Dispose()
}
$record|ConvertTo-Json -Depth 4
Write-Verbose 'Launched once. Query -Mode Status with the same Output; this command does not wait or infer acceptance.'