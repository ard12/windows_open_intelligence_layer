[CmdletBinding()]
param(
 [Parameter(Mandatory)][string]$Fixture,
 [Parameter(Mandatory)][string]$OutputRoot,
 [Parameter(Mandatory)][string]$DotnetPath,
 [Parameter(Mandatory)][string]$PackageCache,
 [ValidateSet('false','true')][string]$Switch='true',
 [ValidateSet('ordinary','held')][string]$Phase='ordinary',
 [switch]$NoBuild
)
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot)
$application=Join-Path $OutputRoot 'application'
$run=Join-Path $OutputRoot ($Switch+'-'+$Phase+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $application,$run | Out-Null
$env:DOTNET_ROOT=Split-Path $DotnetPath
$env:DOTNET_CLI_HOME=Join-Path $OutputRoot 'dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:TEMP=$OutputRoot;$env:TMP=$OutputRoot;$env:NUGET_PACKAGES=$PackageCache
if(!$NoBuild) {
 foreach($project in @('src/Woi.Worker/Woi.Worker.csproj','src/Woi.Desktop/Woi.Desktop.csproj','tools/Woi.AutomationRegression/Woi.AutomationRegression.csproj')) {
  & $DotnetPath publish (Join-Path $repo $project) -c Release -r win-x64 --self-contained false --artifacts-path (Join-Path $OutputRoot 'build') --source $PackageCache -p:RestoreLockedMode=true "-p:ApplicationDirectory=$application" -o $application
  if($LASTEXITCODE -ne 0){throw "Build failed: $project"}
 }
}
$config=Get-Content (Join-Path $application 'WindowsOpenIntelligence.runtimeconfig.json') -Raw|ConvertFrom-Json
if($config.runtimeOptions.configProperties.'Switch.System.Windows.Automation.Peers.ItemAutomationPeerKeepsItsItemAlive' -ne $true){throw 'Packaged desktop startup mitigation is not configured.'}
& $DotnetPath (Join-Path $application 'Woi.AutomationRegression.dll') --verify-memory-guard (Join-Path $run 'memory-guard.json')
if($LASTEXITCODE -ne 0){throw 'Missing-monitor/identity negative controls failed.'}
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$output=Join-Path $run 'result.json'
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $application 'Woi.AutomationRegression.exe'))
foreach($argument in @([IO.Path]::GetFullPath($Fixture),$output,$Switch,$Phase)){$start.ArgumentList.Add($argument)}
$start.UseShellExecute=$false;$start.CreateNoWindow=$true
$app=[Diagnostics.Process]::Start($start)
$handler=[System.Windows.Automation.AutomationEventHandler]{param($sender,$eventArgs)}
try {
 for($i=0;$i -lt 100 -and !(Test-Path "$output.ready") -and !$app.HasExited;$i++){Start-Sleep -Milliseconds 100}
 if($app.HasExited){throw "Harness exited before client attach: $($app.ExitCode)"}
 $condition=[System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$app.Id)
 $window=[System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$condition)
 if(!$window){throw 'Owned regression window not found.'}
 [System.Windows.Automation.Automation]::AddAutomationEventHandler([System.Windows.Automation.SelectionItemPattern]::ElementSelectedEvent,$window,[System.Windows.Automation.TreeScope]::Subtree,$handler)
 $elements=$window.FindAll([System.Windows.Automation.TreeScope]::Descendants,[System.Windows.Automation.Condition]::TrueCondition)
 $elements=$null
 'go'|Set-Content "$output.go"
 for($i=0;$i -lt 900 -and !$app.HasExited;$i++){
  if(Test-Path "$output.detach") { [System.Windows.Automation.Automation]::RemoveAllEventHandlers(); $window=$null; 'detached'|Set-Content "$output.detached" }
  Start-Sleep -Milliseconds 200
 }
 if(!$app.HasExited){$app.Kill();throw 'Regression timed out.'}
 $result=Get-Content $output -Raw|ConvertFrom-Json
 if($result.error -or $result.completed -ne 20 -or $result.stage -ne 'complete' -or !$result.listener -or $result.effective -ne [bool]::Parse($Switch)){throw "Native regression failed; inspect $output"}
 $active=@($result.samples|Where-Object monitoringExpected)
 if(!$active.Count -or @($active|Where-Object {$null -eq $_.monitorWorkingSet -or $null -eq $_.uiPlusMonitorWorkingSet}).Count){throw "Active owned-monitor measurements unavailable; memory gate unknown. Inspect $output"}
 $peak=($active|Measure-Object uiPlusMonitorWorkingSet -Maximum).Maximum
 if($peak -ge 300MB){throw "Unchanged300MiB UI+monitor memory gate failed; inspect $output"}
 [pscustomobject]@{result=$output;effectiveSwitch=$result.effective;sampledPeakUiPlusMonitorMiB=$peak/1MB;stableAcceptance='OPEN'}
} finally { [System.Windows.Automation.Automation]::RemoveAllEventHandlers(); if(!$app.HasExited){$app.Kill()} }
