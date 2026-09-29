[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$output=Join-Path $projectRoot 'dist'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$archive=Join-Path $output 'WindowsOpenIntelligence-source.zip'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream=[IO.File]::Open($archive,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
$count=0
try {
    $roots=@('.github','docs','licenses','scripts','src','tests','tools')
    $files=@('README.md','CONTRIBUTING.md','LICENSE','THIRD-PARTY-NOTICES.md','.gitignore','.gitattributes','Directory.Build.props','global.json','NuGet.Config','WindowsOpenIntelligence.slnx')
    foreach($name in $roots) {
        $folder=Join-Path $projectRoot $name
        foreach($item in Get-ChildItem -LiteralPath $folder -File -Recurse -Force) {
            $relative=$item.FullName.Substring($projectRoot.Length+1)
            if($relative -match '(^|[\\/])(bin|obj)([\\/]|$)' -or $item.Extension -in @('.etl','.etlx','.db','.user')) {continue}
            if($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {throw "Source export refuses redirected files: $relative"}
            $files+=$relative
        }
    }
    foreach($relative in $files | Sort-Object -Unique) {
        $path=[IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
        if(!$path.StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Source path escaped the workspace.'}
        if(!(Test-Path -LiteralPath $path -PathType Leaf)) {throw "Required source file missing: $relative"}
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$path,$relative.Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $count++
    }
} finally {$zip.Dispose();$stream.Dispose()}
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($archive+'.sha256',$hash+'  WindowsOpenIntelligence-source.zip'+[Environment]::NewLine)
Write-Output "Exported $count source files: $archive"
