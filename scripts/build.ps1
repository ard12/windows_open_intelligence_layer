[CmdletBinding()]param([ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$dotnet=Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if(!(Test-Path -LiteralPath $dotnet)) {$dotnet=(Get-Command dotnet -ErrorAction Stop).Source}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
Push-Location $projectRoot
try {
    & $dotnet restore WindowsOpenIntelligence.slnx --locked-mode
    if($LASTEXITCODE -ne 0){throw 'Dependency lock verification failed.'}
    & $dotnet build WindowsOpenIntelligence.slnx -c $Configuration --no-restore
    if($LASTEXITCODE -ne 0){throw 'Build failed.'}
} finally {Pop-Location}
