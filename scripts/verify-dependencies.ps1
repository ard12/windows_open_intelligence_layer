[CmdletBinding()]
param(
    [string]$BundlePath,
    [string]$OutputPath
)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
if(!$BundlePath){$BundlePath=Join-Path $projectRoot 'dist\WindowsOpenIntelligence'}
if(!$OutputPath){$OutputPath=Join-Path $projectRoot '.artifacts\dependency-verification\report.json'}
$packageRoot=$env:NUGET_PACKAGES
if(!$packageRoot) {
    $localPackages=Join-Path $projectRoot '.tools\nuget'
    $packageRoot=if(Test-Path -LiteralPath $localPackages){$localPackages}else{Join-Path $env:USERPROFILE '.nuget\packages'}
}
$sdkVersion=(Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$versioning=Join-Path $projectRoot ".tools\dotnet\sdk\$sdkVersion\NuGet.Versioning.dll"
if(!(Test-Path -LiteralPath $versioning)) {
    $dotnet=(Get-Command dotnet -ErrorAction Stop).Source
    $versioning=Join-Path (Split-Path $dotnet -Parent) "sdk\$sdkVersion\NuGet.Versioning.dll"
}
foreach($assembly in @('NuGet.Common','NuGet.Versioning','NuGet.Frameworks','NuGet.Packaging')) {
    Add-Type -Path (Join-Path (Split-Path $versioning -Parent) ($assembly+'.dll'))
}
# Use NuGet's own range semantics, including prereleases, rather than a custom version comparator.
if(![NuGet.Versioning.VersionRange]::Parse('(, 13.0.1)').Satisfies([NuGet.Versioning.NuGetVersion]::Parse('12.0.3')) -or
    [NuGet.Versioning.VersionRange]::Parse('(, 13.0.1)').Satisfies([NuGet.Versioning.NuGetVersion]::Parse('13.0.1')) -or
    ![NuGet.Versioning.VersionRange]::Parse('[3.3.0-pre20200830200122]').Satisfies([NuGet.Versioning.NuGetVersion]::Parse('3.3.0-pre20200830200122'))) {
    throw 'NuGet range semantic self-check failed.'
}
function Get-Sha256Text([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}
function Get-AdvisoryJson([string]$Uri) {
    $address=[Uri]$Uri
    if($address.Scheme -ne 'https' -or $address.Host -notin 'api.nuget.org','builds.dotnet.microsoft.com'){throw "Unexpected advisory host: $Uri"}
    $response=Invoke-WebRequest -Uri $Uri -TimeoutSec 30 -MaximumRedirection 0
    if($response.StatusCode -ne 200 -or $response.RawContentLength -gt 20MB -or $response.Content.Length -gt 20MB){throw "Invalid advisory response: $Uri"}
    @{data=($response.Content | ConvertFrom-Json -AsHashtable); sha256=(Get-Sha256Text $response.Content); root=$response.Content.Trim()}
}
$packages=@{}
$lockFiles=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'src'),(Join-Path $projectRoot 'tests'),(Join-Path $projectRoot 'tools') -Recurse -File -Filter 'packages.lock.json')
if(!$lockFiles.Count){throw 'No lock files found.'}
foreach($file in $lockFiles) {
    $lock=Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
    foreach($framework in $lock.dependencies.Values) {
        foreach($id in $framework.Keys) {
            $entry=$framework[$id]
            if($entry.type -eq 'Project'){continue}
            if(!$entry.resolved -or !$entry.contentHash){throw "Incomplete lock entry: $id in $($file.Name)"}
            $key=($id+'/'+$entry.resolved).ToLowerInvariant()
            if(!$packages.ContainsKey($key)){$packages[$key]=@{id=$id; version=$entry.resolved; contentHash=$entry.contentHash; scope='locked'}}
            elseif($packages[$key].contentHash -ne $entry.contentHash){throw "Conflicting package hashes: $key"}
        }
    }
}
$errors=[Collections.Generic.List[string]]::new()
$assets=[Collections.Generic.List[object]]::new()
$manifests=@(Get-ChildItem -LiteralPath $BundlePath -File -Filter '*.deps.json')
foreach($required in @('WindowsOpenIntelligence','Woi.Worker','Woi.Capture')) {
    if(!(Test-Path -LiteralPath (Join-Path $BundlePath ($required+'.deps.json')))){throw "Missing published dependency manifest: $required"}
    $config=Get-Content -LiteralPath (Join-Path $BundlePath ($required+'.runtimeconfig.json')) -Raw | ConvertFrom-Json
    if(!$config.runtimeOptions.includedFrameworks){throw "Bundle is not self-contained: $required"}
}
$seenAssets=@{}
$shippedPackages=@{}
$applicationBinaries=@{}
foreach($manifestFile in $manifests) {
    $manifest=Get-Content -LiteralPath $manifestFile.FullName -Raw | ConvertFrom-Json -AsHashtable
    $target=$manifest.targets[$manifest.runtimeTarget.name]
    if(!$target){throw "Missing selected runtime target in $($manifestFile.Name)"}
    foreach($libraryKey in $target.Keys) {
        $library=$manifest.libraries[$libraryKey]
        $isRuntime=$libraryKey.StartsWith('runtimepack.',[StringComparison]::OrdinalIgnoreCase)
        if($library.type -ne 'package' -and !$isRuntime) {
            foreach($asset in $target[$libraryKey].runtime.Keys){$applicationBinaries[[IO.Path]::GetFileName($asset)]=$true}
            continue
        }
        $separator=$libraryKey.LastIndexOf('/')
        $id=$libraryKey.Substring(0,$separator) -replace '^runtimepack\.',''
        $version=$libraryKey.Substring($separator+1)
        $packageKey=($id+'/'+$version).ToLowerInvariant()
        $shippedPackages[$packageKey]=$true
        if(!$isRuntime -and !$packages.ContainsKey($packageKey)){throw "Published package is absent from locks: $packageKey"}
        if($isRuntime -and !$packages.ContainsKey($packageKey)){$packages[$packageKey]=@{id=$id; version=$version; contentHash=$null; scope='published-runtime-pack'}}
        $origin=Join-Path $packageRoot $packageKey
        if(!(Test-Path -LiteralPath $origin)){throw "Missing cached package for identity check: $packageKey"}
        foreach($kind in @('runtime','native','resources')) {
            $entries=$target[$libraryKey][$kind]
            if(!$entries){continue}
            foreach($asset in $entries.Keys) {
                if($asset.EndsWith('/_._')){continue}
                $relative=if($kind -eq 'resources') { Join-Path $entries[$asset].locale ([IO.Path]::GetFileName($asset)) } else { [IO.Path]::GetFileName($asset) }
                $assetKey=$packageKey+'|'+$asset+'|'+$relative
                if($seenAssets.ContainsKey($assetKey)){continue}
                $seenAssets[$assetKey]=$true
                $published=Join-Path $BundlePath $relative
                if(!(Test-Path -LiteralPath $published -PathType Leaf)){$errors.Add("Missing bundled asset: $relative ($packageKey)");continue}
                $hash=(Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash.ToLowerInvariant()
                $archive=Join-Path $origin ($id.ToLowerInvariant()+'.'+$version.ToLowerInvariant()+'.nupkg')
                $zip=[IO.Compression.ZipFile]::OpenRead($archive)
                try {
                    $suffix=($relative -replace '\\','/')
                    $origins=if($isRuntime) { @($zip.Entries | Where-Object {$_.FullName.EndsWith('/'+$suffix,[StringComparison]::Ordinal)}) } else { @($zip.Entries | Where-Object {$_.FullName -eq $asset}) }
                    $matched=$false
                    foreach($entry in $origins) {
                        $stream=$entry.Open()
                        try {$originHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()} finally {$stream.Dispose()}
                        if($originHash -eq $hash){$matched=$true;break}
                    }
                } finally {$zip.Dispose()}
                $assets.Add(@{path=$relative; package=$packageKey; kind=$kind; sha256=$hash; matchesCachedPackage=$matched})
            }
        }
    }
    $applicationBinaries[$manifestFile.Name.Replace('.deps.json','.exe')]=$true
}
# TraceEvent copies these through MSBuild content items, outside its dependency manifest's native list.
foreach($packageKey in $shippedPackages.Keys | Where-Object {$_.StartsWith('microsoft.diagnostics.tracing.traceevent/')}) {
    $package=$packages[$packageKey]
    $archive=Join-Path (Join-Path $packageRoot $packageKey) ($package.id.ToLowerInvariant()+'.'+$package.version.ToLowerInvariant()+'.nupkg')
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try {
        foreach($name in @('KernelTraceControl.dll','msdia140.dll')) {
            $relative='amd64/'+$name
            $published=Join-Path $BundlePath $relative
            if(!(Test-Path -LiteralPath $published -PathType Leaf)){$errors.Add("Missing bundled content asset: $relative");continue}
            $hash=(Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash.ToLowerInvariant()
            $entry=$zip.GetEntry('build/native/'+$relative)
            if(!$entry){throw "Missing archived TraceEvent content asset: $relative"}
            $stream=$entry.Open()
            try {$originHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()} finally {$stream.Dispose()}
            $assets.Add(@{path=$relative; package=$packageKey; kind='native-content'; sha256=$hash; matchesCachedPackage=($hash -eq $originHash)})
        }
    } finally {$zip.Dispose()}
}
$knownAssets=@{}
foreach($asset in $assets){$knownAssets[$asset.path.Replace('/',[IO.Path]::DirectorySeparatorChar)]=$true}
$bundleRoot=[IO.Path]::GetFullPath($BundlePath).TrimEnd([IO.Path]::DirectorySeparatorChar)
foreach($file in Get-ChildItem -LiteralPath $bundleRoot -Recurse -File | Where-Object {$_.Extension -in '.dll','.exe'}) {
    $relative=$file.FullName.Substring($bundleRoot.Length+1)
    if($knownAssets.ContainsKey($relative) -or $applicationBinaries.ContainsKey($relative)){continue}
    # Self-contained WPF also copies satellite assemblies which are absent from deps.json.
    if($relative -match '^[a-z]{2}(-[A-Za-z]{2,4})?\\[^\\]+\.resources\.dll$') {
        foreach($packageKey in $shippedPackages.Keys | Where-Object {$_ -match '^microsoft\.(netcore|windowsdesktop)\.app\.runtime\.win-x64/'}) {
            $package=$packages[$packageKey]
            $archive=Join-Path (Join-Path $packageRoot $packageKey) ($package.id.ToLowerInvariant()+'.'+$package.version.ToLowerInvariant()+'.nupkg')
            $zip=[IO.Compression.ZipFile]::OpenRead($archive)
            try {
                $entry=@($zip.Entries | Where-Object {$_.FullName.EndsWith('/'+$relative.Replace('\','/'),[StringComparison]::Ordinal)})
                if(!$entry.Count){continue}
                $stream=$entry[0].Open()
                try {$originHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()} finally {$stream.Dispose()}
                $hash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                $assets.Add(@{path=$relative; package=$packageKey; kind='resource-content'; sha256=$hash; matchesCachedPackage=($hash -eq $originHash)})
                $knownAssets[$relative]=$true
                break
            } finally {$zip.Dispose()}
        }
    }
    if(!$knownAssets.ContainsKey($relative)){$errors.Add("Unaccounted bundled binary: $relative")}
}
foreach($asset in $assets) {
    if($asset.matchesCachedPackage){continue}
    # WPF replaces the core runtime's WindowsBase facade when self-contained projects share one output.
    $frameworkVersion=$asset.package -replace '^microsoft.netcore.app.runtime.win-x64/',''
    $replacement=@($assets | Where-Object {$_.path -eq 'WindowsBase.dll' -and $_.package -eq "microsoft.windowsdesktop.app.runtime.win-x64/$frameworkVersion" -and $_.matchesCachedPackage})
    if($asset.path -eq 'WindowsBase.dll' -and $asset.package.StartsWith('microsoft.netcore.app.runtime.win-x64/') -and $replacement.Count) {
        $asset.substitutedBy=$replacement[0].package
    } else {$errors.Add("Bundled bytes differ from exact cached package: $($asset.path) ($($asset.package))")}
}
foreach($package in $packages.Values) {
    $key=($package.id+'/'+$package.version).ToLowerInvariant()
    $archive=Join-Path (Join-Path $packageRoot $key) ($package.id.ToLowerInvariant()+'.'+$package.version.ToLowerInvariant()+'.nupkg')
    if(!(Test-Path -LiteralPath $archive -PathType Leaf)){throw "Missing cached package archive: $key"}
    $package.archiveSha512=(Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant()
    $stream=[IO.File]::OpenRead($archive)
    try {
        $reader=[NuGet.Packaging.PackageArchiveReader]::new($stream)
        try {$package.actualContentHash=$reader.GetContentHash([Threading.CancellationToken]::None,$null)} finally {$reader.Dispose()}
    } finally {$stream.Dispose()}
    if($package.contentHash -and $package.actualContentHash -ne $package.contentHash){$errors.Add("Cached package content differs from lock hash: $key")}
}
$runtimeFeedUri='https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$runtimeFeed=Get-AdvisoryJson $runtimeFeedUri
if($runtimeFeed.data -isnot [Collections.IDictionary] -or !$runtimeFeed.data['latest-runtime'] -or $runtimeFeed.data['channel-version'] -ne '10.0'){throw 'Missing .NET channel metadata.'}
foreach($package in $packages.Values | Where-Object {$_.scope -eq 'published-runtime-pack'}) {
    if($package.version -ne $runtimeFeed.data['latest-runtime']){$errors.Add("Bundled runtime is behind the latest published .NET 10 patch: $($package.id) $($package.version), latest $($runtimeFeed.data['latest-runtime'])")}
}
$indexUri='https://api.nuget.org/v3/vulnerabilities/index.json'
$index=Get-AdvisoryJson $indexUri
$pages=@($index.data)
if(!$index.root.StartsWith('[') -or $pages.Count -lt 1 -or $pages.Count -gt 16){throw 'Invalid vulnerability index; cannot establish advisory coverage.'}
$feedRecords=[Collections.Generic.List[object]]::new()
$findings=[Collections.Generic.List[object]]::new()
foreach($page in $pages) {
    if(!$page['@id'] -or !$page['@updated']){throw 'Incomplete vulnerability index entry.'}
    $data=Get-AdvisoryJson $page['@id']
    if($data.data -isnot [Collections.IDictionary] -and $data.root -ne '[]'){throw 'Invalid advisory page.'}
    $feedRecords.Add(@{url=$page['@id']; updated=$page['@updated']; sha256=$data.sha256})
    foreach($package in $packages.Values) {
        $advisories=if($data.data -is [Collections.IDictionary]){$data.data[$package.id.ToLowerInvariant()]}else{@()}
        foreach($advisory in $advisories) {
            if(!$advisory.versions -or !$advisory.url -or $advisory.severity -notin 0,1,2,3){throw "Invalid advisory for $($package.id)"}
            if([NuGet.Versioning.VersionRange]::Parse($advisory.versions).Satisfies([NuGet.Versioning.NuGetVersion]::Parse($package.version))) {
                $findings.Add(@{package=$package.id; version=$package.version; scope=$package.scope; affectedRange=$advisory.versions; severity=$advisory.severity; url=$advisory.url})
            }
        }
    }
}
$result=[ordered]@{
    schemaVersion=1; verifiedAtUtc=[DateTime]::UtcNow.ToString('O'); success=($errors.Count -eq 0 -and $findings.Count -eq 0)
    scope='All non-project lock entries, including tests/tools and published self-contained runtime packs; published runtime/native/resource and TraceEvent content assets matched against package archive bytes; unaccounted DLL/EXE files rejected.'
    limitations='A current known-advisory check, not proof of future safety. Locked package content hashes and archive-entry bytes establish identity; runtime packs are hashed but their publisher signatures are not independently validated. Does not execute or fuzz native libraries.'
    dotnetChannel=@{url=$runtimeFeedUri; sha256=$runtimeFeed.sha256; latestRuntime=$runtimeFeed.data['latest-runtime']; latestSdk=$runtimeFeed.data['latest-sdk']; sdk=$sdkVersion}
    vulnerabilityIndex=@{url=$indexUri; sha256=$index.sha256}; feeds=@($feedRecords); lockFiles=@($lockFiles | ForEach-Object {@{path=$_.FullName.Substring($projectRoot.Length+1); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
    packages=@($packages.Values | Sort-Object id,version); assets=@($assets | Sort-Object path,package); vulnerabilities=@($findings); errors=@($errors)
}
New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($OutputPath)) -Parent) -Force | Out-Null
[IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath),($result | ConvertTo-Json -Depth 12),[Text.UTF8Encoding]::new($false))
if(!$result.success){throw "Dependency verification failed: $($findings.Count) advisory match(es), $($errors.Count) asset error(s). See $OutputPath"}
Write-Output "PASS: $($lockFiles.Count) lock files, $($packages.Count) package/version pairs, $($assets.Count) published assets, $($pages.Count) advisory pages; no known advisory matches. $OutputPath"
