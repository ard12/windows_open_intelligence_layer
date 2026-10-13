$ErrorActionPreference='Stop'

function Test-PackagePrivatePath([string]$RelativePath) {
    $path=$RelativePath.Replace('\','/')
    return $path -match '(?i)(^|/)(bin|obj|\.git|\.tools|\.artifacts|dist)(/|$)' -or
        $path -match '(?i)(^|/)(\.env(?:\..*)?|\.woi-save-.*|id_(rsa|dsa|ecdsa|ed25519)(?:\.pub)?|(credentials|secrets)\.json)$' -or
        $path -match '(?i)\.(woi|etl|etlx|db|sqlite|sqlite3)(-(shm|wal|journal))?$|\.(user|pdb|dmp|mdmp|hdmp|dump|key|pem|pfx|p12|ppk|kdbx|keystore|jks)$'
}

function Assert-PackagePath([string]$ProjectRoot,[string]$Path) {
    $root=[IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $full=[IO.Path]::GetFullPath($Path)
    if($full -ne $root -and !$full.StartsWith($root+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package path escaped the workspace.'
    }
    # Check ancestors too: a regular file can sit inside a redirected directory.
    for($current=$full;$current;$current=[IO.Path]::GetDirectoryName($current)) {
        if(Test-Path -LiteralPath $current) {
            if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Packaging refuses redirected paths: $current"
            }
        }
    }
}

function Get-PackageFiles([string]$ProjectRoot,[string[]]$Roots,[string[]]$RequiredFiles,[switch]$IncludeUntrackedBuildFiles) {
    Assert-PackagePath $ProjectRoot $ProjectRoot
    foreach($name in $Roots){Assert-PackagePath $ProjectRoot (Join-Path $ProjectRoot $name)}
    $files=@($RequiredFiles)
    if(Test-Path -LiteralPath (Join-Path $ProjectRoot '.git')) {
        $tracked=@(& git -C $ProjectRoot -c core.quotepath=false ls-files --cached)
        if($LASTEXITCODE -ne 0){throw 'Could not inspect tracked package files.'}
        $files+=@($tracked | Where-Object {
            $relative=$_.Replace('\','/')
            @($Roots | Where-Object {$relative.StartsWith($_+'/',[StringComparison]::OrdinalIgnoreCase)}).Count -gt 0
        })
        if($IncludeUntrackedBuildFiles) {
            $untracked=@(& git -C $ProjectRoot -c core.quotepath=false ls-files --others --exclude-standard)
            if($LASTEXITCODE -ne 0){throw 'Could not inspect new source files.'}
            # Keep review-checkout code buildable without including untracked personal docs.
            $files+=@($untracked | Where-Object {$_ -match '^(src|tests|tools|scripts)/.*\.(cs|xaml|csproj|json|ps1|cjs|props|targets|config|manifest)$'})
        }
    } else {
        # Source ZIPs have no Git metadata; retain the same fixed roots and privacy policy.
        foreach($name in $Roots) {
            $folder=Join-Path $ProjectRoot $name
            Assert-PackagePath $ProjectRoot $folder
            $pending=[Collections.Generic.Stack[string]]::new()
            $pending.Push($folder)
            while($pending.Count) {
                foreach($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
                    Assert-PackagePath $ProjectRoot $item.FullName
                    $relative=$item.FullName.Substring($ProjectRoot.Length+1)
                    if(Test-PackagePrivatePath $relative){continue}
                    if($item.PSIsContainer){$pending.Push($item.FullName)}else{$files+=$relative}
                }
            }
        }
    }
    foreach($relative in $files | Sort-Object -Unique) {
        $path=Join-Path $ProjectRoot $relative
        Assert-PackagePath $ProjectRoot $path
        if(Test-PackagePrivatePath $relative){continue}
        if(!(Test-Path -LiteralPath $path -PathType Leaf)){throw "Required package file missing: $relative"}
        $relative.Replace('\','/')
    }
}

function Copy-PackageAssets([string]$ProjectRoot,[string]$Destination) {
    Assert-PackagePath $ProjectRoot $Destination
    $files=@(Get-PackageFiles $ProjectRoot @('docs','licenses') @('README.md','LICENSE','THIRD-PARTY-NOTICES.md','docs/verification/dependencies-2026-10-13.md'))
    foreach($relative in $files) {
        $target=Join-Path $Destination $relative
        Assert-PackagePath $ProjectRoot $target
        New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $ProjectRoot $relative) -Destination $target -Force
    }
}
