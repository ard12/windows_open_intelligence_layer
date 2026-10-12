[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$scratch=Join-Path $projectRoot ('.artifacts\dependency-verification\fixtures-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch -Force | Out-Null
$packageRoot=$env:NUGET_PACKAGES
if(!$packageRoot) {
    $localPackages=Join-Path $projectRoot '.tools\nuget'
    $packageRoot=if(Test-Path -LiteralPath $localPackages){$localPackages}else{Join-Path $env:USERPROFILE '.nuget\packages'}
}
$package='SQLitePCLRaw.core/2.1.11'
$asset='lib/netstandard2.0/SQLitePCLRaw.core.dll'
$manifest=@{
    runtimeTarget=@{name='.NETCoreApp,Version=v10.0/win-x64'}
    targets=@{'.NETCoreApp,Version=v10.0/win-x64'=@{$package=@{runtime=@{$asset=@{}}}}}
    libraries=@{$package=@{type='package'}}
} | ConvertTo-Json -Depth 8
$config=@{runtimeOptions=@{includedFrameworks=@(@{name='Microsoft.NETCore.App';version='10.0.12'})}} | ConvertTo-Json -Depth 5
foreach($name in @('WindowsOpenIntelligence','Woi.Worker','Woi.Capture')) {
    [IO.File]::WriteAllText((Join-Path $scratch ($name+'.deps.json')),$manifest)
    [IO.File]::WriteAllText((Join-Path $scratch ($name+'.runtimeconfig.json')),$config)
}
$dll=Join-Path $scratch 'SQLitePCLRaw.core.dll'
Copy-Item -LiteralPath (Join-Path $packageRoot ($package+'/'+$asset)) -Destination $dll
$successReport=Join-Path $scratch 'valid.json'
& (Join-Path $PSScriptRoot 'verify-dependencies.ps1') -BundlePath $scratch -OutputPath $successReport
if(!(Get-Content -LiteralPath $successReport -Raw | ConvertFrom-Json).success){throw 'Valid package-byte fixture did not pass.'}
$bytes=[IO.File]::ReadAllBytes($dll)
$bytes[$bytes.Length-1]=$bytes[$bytes.Length-1] -bxor 1
[IO.File]::WriteAllBytes($dll,$bytes)
$failureReport=Join-Path $scratch 'tampered.json'
$failed=$false
try {& (Join-Path $PSScriptRoot 'verify-dependencies.ps1') -BundlePath $scratch -OutputPath $failureReport} catch {$failed=$true}
if(!$failed -or !(Test-Path -LiteralPath $failureReport)){throw 'Tampered dependency did not produce a failure report.'}
$report=Get-Content -LiteralPath $failureReport -Raw | ConvertFrom-Json
if($report.success -or !@($report.errors | Where-Object {$_ -eq 'Bundled bytes differ from exact cached package: SQLitePCLRaw.core.dll (sqlitepclraw.core/2.1.11)'}).Count) {
    throw 'Tampered dependency failed for an unexpected reason.'
}
Copy-Item -LiteralPath (Join-Path $packageRoot ($package+'/'+$asset)) -Destination $dll -Force
Copy-Item -LiteralPath $dll -Destination (Join-Path $scratch 'unexpected.dll')
$unknownReport=Join-Path $scratch 'unexpected.json'
$failed=$false
try {& (Join-Path $PSScriptRoot 'verify-dependencies.ps1') -BundlePath $scratch -OutputPath $unknownReport} catch {$failed=$true}
if(!$failed -or !(Test-Path -LiteralPath $unknownReport)){throw 'Unaccounted binary did not produce a failure report.'}
$report=Get-Content -LiteralPath $unknownReport -Raw | ConvertFrom-Json
if($report.success -or $report.errors -notcontains 'Unaccounted bundled binary: unexpected.dll'){throw 'Unaccounted binary failed for an unexpected reason.'}
Write-Output "PASS: clean archived dependency accepted; one-byte alteration and unaccounted binary rejected. Fixtures and reports: $scratch"
