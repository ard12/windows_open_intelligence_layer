[CmdletBinding()]param(
    [string]$OutputDirectory = '.artifacts/validation/native-capture-matrix',
    [ValidateSet('Resources','FileActivity')][string[]]$Profiles = @('Resources','FileActivity')
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
if (!$output.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Validation output must stay in the workspace.' }
$dotnet = Join-Path $root '.tools/dotnet/dotnet.exe'
if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$validator = Join-Path $root 'tools/Woi.Validation/bin/Release/net10.0-windows/Woi.Validation.dll'
$helper = Join-Path $root 'dist/WindowsOpenIntelligence/Woi.Capture.exe'
$workload = Join-Path $root '.artifacts/workload/Woi.Workload.exe'
foreach ($path in @($validator,$helper,$workload)) { if (!(Test-Path -LiteralPath $path)) { throw "Build and publish before validation: $path" } }
[IO.Directory]::CreateDirectory($output) | Out-Null
$results = [Collections.Generic.List[object]]::new()
foreach ($profile in $Profiles) {
    $caseDirectory = Join-Path $output ($profile + '-' + [Guid]::NewGuid().ToString('N'))
    & $dotnet $validator --capture $caseDirectory $helper $workload $profile
    $exitCode = $LASTEXITCODE
    $results.Add([pscustomobject]@{ profile=$profile; directory=$caseDirectory; exitCode=$exitCode; utc=[DateTime]::UtcNow; passed=$exitCode -eq 0 })
    $results | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'matrix.json') -Encoding utf8
    if ($exitCode -ne 0) { throw "Native $profile validation failed; results are preserved in $caseDirectory. Resolve this failure before retrying or requesting another elevation." }
}
$results | ConvertTo-Json -Depth 5
