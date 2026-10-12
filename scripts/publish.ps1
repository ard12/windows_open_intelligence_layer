[CmdletBinding()]param([switch]$NoRestore)
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'package-files.ps1')
$dotnet=Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if(!(Test-Path -LiteralPath $dotnet)) {$dotnet=(Get-Command dotnet -ErrorAction Stop).Source}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$output=Join-Path $projectRoot 'dist\WindowsOpenIntelligence'
$staging=Join-Path $projectRoot ('.artifacts\publish-'+[Guid]::NewGuid().ToString('N'))
Assert-PackagePath $projectRoot $output
Assert-PackagePath $projectRoot $staging
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Push-Location $projectRoot
try {
    foreach($project in @('src/Woi.Worker/Woi.Worker.csproj','src/Woi.Capture/Woi.Capture.csproj','src/Woi.Desktop/Woi.Desktop.csproj')) {
        $restoreArgs=@(if($NoRestore){'--no-restore'}else{'-p:RestoreLockedMode=true'})
        & $dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -p:DebugSymbols=false -p:DebugType=None @restoreArgs -o $staging
        if($LASTEXITCODE -ne 0){throw "Publish failed: $project"}
    }
    Copy-PackageAssets $projectRoot $staging
    foreach($file in Get-ChildItem -LiteralPath $staging -File -Recurse -Force) {
        $relative=$file.FullName.Substring($staging.Length+1)
        if(Test-PackagePrivatePath $relative){throw "Private file reached application staging: $relative"}
    }
    & (Join-Path $PSScriptRoot 'verify-dependencies.ps1') -BundlePath $staging -OutputPath (Join-Path $projectRoot '.artifacts/validation/dependencies.json')
    New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
    if(Test-Path -LiteralPath $output) {
        $backup=Join-Path $projectRoot ('.artifacts\releases\unpacked-'+[Guid]::NewGuid().ToString('N'))
        foreach($target in @($output,$backup)) {
            if(![IO.Path]::GetFullPath($target).StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Publish path escaped workspace.'}
        }
        New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null
        Move-Item -LiteralPath $output -Destination $backup
    }
    if(![IO.Path]::GetFullPath($staging).StartsWith($projectRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Staging path escaped workspace.'}
    Move-Item -LiteralPath $staging -Destination $output
    $archive=Join-Path $projectRoot 'dist\WindowsOpenIntelligence-win-x64.zip'
    Assert-PackagePath $projectRoot $archive
    Assert-PackagePath $projectRoot ($archive+'.sha256')
    Compress-Archive -Path (Join-Path $output '*') -DestinationPath $archive -Force
    $hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($archive+'.sha256'),$hash+'  WindowsOpenIntelligence-win-x64.zip'+[Environment]::NewLine)
    Write-Output $archive
} finally {Pop-Location}
