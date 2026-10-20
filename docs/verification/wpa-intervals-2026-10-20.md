# WPA-inspired timeline and interval comparison: verification

Date: 2026-10-20. Canonical repository: `D:\windows open intelligence`, branch `main`, base commit `5ba9bdfe917ec27726210e14249a5652daa3d2dc`. Existing System Informer, Procmon and PerfView changes were preserved. Working files, source snapshot, builds, caches and evidence are under `J:\Codex\woi-wpa`. No repository relocation, capture, installation, commit, push or release was performed.

## Result

**149 tests passed, zero failed, zero skipped.** Final Release build: **zero warnings/errors**. Native WPF smoke passed on Windows 10 build 19045, including timeline/A-B comparison, CPU stacks, saved event filters/navigation, historical process families and worker recovery.

The scoped implementation is complete. This establishes WOI behavior against its explicit contract, not runtime parity with WPA or acceptance on Windows 11.

## Implemented contract

- Shift-drag on either resource plot stages a common time selection. Zoom / apply commits the interval to both plots, contributors, events and loaded stack/history views. Back retains up to 32 successful prior scopes; Full trace preserves process scope; arrows pan by half the applied width. Native plot pan/zoom is disabled to avoid silently changing chart scope alone.
- A and B are pinned independently from the current edited range. Comparisons require the same active ETL and the same process lifetime, or whole-system scope on both sides. Reimport clears pins, comparison and navigation history.
- Six additive metrics are compared: CPU samples, estimated sampled CPU milliseconds, recorded physical disk bytes, logical file bytes, network send/receive bytes and disk operation count. Totals and rates per selected second are separate. All differences are B minus A; relative change is unavailable for zero/missing A.
- Full-bucket aggregates and exact boundary-event replay produce measurements. Charts remain downsampled one-second display buckets; their plotted points are never summed to calculate exact comparison totals.
- Contributor pages join exact trace-local lifetime IDs and retain activity present on only one side, plus unattributed activity. Names/PIDs do not identify the same lifetime. Unavailable categories remain null; any unknown CPU weight suppresses complete CPU-ms arithmetic. No observations in an available category means recorded zero, not proof of complete provider coverage.
- Open A/B restores the comparison's stored range. Representative-event inspection uses its recorded coordinate and source identity. Page JSON export contains all interval totals, the displayed contributor page, source hash, exact ranges, pagination metadata and limitations.
- Refresh stages every requested response before publishing. Cancellation or a late error leaves the old coherent view. Automatic/lazy tab loads use the applied interval. Explicit Use interval and filter actions retain their documented per-view scope controls.

The existing monitor-attempt comparison and automatic preceding-30-second CPU baseline retain their original meanings.

## Executed evidence

All filenames below are relative to `J:\Codex\woi-wpa\outputs`.

| Check | Result / evidence |
|---|---|
| Release solution build | `build.log`: zero warnings/errors |
| Focused interval tests | `wpa-focused.trx`: 7 passed / 0 failed / 0 skipped |
| Final full suite | `wpa-full.trx`: 149 passed / 0 failed / 0 skipped, approximately one minute |
| Native desktop | `desktop.json`: success and timelineComparisonVerified true; CPU stack, event-filter/navigation, process-history and worker-recovery flags also true |
| Source snapshot | `source-verification-manifest.json`: all 110 source/build-input files hash-match the canonical checkout |
| Patch hygiene | `git diff --check` passed; line-ending normalization warnings only |
| Normal and small layout | `desktop.interval-comparison.png`, `desktop.interval-comparison-small.png`; visual inspection and a minimum contributor-grid-height assertion at 1100 x 720 |
| Selection rendering | `desktop.timeline-selection.png`; visually confirmed the band covers the selected time range on both plots; smoke asserts span X coordinates |
| Export identity | `desktop.interval-comparison.json`; smoke deserializes and checks source hash and exact applied query |

Unit checks cover reversed comparison, unequal durations, identical ranges, fractional half-open edges, clamped durations, known-zero baseline, missing categories, unknown CPU weights, PID/name reuse, unattributed rows, deterministic paging, invalid input and cancellation/retry.

Two real fixtures (`net452/net.4.5.2.x64.etl`, `allocator/SimpleAllocator-dotnet6.etl`) independently enumerate normalized events to reconcile CPU counts/weights, disk/file/network totals and disk operation counts with the comparison. Contributor pages conserve CPU totals; evidence timestamps stay inside their corresponding interval; process-scoped totals match the selected lifetime. This is an independent query/aggregation check over the existing parser, not an independent ETL decoder.

Native checks exercise staged selection without applying, applied-scope tab loading, A/B pinning, cancellation, invalid comparison retention, opening A/B, swapping deltas, evidence inspection, export and reimport reset. A deliberately invalid stack grouping fails late in refresh, after earlier queries succeed; the check confirms old metrics, stack/history objects and applied interval remain intact. Back/pan and both plot limits are checked together. These are in-process native UI checks; physical mouse/keyboard automation was not separately exercised.

The final full run added raw-event assertions for all six metrics after the native desktop run. Application source remained unchanged. No new package was added; restore used locked versions and the existing cache with `--ignore-failed-sources -p:NuGetAudit=false`. Advisory refresh was outside this task.

Fixture SHA-256 hashes remained unchanged:

- net452: `50da7f725f80f88a7622b37f9826844a790cb0c4f64e351fa1f38cf24916889a`
- allocator: `5876415dbc49e6b86bd96906e77a2237deced01a8841684644f3bd303a1cf812`

## Example observed result

The native fixture compared A = `[1.383972128, 4.151916384)` seconds with B = `[5.535888512, 11.071777024)` seconds. A contained 1,569 CPU samples and B 2,972: **+1,403 samples**, but rates were approximately **566.847 versus 536.861 samples/s**, a **−29.986 samples/s** difference. This demonstrates why totals and unequal-duration rates must remain separate. It does not establish equivalent work or a performance improvement.

## Corrections and remaining limits

The first focused run exposed a test-fixture mistake: new lifetime rows had been added to an existing aggregate store without removing its original rows. The test now uses a separate store. Its failed result is retained as `wpa-focused-attempt1.trx`. The first desktop run failed the minimum-size layout assertion; `desktop-attempt1.json` and its small-window screenshot are retained. Layout now gives both tables scrollable space. Visual inspection also caught selection shading on the resource axis; it was corrected to the time axis and received a geometry regression check. An initial build failed on a path-helper namespace mismatch, corrected by reusing the existing core local-path validator.

The independent source review found partial-refresh publication and lazy-tab draft-scope issues. Both were corrected and reviewed again; the native checks above cover them. Sandbox writes to J: required approved host execution; no code workaround was introduced.

Direct WPA runtime/decompilation parity remains unverified: the bounded installation lookup did not identify a usable WPA artifact. The [reference study](../research/wpa-timeline-study-2026-10-20.md) uses Microsoft documentation and labels WOI-specific choices. Windows 11, physical input automation, very large-trace performance, cross-trace matching, work-quantity normalization, CPU-stack diffs and arbitrary WPA tables remain unqualified or outside this implementation. Source-level event loss cannot identify losses in A versus B. No scheduling/wait or causal conclusions are introduced.

Native JSON flags for optional monitoring/investigation scenarios are false because those scenarios were not requested in this run. `cpuStackCoverage` is null at the end because the timeline reimport-reset check clears lazy stack state; CPU stack verification ran and passed earlier in the same smoke.
