[CmdletBinding()]param([switch]$Large)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$fixtureRoot=Join-Path $projectRoot '.artifacts\fixtures'
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$fixtures=@(
    @{Folder='net452'; Archive='net.4.5.2.x64.etl.zip'; Remote='net.4.5.2.x64.etl.zip'; File='net.4.5.2.x64.etl'; Hash='50DA7F725F80F88A7622B37F9826844A790CB0C4F64E351FA1F38CF24916889A'},
    @{Folder='allocator'; Archive='SimpleAllocator-dotnet6.etl.zip'; Remote='AutomatedAnalysis/SimpleAllocator-dotnet6.etl.zip'; File='SimpleAllocator-dotnet6.etl'; Hash='5876415DBC49E6B86BD96906E77A2237DECED01A8841684644F3BD303A1CF812'}
)
if($Large){$fixtures+=@{Folder='net462'; Archive='net.4.6.2.x86.etl.zip'; Remote='net.4.6.2.x86.etl.zip'; File='PerfViewData.etl'; Hash='08AC25A2603A2E63DAC425A1AC3A6CF5885B8EE6C2F6F070A8CC74DF33D8A3F6'}}
foreach($fixture in $fixtures) {
    $folder=Join-Path $fixtureRoot $fixture.Folder
    $file=Join-Path $folder $fixture.File
    if(!(Test-Path -LiteralPath $file)) {
        $archive=Join-Path $fixtureRoot $fixture.Archive
        Invoke-WebRequest -UseBasicParsing -Uri ('https://raw.githubusercontent.com/microsoft/perfview/main/src/TraceEvent/TraceEvent.Tests/inputs/'+$fixture.Remote) -OutFile $archive
        Expand-Archive -LiteralPath $archive -DestinationPath $folder -Force
    }
    if((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $fixture.Hash) {throw "Unexpected fixture fingerprint: $file"}
}
Write-Output $fixtureRoot
