[CmdletBinding()]param([switch]$DownloadFixtures)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$dotnet=Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if(!(Test-Path -LiteralPath $dotnet)) {$dotnet=(Get-Command dotnet -ErrorAction Stop).Source}
if($DownloadFixtures) {$env:WOI_FIXTURES=& (Join-Path $PSScriptRoot 'get-fixtures.ps1')}
elseif(Test-Path -LiteralPath (Join-Path $projectRoot '.artifacts\fixtures\net452\net.4.5.2.x64.etl')) {$env:WOI_FIXTURES=Join-Path $projectRoot '.artifacts\fixtures'}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
Push-Location $projectRoot
try {
    & $dotnet restore WindowsOpenIntelligence.slnx --locked-mode
    if($LASTEXITCODE -ne 0){throw 'Dependency lock verification failed.'}
    & $dotnet build WindowsOpenIntelligence.slnx --no-restore -c Release
    if($LASTEXITCODE -ne 0){throw 'Build failed.'}
    & $dotnet test tests/Woi.Tests/Woi.Tests.csproj --no-build -c Release --logger 'trx;LogFileName=verification.trx' --results-directory .artifacts/test-results
    if($LASTEXITCODE -ne 0){throw 'Tests failed.'}
} finally {Pop-Location}
