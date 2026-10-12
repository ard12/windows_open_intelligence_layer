[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$fixture=Join-Path $projectRoot ('tests/.source-export-check-'+[Guid]::NewGuid().ToString('N'))
$privateNames=@('saved.woi','saved-upper.WOI','saved-mixed.WoI','.woi-save-controlled.tmp','.woi-save-controlled.tmp-journal','memory.dmp','memory-upper.DMP','memory.mdmp','memory.hdmp','memory.dump','evidence.db','evidence.db-shm','evidence.db-wal','recording.etl','index.etlx')
Add-Type -AssemblyName System.IO.Compression.FileSystem
Push-Location $projectRoot
try {
    $tracked=@(& git ls-files)
    if($LASTEXITCODE -ne 0){throw 'Could not inspect tracked source files.'}
    if($tracked | Where-Object {$_ -match '(?i)(\.(woi|dmp|mdmp|hdmp|dump|etl|etlx|db|db-shm|db-wal)$|(^|/)\.woi-save-)'} ) {
        throw 'Private evidence file is already tracked; ignore rules cannot protect it.'
    }
    New-Item -ItemType Directory -Path $fixture | Out-Null
    foreach($name in $privateNames) {
        $path=Join-Path $fixture $name
        [IO.File]::WriteAllText($path,'synthetic exclusion fixture')
        & git check-ignore -- $path | Out-Null
        if($LASTEXITCODE -ne 0){throw "Git did not ignore $name"}
    }
    [IO.File]::WriteAllText((Join-Path $fixture 'control.md'),'Synthetic public source control.')
    & (Join-Path $PSScriptRoot 'export-source.ps1')
    $zip=[IO.Compression.ZipFile]::OpenRead((Join-Path $projectRoot 'dist/WindowsOpenIntelligence-source.zip'))
    try {
        $entries=@($zip.Entries.FullName)
        if($entries | Where-Object {$_ -match '(?i)(\.(woi|dmp|mdmp|hdmp|dump|etl|etlx|db|db-shm|db-wal)$|(^|/)\.woi-save-)'} ) {
            throw 'Private evidence file reached the source archive.'
        }
        $control=$fixture.Substring($projectRoot.Length+1).Replace('\','/')+'/control.md'
        foreach($required in @($control,'README.md','LICENSE','WindowsOpenIntelligence.slnx','scripts/test-source-export.ps1')) {
            if($required -notin $entries){throw "Source archive is missing $required"}
        }
    } finally {$zip.Dispose()}
} finally {
    Pop-Location
    $resolved=[IO.Path]::GetFullPath($fixture)
    $expected=Join-Path $projectRoot 'tests/.source-export-check-'
    if(!$resolved.StartsWith($expected,[StringComparison]::OrdinalIgnoreCase)){throw 'Fixture cleanup escaped the test directory.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
    # Replace the tested archive so the deliverable contains no synthetic fixture.
    & (Join-Path $PSScriptRoot 'export-source.ps1')
}
Write-Output "Source export passed: $($privateNames.Count) private fixtures ignored and excluded; public source retained."
