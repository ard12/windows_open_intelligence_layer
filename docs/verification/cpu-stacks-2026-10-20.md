# CPU stack aggregation: executed verification

Implemented locally in `D:\windows open intelligence` on `main`, based on `5ba9bdfe917ec27726210e14249a5652daa3d2dc`. Existing process-history and saved-filter changes were preserved. Following the request to use J:, research, build intermediates, isolated desktop cache and verification outputs are under `J:\Codex\woi-perfview`. The canonical repository was not relocated. No commit, push or release was made.

## Result

**142 tests passed, zero failed, zero skipped.** The final Release solution build passed with zero warnings/errors. Native desktop verification passed, including CPU stack views, existing event filters, event navigation, historical process families and worker recovery. Normal and minimum-window screenshots were inspected. This qualifies the implemented WOI behavior; it does not establish PerfView GUI parity or capture completeness.

## Implemented behavior

- The CPU stacks tab offers function/module summaries, contextual call trees, separate caller/callee navigation, paging and representative-event inspection. It uses the existing interval and trace-local process lifetime; Explorer display filters do not alter stack totals.
- Inclusive flat counts credit each distinct frame once per sample; exclusive counts credit the captured leaf. Trees retain recursive positions. Caller/callee navigation uses the deepest focus occurrence, matching the separate tree behavior studied in PerfView.
- Sample counts and known CPU millisecond weights remain separate. Unknown weights make the complete millisecond total unavailable while retaining the known subtotal. Percentages use all selected sample counts, not utilization or weighted CPU percentages.
- An explicit no-captured-stack accounting row preserves exclusive totals. Coverage reports missing stacks, unresolved frames, unknown weights and depth truncation. Captured stack absence is not filled with an invented observed stack.
- Failed/cancelled requests retain the displayed result. New imports and symbol-resolution attempts invalidate source-local frame keys. Evidence and local symbol inspection use the selected stack sample's exact event coordinate.
- Aggregation fails explicitly beyond 1,000,000 selected samples or 100,000 distinct rows; frames are capped at 256 with an explicit older-frame marker. Navigation rescans the selection rather than retaining an unbounded stack cache.

## Executed checks

| Check | Evidence/result |
|---|---|
| Focused CPU stack tests | `cpu-stacks-focused.trx`: 6 passed / 0 failed / 0 skipped |
| Full suite | `cpu-stacks-full.trx`: 142 passed / 0 failed / 0 skipped; approximately 81 seconds |
| Final Release build | `dotnet build WindowsOpenIntelligence.slnx -c Release --no-restore`, from the J: source snapshot; zero warnings/errors |
| Controlled reference aggregation | Installed TraceEvent 3.2.8 `InternStackSource` and `CallTree`: recursive stacks compared per frame for inclusive/exclusive counts and weights |
| Two real ETL fixtures | Raw captured-stack presence, normalized CPU weights, resource contributor reconciliation, exclusive totals across pages, exact evidence identity, process scope, cancellation/retry and unchanged source hashes |
| Real reference comparison | TraceEvent's `TraceEventStackSource` and `CallTree`: root sample counts and captured-leaf exclusive counts for the selected captured-stack, non-depth-truncated subset. This is not real-ETL per-frame or millisecond parity |
| Tree semantics | Recursive flat deduplication, contextual paths, deepest-focus callers/callees, path conservation, missing-stack accounting and module grouping |
| Input and boundaries | Half-open intervals, trace-local process identity, paging, unknown weights, unresolved/depth-truncated input, invalid queries and cancellation |
| Native desktop | `desktop-final.json`: success, cpuStacksVerified, eventFiltersVerified, eventNavigationVerified, processHistoryVerified and workerRecoveryVerified all true |
| CPU desktop workflow | Load, paging reversal, call-tree descend/up, caller/callee focus totals, cancellation without deferred restart, evidence source hash, invalid symbol-attempt invalidation/reload, module grouping and reimport while the CPU tab is selected |
| Layout | `desktop-final.cpu-stacks.png` and `desktop-final.cpu-stacks-small.png`; header scrolls at 1100 x 720 and stack rows remain visible |
| Snapshot identity | `source-verification-manifest.json`: 106 source/build-input files hash-match the canonical repository |
| Patch hygiene | `git diff --check` passed; line-ending normalization warnings only |

All named evidence files are in `J:\Codex\woi-perfview\outputs`. Builds used the existing .NET SDK 10.0.401 and locked dependency versions; no new dependency was introduced. Restore used the existing package cache with `--locked-mode --ignore-failed-sources -p:NuGetAudit=false`; vulnerability-advisory refresh was not performed. The final build/native smoke followed the full suite after changes limited to desktop smoke coverage reporting and minimum-window layout; core and test sources were unchanged.

The native smoke's false monitor, investigation, saved-comparison and saved-only-startup flags identify optional scenarios not requested in this run, not failed checks. Test caches and saved-filter files were isolated on J:.

## Observed fixture coverage

For the native net452 fixture at full scope:

| Measurement | Result |
|---|---:|
| Selected CPU samples | 6,320 |
| Captured-stack samples | 6,318 |
| No-captured-stack samples | 2 |
| Samples with known weights | 6,311 |
| Unknown-weight samples | 9 |
| Known CPU weight subtotal | 6,214.6988 ms |
| Complete CPU millisecond total | Unavailable because 9 weights are unknown |
| Samples containing at least one unresolved frame | 6,318 |
| Depth-truncated samples | 0 |
| Function/accounting rows | 5,978 |

An unresolved frame somewhere in a chain does not mean every frame is unresolved. These are sampled observations, not exact CPU duration or proof that all work was captured.

Original fixture SHA-256 hashes remained unchanged:

- net452: `50da7f725f80f88a7622b37f9826844a790cb0c4f64e351fa1f38cf24916889a`
- allocator: `5876415dbc49e6b86bd96906e77a2237deced01a8841684644f3bd303a1cf812`

## Retained failed attempts and limits

The first native smoke (`desktop.json`) failed the minimum-window grid-height assertion after earlier CPU functional checks passed. Its screenshots are retained. Bounding the header's scroll area fixed that failure; the final complete smoke passed. An initial focused build reported xUnit2031; the assertion was corrected and the final build is warning-free.

Sandbox writes to J: build outputs and native directory-handle checks encountered access-denied errors. Approved host execution succeeded without a code workaround. Low free space on D: motivated building from the hash-matched J: snapshot; no cleanup or relocation was performed by this task.

Unverified or deliberately deferred: PerfView GUI execution, Windows 11 qualification, successful matching-PDB resolution, automatic symbol downloads, maximum-size performance, arbitrary regex/fold/group expressions, baseline comparison and new capture profiles. No collector was run or changed. The existing captures must actually contain stacks for stack-based conclusions. No aggregate can recover uncaptured callers or establish scheduling/wait causes from sampled CPU alone.

See the [pinned PerfView source study](../research/perfview-cpu-stack-study-2026-10-20.md) for source links, observed reference semantics and intentional differences.
