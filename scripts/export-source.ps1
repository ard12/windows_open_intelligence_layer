[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'package-files.ps1')
$output=Join-Path $projectRoot 'dist'
Assert-PackagePath $projectRoot $output
New-Item -ItemType Directory -Path $output -Force | Out-Null
$archive=Join-Path $output 'WindowsOpenIntelligence-source.zip'
Assert-PackagePath $projectRoot $archive
Assert-PackagePath $projectRoot ($archive+'.sha256')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$stream=[IO.File]::Open($archive,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
$zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create)
$count=0
try {
    $roots=@('.github','docs','licenses','scripts','src','tests','tools')
    $required=@('README.md','CONTRIBUTING.md','LICENSE','THIRD-PARTY-NOTICES.md','.gitignore','.gitattributes','Directory.Build.props','global.json','NuGet.Config','WindowsOpenIntelligence.slnx','scripts/package-files.ps1','docs/verification/dependencies-2026-10-13.md')
    foreach($relative in Get-PackageFiles $projectRoot $roots $required -IncludeUntrackedBuildFiles) {
        $path=[IO.Path]::GetFullPath((Join-Path $projectRoot $relative))
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$path,$relative.Replace('\','/'),[IO.Compression.CompressionLevel]::Optimal) | Out-Null
        $count++
    }
} finally {$zip.Dispose();$stream.Dispose()}
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($archive+'.sha256',$hash+'  WindowsOpenIntelligence-source.zip'+[Environment]::NewLine)
Write-Output "Exported $count source files: $archive"
