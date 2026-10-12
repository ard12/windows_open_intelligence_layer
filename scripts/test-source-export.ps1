[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'package-files.ps1')
$fixture=Join-Path $projectRoot ('.artifacts/source-export-check-'+[Guid]::NewGuid().ToString('N'))
$privateNames=@('saved.woi','saved-upper.WOI','saved-mixed.WoI','.woi-save-controlled.tmp','.woi-save-controlled.tmp-journal','memory.dmp','memory-upper.DMP','memory.mdmp','memory.hdmp','memory.dump','evidence.db','evidence.db-shm','evidence.db-wal','recording.etl','index.etlx','evidence.sqlite3','evidence.sqlite-wal','.env','.env.production','private.key','private.pem','private.pfx','private.p12','private.ppk','id_rsa','id_ed25519','credentials.json','secrets.json','private-upper.PEM','recording-upper.ETL','evidence-upper.DB-WAL','.ENV.production','Credentials.json')
$privateNames+='developer.pdb'
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    Assert-PackagePath $projectRoot $fixture
    New-Item -ItemType Directory -Path $fixture | Out-Null
    foreach($folder in @('.github','docs','licenses','scripts','src','tests','tools')) {
        New-Item -ItemType Directory -Path (Join-Path $fixture $folder) | Out-Null
    }
    foreach($name in @('README.md','CONTRIBUTING.md','LICENSE','THIRD-PARTY-NOTICES.md','.gitignore','.gitattributes','Directory.Build.props','global.json','NuGet.Config','WindowsOpenIntelligence.slnx')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination (Join-Path $fixture $name)
    }
    foreach($name in @('package-files.ps1','export-source.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $fixture ('scripts/'+$name))
    }
    New-Item -ItemType Directory -Path (Join-Path $fixture 'docs/verification') | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/verification/dependencies-2026-10-13.md') -Destination (Join-Path $fixture 'docs/verification')
    foreach($folder in @('docs','licenses')) {
        foreach($name in $privateNames) {
            [IO.File]::WriteAllText((Join-Path $fixture ($folder+'/'+$name)),'synthetic exclusion fixture')
        }
        [IO.File]::WriteAllText((Join-Path $fixture ($folder+'/control.md')),'Synthetic public control.')
    }
    & git -C $fixture init --quiet
    if($LASTEXITCODE -ne 0){throw 'Could not initialize isolated packaging fixture.'}
    foreach($name in $privateNames) {
        & git -C $fixture check-ignore -- ('docs/'+$name) | Out-Null
        if($LASTEXITCODE -ne 0){throw "Git did not ignore $name"}
    }
    # Tracked dummy secrets prove the policy works independently of Git ignore rules.
    & git -C $fixture add --force -- .
    if($LASTEXITCODE -ne 0){throw 'Could not index isolated packaging fixture.'}
    [IO.File]::WriteAllText((Join-Path $fixture 'docs/untracked-note.md'),'Untracked personal note.')
    [IO.File]::WriteAllText((Join-Path $fixture 'src/NewCode.cs'),'// New review-checkout source must be retained.')
    [IO.File]::WriteAllText((Join-Path $fixture 'src/.env.local'),'synthetic secret')
    & (Join-Path $fixture 'scripts/export-source.ps1')
    $zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $fixture 'dist/WindowsOpenIntelligence-source.zip'))
    try {
        $entries=@($zip.Entries.FullName)
        foreach($entry in $entries) {
            if(Test-PackagePrivatePath $entry){throw "Private file reached source ZIP: $entry"}
        }
        foreach($required in @('README.md','LICENSE','WindowsOpenIntelligence.slnx','scripts/package-files.ps1','docs/control.md','licenses/control.md','src/NewCode.cs')) {
            if($required -notin $entries){throw "Source archive is missing $required"}
        }
        if('docs/untracked-note.md' -in $entries){throw 'Untracked personal document reached source ZIP.'}
    } finally {$zip.Dispose()}
    $assets=Join-Path $fixture '.artifacts/app-assets'
    Copy-PackageAssets $fixture $assets
    foreach($item in Get-ChildItem -LiteralPath $assets -File -Recurse -Force) {
        if(Test-PackagePrivatePath $item.FullName.Substring($assets.Length+1)){throw 'Private file reached application assets.'}
    }
    foreach($required in @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','docs/control.md','licenses/control.md')) {
        if(!(Test-Path -LiteralPath (Join-Path $assets $required))){throw "Application assets missing $required"}
    }
    if(Test-Path -LiteralPath (Join-Path $assets 'docs/untracked-note.md')){throw 'Untracked personal document reached application assets.'}

    $extracted=Join-Path $fixture '.artifacts/extracted'
    [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $fixture 'dist/WindowsOpenIntelligence-source.zip'),$extracted)
    foreach($folder in @('.github','tests','tools')) {
        New-Item -ItemType Directory -Path (Join-Path $extracted $folder) -Force | Out-Null
    }
    foreach($name in $privateNames) {
        [IO.File]::WriteAllText((Join-Path $extracted ('docs/'+$name)),'synthetic extracted-tree exclusion fixture')
    }
    & (Join-Path $extracted 'scripts/export-source.ps1')
    $fallbackZip=[IO.Compression.ZipFile]::OpenRead((Join-Path $extracted 'dist/WindowsOpenIntelligence-source.zip'))
    try {
        foreach($entry in $fallbackZip.Entries) {
            if(Test-PackagePrivatePath $entry.FullName){throw 'Private file reached source ZIP without Git metadata.'}
        }
        if('src/NewCode.cs' -notin @($fallbackZip.Entries.FullName)){throw 'Extracted source ZIP lost new code.'}
    } finally {$fallbackZip.Dispose()}
    Copy-PackageAssets $extracted (Join-Path $extracted '.artifacts/app-assets')
    foreach($item in Get-ChildItem -LiteralPath (Join-Path $extracted '.artifacts/app-assets') -File -Recurse -Force) {
        if(Test-PackagePrivatePath $item.Name){throw 'Private file reached application assets without Git metadata.'}
    }

    $redirect=Join-Path $fixture 'docs/redirected'
    $redirectTarget=Join-Path $fixture '.artifacts/redirect-target'
    New-Item -ItemType Directory -Path $redirectTarget -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $redirectTarget 'control.md'),'Redirect target must not be packaged.')
    New-Item -ItemType Junction -Path $redirect -Value $redirectTarget | Out-Null
    try {
        foreach($operation in @('source','assets')) {
            $rejected=$false
            try {
                if($operation -eq 'source'){Assert-PackagePath $fixture (Join-Path $redirect 'control.md')}
                else{Copy-PackageAssets $fixture $redirect}
            } catch {
                if($_.Exception.Message -notlike '*refuses redirected paths*'){throw}
                $rejected=$true
            }
            if(!$rejected){throw "Redirected $operation path was accepted."}
        }
    } finally {[IO.Directory]::Delete($redirect)}
} finally {
    $resolved=[IO.Path]::GetFullPath($fixture)
    $expected=Join-Path $projectRoot '.artifacts/source-export-check-'
    if(!$resolved.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture cleanup escaped the artifact directory.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
Write-Output "Packaging privacy passed: $($privateNames.Count) private filenames excluded from source ZIP and application assets; new code retained, untracked docs omitted, redirected paths rejected."
