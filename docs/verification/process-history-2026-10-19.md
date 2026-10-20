# Historical process-family verification — 19 October 2026

Base source: `5ba9bdfe917ec27726210e14249a5652daa3d2dc`, plus the uncommitted historical-process-family change. Local Windows build `10.0.19045.0`, .NET SDK `10.0.401`. No push, release, installer update or elevated trace capture was performed.

## Executed checks

| Check | Result | Evidence |
|---|---|---|
| Release solution build | Passed, zero warnings/errors | Local `dotnet build WindowsOpenIntelligence.slnx -c Release --no-restore` |
| Focused family/core and real-ETL worker tests | **7 passed, 0 failed, 0 skipped** | `.artifacts/process-history-validation/process-history-focused-retry.trx` |
| Complete existing test suite including new tests | **132 passed, 0 failed, 0 skipped** | `.artifacts/process-history-validation/process-history-full.trx` |
| Final desktop build after UI-only cancellation and display refinements | Passed, zero warnings/errors | Local `dotnet build src/Woi.Desktop/Woi.Desktop.csproj -c Release --no-restore` |
| Final desktop smoke using net452 public fixture | Passed, exit code 0 | `.artifacts/process-history-validation/desktop-accepted.json` |
| Rendered family view | Visually inspected; real `cmd → Test.x64` relationship and child exit shown | `.artifacts/process-history-validation/desktop-accepted.history.family.png` |
| Whitespace/diff validation | `git diff --check` passed | Git emits existing CRLF normalization notices, not whitespace errors |

The final desktop smoke explicitly passed: lazy worker startup, worker-crash recovery, selecting history during import, reopening while history remained selected, family totals reconciliation, focus/all-family navigation, a real captured parent/child link, preservation of a failed operation's error, and cancellation without deferred work restarting. It produced 500 event rows and 20 contributor rows from 179,474 indexed events.

The six controlled core tests cover: exited parents with surviving children; reused PIDs; missing/newer/self/cyclic parents; unknown start/exit boundaries; family totals independent of pages; exact partial-interval edges; unavailable weights/byte metrics; a 10,000-level family using iterative traversal; empty input; bad limits/offsets/IDs/intervals; and cancellation. The seventh test exercises real-ETL paging and entity lookup through the worker, plus cache reopening.

The full suite ran before the last UI-only refinements. Core, worker and test code were unchanged afterward; the final desktop build and targeted desktop smoke verified those UI changes. The broader suite was not rerun merely to repeat the same core checks.

## Retained failure and correction

The first focused run passed six tests and failed the new real-ETL total comparison. It expected 5,316 samples, while captured families accounted for 5,129. The remaining 187 were unattributed activity, which cannot be assigned to a family. The implementation now returns separate whole-interval `unattributedTotals`, displays the unattributed CPU count and verifies attributed + unattributed reconciliation. The initial result is preserved in `process-history-focused.trx`; it was not overwritten by the passing retry.

Source review also found two UI issues before completion: initial history loads could be dropped while busy, and queued history loading could restart after cancellation or clear a preceding error. Both were fixed, then exercised by the final desktop smoke.

## Trace identity

| Public fixture | SHA-256 |
|---|---|
| `allocator/SimpleAllocator-dotnet6.etl` | `5876415dbc49e6b86bd96906e77a2237deced01a8841684644f3bd303a1cf812` |
| `net452/net.4.5.2.x64.etl` | `50da7f725f80f88a7622b37f9826844a790cb0c4f64e351fa1f38cf24916889a` |

The allocator fixture exposes 1,015 captured lifetimes but no resolvable parent links under the existing importer rules. The net452 fixture exposes 36 lifetimes and one resolvable parent link. Missing start evidence was not guessed to manufacture a fuller tree. A probe for a guessed net462 filename failed because that path did not exist; no result from that probe is included in the acceptance claim.

## Limits

System Informer findings are from pinned source inspection, not running its application. The desktop check used a local staged framework-dependent build and public fixtures, not a newly published self-contained package. Monitor, saved-investigation and saved-comparison smoke options were not selected in this pass; their false flags mean not exercised here. The full automated suite is separate evidence.

No independent Windows 11 run, broad user study, maximum-rich-schema ETL test, native sleep/resume test, observer-overhead measurement, privileged helper security test or new collector was performed. Existing release gates stay open. The 10,000-level synthetic test verifies traversal robustness, not a UI performance guarantee. Historical process parentage does not establish a blocking dependency or application-level cause.

Implementation and follow-up sequence: [System Informer study and feature plan](../research/system-informer-study-and-feature-plan-2026-10-19.md).
