[CmdletBinding()]param()
$ErrorActionPreference = 'Stop'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'monitor-stress.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Monitor stress script failed to parse.' }
$guards = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq '$enoughTicks'
}, $true))
if ($guards.Count -ne 1) { throw 'Expected one monitor coverage guard.' }
# Exercise the actual reporting predicate without waiting hours or sampling this machine.
$guard = [scriptblock]::Create($guards[0].Right.Extent.Text)
foreach ($case in @(
    @{ Seconds=10; Snapshots=7; Pass=$false },
    @{ Seconds=10; Snapshots=8; Pass=$true },
    @{ Seconds=120; Snapshots=95; Pass=$false },
    @{ Seconds=120; Snapshots=96; Pass=$true },
    @{ Seconds=121; Snapshots=0; Pass=$false },
    @{ Seconds=121; Snapshots=95; Pass=$false },
    @{ Seconds=121; Snapshots=96; Pass=$true },
    @{ Seconds=7200; Snapshots=3600; Pass=$false },
    @{ Seconds=7200; Snapshots=5759; Pass=$false },
    @{ Seconds=7200; Snapshots=5760; Pass=$true }
)) {
    $Seconds = $case.Seconds
    $result = [pscustomobject]@{ measuredSnapshots=$case.Snapshots }
    if ((& $guard) -ne $case.Pass) {
        throw "Incorrect monitor coverage result: $Seconds seconds, $($case.Snapshots) measured snapshots."
    }
}
Write-Output 'PASS: monitor coverage boundaries, long-run starvation and cumulative counts.'
