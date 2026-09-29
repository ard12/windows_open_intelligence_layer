[CmdletBinding()]param([string]$Trace,[string]$Report)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$dotnet=Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if(!(Test-Path -LiteralPath $dotnet)){$dotnet=(Get-Command dotnet -ErrorAction Stop).Source}
if(!$Trace -or !$Report){throw 'Provide -Trace and -Report. Exit 1 means measured acceptance checks failed; inspect the report.'}
& $dotnet restore (Join-Path $projectRoot 'tools\Woi.Reference\Woi.Reference.csproj') --locked-mode
if($LASTEXITCODE -ne 0){throw 'Reference dependencies do not match the lock file.'}
& $dotnet build (Join-Path $projectRoot 'tools\Woi.Reference\Woi.Reference.csproj') -c Release --no-restore
if($LASTEXITCODE -ne 0){throw 'Reference validator build failed.'}
& $dotnet (Join-Path $projectRoot 'tools\Woi.Reference\bin\Release\net10.0-windows\Woi.Reference.dll') $Trace $Report
$validationExit=$LASTEXITCODE
if($validationExit -eq 1){Write-Warning 'Reference measurements were recorded, but an acceptance check failed. See the JSON report.'}
elseif($validationExit -ne 0){throw 'Reference validation could not complete.'}
exit $validationExit
