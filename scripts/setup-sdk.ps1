[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$toolsRoot=Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
$installer=Join-Path $toolsRoot 'dotnet-install.ps1'
Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
$version=(Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
& $installer -Version $version -Architecture x64 -InstallDir (Join-Path $toolsRoot 'dotnet') -NoPath
if(!(Test-Path -LiteralPath (Join-Path $toolsRoot 'dotnet\dotnet.exe'))){throw 'SDK installation failed.'}
Write-Output 'SDK installed locally. Run scripts/build.ps1.'
