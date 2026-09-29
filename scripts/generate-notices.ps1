[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$packageRoot=$env:NUGET_PACKAGES
if(!$packageRoot) {
    $localPackages=Join-Path $projectRoot '.tools\nuget'
    $packageRoot=if(Test-Path -LiteralPath $localPackages){$localPackages}else{Join-Path $env:USERPROFILE '.nuget\packages'}
}
$licenses=Join-Path $projectRoot 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
$packages=@{}
foreach($lock in @('src\Woi.Desktop\packages.lock.json','src\Woi.Worker\packages.lock.json')) {
    $data=Get-Content -LiteralPath (Join-Path $projectRoot $lock) -Raw | ConvertFrom-Json
    foreach($framework in $data.dependencies.PSObject.Properties) {
        foreach($dependency in $framework.Value.PSObject.Properties) {
            if($dependency.Value.type -ne 'Project') {$packages[$dependency.Name]=$dependency.Value.resolved}
        }
    }
}
foreach($id in @('Microsoft.NETCore.App.Runtime.win-x64','Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
    $folder=Join-Path $packageRoot $id.ToLowerInvariant()
    $version=(Get-ChildItem -LiteralPath $folder -Directory | Sort-Object Name -Descending | Select-Object -First 1).Name
    $packages[$id]=$version
}
$lines=[Collections.Generic.List[string]]::new()
$lines.Add('# Third-party notices')
$lines.Add('')
$lines.Add('Windows Open Intelligence uses the packages below. Original package metadata and included license/notice files are preserved in licenses/. Platform-specific dependencies may be resolved but omitted from the Windows x64 output. Test dependencies are excluded from this application inventory. No System Informer or OpenProcMon source or driver is distributed.')
$lines.Add('')
$lines.Add('| Package | Version | License | Authors / copyright |')
$lines.Add('|---|---|---|---|')
foreach($id in ($packages.Keys | Sort-Object)) {
    $version=$packages[$id]
    $directory=Join-Path (Join-Path $packageRoot $id.ToLowerInvariant()) $version
    $nuspec=Get-ChildItem -LiteralPath $directory -Filter '*.nuspec' | Select-Object -First 1
    if(!$nuspec){throw "Missing package metadata: $id $version"}
    [xml]$metadata=Get-Content -LiteralPath $nuspec.FullName -Raw
    $license=$metadata.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='license']")
    $authors=$metadata.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='authors']").InnerText
    $copyright=$metadata.SelectSingleNode("/*[local-name()='package']/*[local-name()='metadata']/*[local-name()='copyright']").InnerText
    $licenseText=if($license){$license.InnerText}else{'See original license / upstream notices'}
    $lines.Add("| $id | $version | $licenseText | $authors $copyright |")
    $destination=Join-Path $licenses ($id+'-'+$version)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $nuspec.FullName -Destination $destination -Force
    foreach($file in (Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {$_.Name -match 'LICENSE|NOTICE|COPYING|AUTHORS'})) {
        $relative=$file.FullName.Substring($directory.Length+1)
        $target=Join-Path $destination $relative
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}
$lines.Add('')
$lines.Add('MIT terms are preserved in licenses/MIT.txt, Apache 2.0 terms in licenses/Apache-2.0.txt, and original TraceEvent, ScottPlot, CommunityToolkit, runtime, and native-library notices alongside them. SQLite itself is public domain; the SQLitePCLRaw managed wrapper uses Apache 2.0.')
[IO.File]::WriteAllLines((Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md'),$lines,[Text.UTF8Encoding]::new($false))
