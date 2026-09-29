[CmdletBinding()]param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$identity=[Security.Principal.WindowsIdentity]::GetCurrent()
if(!([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){
    throw 'Run this validation capture from an administrator PowerShell. The analyzer stays unprivileged.'
}
$output=[IO.Path]::GetFullPath($OutputDirectory)
$workspace=[IO.Path]::GetFullPath($projectRoot).TrimEnd('\')+'\'
if(!$output.StartsWith($workspace,[StringComparison]::OrdinalIgnoreCase)){throw 'Validation output must stay inside the project workspace.'}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$workload=Join-Path $projectRoot '.artifacts\workload\Woi.Workload.exe'
if(!(Test-Path -LiteralPath $workload)){throw 'Publish tools/Woi.Workload to .artifacts/workload first.'}
if(Test-Path -LiteralPath (Join-Path $output 'controlled-io.bin')){throw 'Use a new empty output directory for each capture.'}
if(([IO.DriveInfo]::new([IO.Path]::GetPathRoot($output))).AvailableFreeSpace -lt 512MB){throw 'Validation requires at least 512 MiB free space.'}
$instance='WOIValidation'+[Guid]::NewGuid().ToString('N')
$started=$false
$trace=Join-Path $output 'controlled.etl'
$log=Join-Path $output 'capture.log'
try {
    $status=& wpr.exe -status 2>&1
    $status | Out-File -LiteralPath $log
    if($LASTEXITCODE -ne 0 -or ($status -join ' ') -notmatch 'WPR is not recording'){
        throw 'Another WPR recording may be active. Validation refuses to start until it is finished.'
    }
    & wpr.exe -start GeneralProfile -start FileIO -start Network -start ResidentSet -filemode -recordtempto $output -instancename $instance 2>&1 | Out-File -LiteralPath $log -Append
    if($LASTEXITCODE -ne 0){throw 'WPR could not start the validation profiles. See capture.log.'}
    $started=$true
    $recordingStartedUtc=[DateTime]::UtcNow
    $process=Start-Process -FilePath $workload -ArgumentList ('"'+$output+'"') -PassThru -WindowStyle Hidden
    if(!$process.WaitForExit(45000)){Stop-Process -Id $process.Id -Force;throw 'The controlled workload exceeded its 45-second limit.'}
    if($process.ExitCode -ne 0){throw 'The controlled workload failed.'}
    & wpr.exe -stop $trace 'Windows Open Intelligence controlled analyzer validation' -instancename $instance 2>&1 | Out-File -LiteralPath $log -Append
    if($LASTEXITCODE -ne 0){throw 'WPR failed to preserve the validation recording.'}
    $started=$false
    [ordered]@{schema=1;instance=$instance;profiles=@('GeneralProfile','FileIO','Network','ResidentSet');mode='file';startedUtc=$recordingStartedUtc;endedUtc=[DateTime]::UtcNow;trace=$trace;sha256=(Get-FileHash -LiteralPath $trace -Algorithm SHA256).Hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $trace).Length} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'capture.json') -Encoding UTF8
} catch {
    $_.Exception.Message | Set-Content -LiteralPath (Join-Path $output 'failure.txt') -Encoding UTF8
    throw
} finally {
    if($started){& wpr.exe -cancel -instancename $instance 2>&1 | Out-File -LiteralPath $log -Append}
}
