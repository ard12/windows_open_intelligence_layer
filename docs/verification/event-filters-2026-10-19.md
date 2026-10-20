# Saved event filters and navigation: executed verification

Implemented locally in `D:\windows open intelligence` on `main`, based on `5ba9bdfe917ec27726210e14249a5652daa3d2dc`. The historical process-family changes already in the working tree were preserved. No commit, push or release was made.

## Result

Release build passed with zero warnings/errors. **136 tests passed, zero failed, zero skipped**, including real ETL fixtures. Native WPF smoke passed on Windows 10 build 19045, with a second visual check at the supported minimum window size of 1100 × 720. Original fixture hashes remained unchanged. Windows 11 and current Procmon runtime parity remain unverified.

## What changed

- Named event filter sets can be saved, replaced by name, loaded and deleted. They survive a new desktop view model without launching the analysis worker. Presets contain quick filters and rules, excluding interval, raw PID and trace-local lifetime identity.
- ProcessName, Category, Operation and Target support case-insensitive literal Equals, Contains, StartsWith and EndsWith. Includes use OR within a field and AND across fields; any exclusion wins. This is WOI's explicit matching contract.
- Row menus derive include/exclude rules from recorded attributes, inspect captured evidence, and navigate to the recorded process family. Name filters cover matching names across lifetimes; family navigation uses exact trace-local identity.
- Apply / first page captures the draft query. Previous/Next use the applied query, with reversible exclusive-cursor history. Failed/cancelled page requests preserve the current rows and cursor state. A retry does not skip records.
- Rules are structurally compared after JSON transport, retaining the existing forward iterator/lookahead optimization. Refresh captures the event scope before asynchronous metric queries. Busy/failed/cancelled inspection cannot switch to unrelated old evidence.
- The saved-filter file is versioned, bounded to 32 presets / 32 rules per preset / 512-character values / 1 MiB, and replaced through a temporary file. Unknown versions, missing required rule fields and damaged JSON fail without overwriting the existing file.
- Event filters affect only Explorer. Resource rankings, interval reports, ETL bytes, collection and process-history totals retain their existing semantics.

## Executed checks

| Check | Evidence/result |
|---|---|
| Release solution build | `.tools\dotnet\dotnet.exe build WindowsOpenIntelligence.slnx -c Release --no-restore`; zero warnings/errors |
| Complete test suite | `.artifacts/procmon-verification/procmon-filters-native.trx`: 136 passed / 0 failed / 0 skipped; about 38 seconds |
| Matcher and input validation | OR/AND grouping, exclusion order independence, all four operators, case handling, unknown enums, null rules, oversized lists/values, structurally equivalent JSON requests |
| Saved filters | Save/reopen/replace/delete; corrupt JSON, unsupported version, null presets and missing required rule members preserve original file bytes |
| Real ETL paging | Complete filtered event-ID sequence reconciled with an independently selected normalized event sequence; >500 matches; replay/next-page lookahead reuse; cancellation reset and retry; exclusion contradiction returns empty |
| Native desktop | `.artifacts/procmon-verification/desktop-complete.json`: success, eventFiltersVerified, eventNavigationVerified, processHistoryVerified, workerRecoveryVerified all true |
| Desktop workflow | Draft edits do not change Next; Previous reproduces original coordinates; applying a changed filter resets navigation; cancellation keeps current page; retry returns expected page; save/reopen/load/delete; row-derived exclusion overrides include; invalid rule leaves displayed events; evidence retains source hash |
| Historical family regression | Deferred import/reopen, error and cancellation handling, totals reconciliation, captured parent/child family navigation all passed in the same smoke |
| Visual layout | Inspected `desktop-complete.filters.png` and `desktop-complete.filters-small.png`; filter controls scroll at minimum size while event rows and navigation remain visible |
| Source preservation | net452 SHA-256 `50da7f725f80f88a7622b37f9826844a790cb0c4f64e351fa1f38cf24916889a`; allocator SHA-256 `5876415dbc49e6b86bd96906e77a2237deced01a8841684644f3bd303a1cf812` |

The final desktop rebuild passed after the full suite; the complete native smoke was repeated against that rebuilt desktop, including an assertion that global scope controls are disabled while busy and contributor selection cannot change scope mid-query. The smoke uses an isolated preset file alongside its screenshots, leaving the user's saved presets untouched. Its false monitor/investigation flags mean those optional scenarios were not requested for this run; they are not failures.

## Failed intermediate attempts retained

1. `procmon-filters.trx`: 128 passed / 8 failed. D: had roughly 1.8 GiB available, below the existing recorder's 2 GiB start threshold; capture tests failed before their intended scenarios, and one cleanup reported a leased directory. No storage guard was weakened and no existing data was deleted.
2. `procmon-filters-final.trx`: 123 passed / 13 failed. Moving test scratch to C: resolved free-space pressure, but sandboxed Windows directory-handle checks returned Access denied. The unchanged suite passed outside the sandbox with isolated C: scratch (`work/test-temp-native`). This was ordinary host execution, not an elevated WPR recording.
3. `desktop-final.json` and `desktop-verified.json`: minimum-window layout assertions failed. The first fix constrained only the advanced editor and was insufficient. Bounding the entire filter-control area and folding the lifetime chart while editing fixed it; `desktop-complete.json` is the passing final run. Failed screenshots remain in the local verification directory.

## Scope limits

The [Procmon study](../research/process-monitor-study-2026-10-19.md) used Microsoft documentation and static shipped-resource inventory through REA. It did not execute Procmon, compare its current rule engine, load its driver, or copy its implementation. Current exclude precedence is not claimed as observed Procmon behavior. WOI has no PMF/PML compatibility, arbitrary payload filtering, individual rule enable/disable, highlighting/find-next, or destructive capture filtering in this slice.

Presets are local preferences and can contain sensitive names/paths. The file does not synchronize concurrent application instances. Elevated collection and native Windows 11 release acceptance are separate existing gates. Next planned research remains PerfView CPU-stack aggregation, followed by WPA wait/scheduling evidence; neither is implemented by this change.
