# Verification record — v0.3.0 preview

Verified locally on 2026-09-29: Windows 10 Pro build 19045 x64, twelve logical processors, approximately 32 GiB RAM; SDK 10.0.401, runtime 10.0.12, installed WPR 10.0.19041. Analysis cache version is 1.1.1. Windows 11 acceptance remains pending.

## 2026-09-30 P0 checkpoint

The updated [product reassessment](product-reassessment.md) makes independent usefulness and correctness the near-term gates. This checkpoint advances P0; it does not close native capture or Windows 11 acceptance.

- Locked Release build passed with zero warnings and errors. All 58 automated tests passed again.
- CPU contributor explanations now use summed recorded sample weights when all contributors have them, matching the query ranking. When weights are incomplete, the explanation names the sample-count leader and says why. The contributor grid and Markdown report expose estimated sampled CPU milliseconds separately from sample counts.
- Explanations distinguish absent CPU or physical-disk coverage from zero activity. Regression assertions cover weighted and unweighted leaders and missing CPU coverage.
- Self-contained publish succeeded after removing rebuildable local validation caches from a nearly full D: drive. The ZIP is about 76 MB. The packaged desktop smoke test on SimpleAllocator-dotnet6.etl passed with 418,378 indexed events, 500 first-page events, 72 contributors, and verified worker crash recovery.
- The controlled native capture was attempted through the production helper on Windows 10 build 19045. Windows canceled UAC elevation before recording, so no ETL was produced and the capture-to-report gate remains open. Repeat on a consenting interactive account and on Windows 11 before claiming native support.
- The existing 1.55% native CPU-weight discrepancy, 514 ms contributor-query p95, maximum-envelope decoding, and independent pilot remain open.

## 2026-09-30 P1 observation checkpoint

The separate unprivileged monitor worker now collects roughly one-second Windows API snapshots while ETL import runs in its own worker. Session/PID/creation-time identity, first-sample baselines, invalid-delta gaps, bounded in-memory retention, marks, and on-demand observed lifetimes are implemented. The UI distinguishes accounted process CPU from machine utilization and process I/O accounting from physical disk transfers.

- Locked Release build passed without warnings. The fixture-aware suite passed 63 tests with no skips, including first-sample, PID reuse, missed-tick, counter-reset, ring-eviction, and exited-lifetime cases.
- The worker protocol smoke returned four snapshots, 2,056 retained process rows, system available memory, one mark, and clean stop. A nonexistent parent PID caused the worker to exit cleanly after a shutdown bug was found and corrected.
- The final packaged WPF smoke opened the 418,378-event SimpleAllocator fixture, recovered from an analysis-worker kill, kept the independent monitor active, marked the observation, queried 500 bounded lifetimes, and rendered the Observe tab with CPU, memory, and accounted process-I/O charts. Stopping observation cleared the unsaved UI history.
- A 120-second packaged monitor run retained 120 snapshots across 119.0 seconds and 60,776 process rows. Peak worker working set was 58.0 MiB; average worker CPU was 0.26% of machine capacity on this Windows 10, 12-logical-processor host. The final tick had 146 unreadable processes; accounted CPU therefore cannot be treated as complete system CPU. The machine-readable result is in the local ignored file `.artifacts/validation/monitor-stress.json`.
- After the final process-I/O chart change, a separate 30-second packaged run retained 30 snapshots across 29.0 seconds and 15,868 process rows, with 46.03 MiB peak worker working set and 0.328% average machine CPU. The final package passed the desktop smoke again. This short run meets the provisional isolated worker CPU/memory targets on this host. It does not establish the UI-plus-monitor budget, workload slowdown, 24-hour behavior, Windows 11 compatibility, or reliable history across sleep/resume and heavy churn. Unsaved sessions have no regular persistence code path; disk-write overhead has not yet been independently measured.

## Completed checks

- Locked restore and Release build completed without warnings or errors.
- 58 automated tests passed with no skips, using the two public Microsoft ETL fixtures.
- Tests cover lifetime reuse, incomplete coverage, sampling changes, weighted/count ranking, exact interval inclusion, aggregate peaks, bounded paging, cache corruption and locks, worker protocol, cancellation, crash/restart, and injected import budgets.
- Capture tests cover fixed profile bounds, unique ownership, stop/deadline/cancel, failed commands, disconnect cleanup, interrupted recovery, and bounded/versioned messages using an injected WPR runner. They do not certify native recording.
- Both generated WPR profiles passed installed WPR profile-details validation with the requested keywords and 128 × 1,024 KiB buffers.
- Packaged desktop smoke tests rendered Summary, Record, Explorer and Evidence and verified reopening after worker termination.
- The dependency audit reported no known vulnerable packages in the resolved graph.
- Self-contained publishing includes the UI, worker and administrator helper; the development-only Microsoft TraceProcessing runtime is excluded.

## Real-trace stress

Three Microsoft recordings were exercised without modifying their originals:

| Recording | Source bytes | Indexed events |
|---|---:|---:|
| net.4.5.2.x64.etl | 4,879,401 | 179,474 |
| SimpleAllocator-dotnet6.etl | 15,868,175 | 418,378 |
| PerfViewData.etl (net.4.6.2.x86 archive) | 115,408,896 | 534,046 |

A 30-cycle packaged run completed 578 requests, 150,000 paged observations and six worker restarts. Source hashes remained unchanged. Damaged input recovery, invalid queries, cancellation before cache publication, and worker termination during decoding all passed. Peak worker memory was 445 MiB.

That run exposed contributor p95 of 623 ms, above the 300 ms target. Resource-filtered replay and SQL grouped totals were then added. A subsequent 12-cycle run completed another 236 requests and 60,000 paged observations with the same failure-recovery checks:

| Operation | Measured p95 |
|---|---:|
| Import/reopen mix | 6,416 ms |
| Contributors, full and partial intervals | 514 ms — exceeds target |
| Metrics | 4.3 ms |
| First CPU event page | 76 ms |
| Later CPU pages | 19 ms |
| Evidence | 16 ms |
| Report export | 28 ms |

Peak worker memory was 438 MiB. The slow partial queries were on the allocator trace. Functional stress success is distinct from passing the latency budget. This performance gate remains open; no coverage was dropped to hide it.

A separate repeated-query benchmark of the 115 MB recording measured 8.06 s cold import, 1.58 s verified reopening, 71 ms contributor p95, and 348 MiB peak worker memory. This smaller repeated-query workload does not replace the broader stress result.

Machine-readable public-fixture results are in [verification/](verification/). Local TRX, rendered desktop images and intermediate artifacts are excluded from the source archive.

## Five-million-observation aggregation

A generated dataset of five million observations, twenty processes, 210,000 aggregate rows and 10,000 seconds independently checked totals, exact partial interval inclusion, nullable sampling metadata, and the 2,000-point chart ceiling.

- Aggregation: 1.59 s.
- First full contributor query: 334 ms — exceeds 300 ms.
- Repeated-query p95: 0.97 ms with the bounded sixteen-entry totals cache.
- Metrics/downsampling: 248 ms; 501 visible points.
- Peak process memory: 235 MiB.
- Independently matched totals: 4,000,000 busy samples, 2,048,000,000 physical disk bytes, and 512,000,000 logical file bytes.

This validates aggregation/query scale, not decoding a five-million-event ETL. Maximum 1 GiB / five-million-event real import remains unverified.

## Independent parser comparison

The development-only Woi.Reference tool uses Microsoft.Windows.EventTracing.Processing.All 1.12.10, based on the [processing technology used by WPA](https://learn.microsoft.com/en-us/windows/apps/trace-processing/overview). Interactive WPA checks remain outstanding.

| Recording | Busy samples, both parsers | Read/write completions, both | Physical bytes, both | Native weight difference |
|---|---:|---:|---:|---:|
| net.4.5.2.x64.etl | 6,320 | 1,229 | 19,851,264 | 1.55% — exceeds 1% gate |
| SimpleAllocator-dotnet6.etl | 5,316 | 858 | 52,171,776 | 0.15% — within gate |

Rules exclude idle thread 0, retain interrupt/DPC activity, use half-open intervals, exclude flushes, and compare weights after recorded sampling metadata begins. Dominant workloads are Test.x64 and SimpleAllocator.

The analyzer assigns the recorded interval to busy samples; TraceProcessing exposes native estimated-duration weights. The validator returns a failed gate for the older fixture instead of masking the difference. Disk response payload and paired issue-to-completion latency also have different definitions; measured sum differences are approximately 0.0045% and 0.2044%. Summed latency is not disk busy time.

## Native capture gate

The production capture helper was invoked for the controlled test, but Windows canceled its UAC elevation. No controlled recording was created. Logical file byte, loopback, and memory snapshot adapters therefore still need this native test.

The controlled workload contains idle periods, a six-second CPU burst, 64 MiB writes plus reads, an 8 MiB loopback payload, a 128 MiB resident allocation, and twelve child processes. Woi.Validation drives the production helper, imports the resulting ETL, verifies workload lifetime and dominant CPU contributor, checks expected quantities, inspects evidence, and exports reports.

GitHub CI requires that test before uploading a release artifact. [GitHub documents administrator Windows runners with UAC disabled](https://docs.github.com/en/actions/reference/runners/github-hosted-runners), allowing a noninteractive run. The 2026-09-30 Actions run failed before any job step: GitHub reported recent account payment failures or a spending limit that needs increasing. It supplies no remote validation evidence.

## Reproduce

Run from the repository root. The capture test displays UAC on a desktop machine; approve it using the same Windows account.

~~~powershell
.\scripts\test.ps1 -DownloadFixtures
.\scripts\get-fixtures.ps1 -Large
.\scripts\publish.ps1
node .\scripts\stress.cjs .\dist\WindowsOpenIntelligence\Woi.Worker.exe .\.artifacts\validation\stress.json 30 .\.artifacts\fixtures\net452\net.4.5.2.x64.etl .\.artifacts\fixtures\allocator\SimpleAllocator-dotnet6.etl .\.artifacts\fixtures\net462\PerfViewData.etl
~~~

Use the installed dotnet CLI, or .\.tools\dotnet\dotnet.exe:

~~~powershell
dotnet tools/Woi.Validation/bin/Release/net10.0-windows/Woi.Validation.dll --aggregate-stress .artifacts/validation/aggregate-stress.json
dotnet publish tools/Woi.Workload -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -o .artifacts/workload
dotnet tools/Woi.Validation/bin/Release/net10.0-windows/Woi.Validation.dll --capture .artifacts/end-to-end/new-run dist/WindowsOpenIntelligence/Woi.Capture.exe .artifacts/workload/Woi.Workload.exe
~~~

Each capture test needs a fresh output directory. For independent parser validation, use scripts/validate-reference.ps1. The older fixture is expected to report its open weight gate.

## Remaining acceptance

Supported-desktop native capture-to-report; real automatic stop, cancel, disconnect, recovery and coexistence; native rich-adapter and schema/loss cases; native CPU-weight agreement and WPA checks; maximum-envelope decoding; contributor tail latency; Windows 11 and local PDB testing; and the [eight-user pilot](pilot.md) remain open. This preview is not certified for all roadmap acceptance targets.

## 2026-10-01 investigation checkpoints (v0.4.0-preview.1)

P2 readable investigations and P4 saved evidence now have a connected WPF workflow. Findings integrate recorded monotonic rate intervals, expose CPU/memory/process-I/O quantities and prerequisites, and abstain on missing inputs. Snapshot references preserve source/tick/lifetime identities separately from ETLX coordinates. User-confirmed comparisons show duration and absolute resources; incompatible coverage has no calculated difference. Bounded .woi files preserve snapshot evidence, detect accidental corruption, and recalculate descriptions on reopen. ETL reports attach as separate excerpts; original traces are excluded.

- The locked Release build passed without warnings and 89 tests passed with no skips, including the final protocol recovery, sampler identity, and soak-harness additions. Regression cases include partial edges, missing measurements, reused PIDs, clock rollback, measured-duration thresholds, persistence corruption/cancellation, source mismatch, workspace bounds, cross-session application confirmation, report-path exclusion, and sampling diagnostics.
- The packaged smoke passed ETL import (418,378 indexed events), analysis-worker crash recovery, native API observation, two readable investigations, within-session comparison, snapshot evidence, ETL attachment, saving, stopping/discarding the observation workspace, reopening both files, and comparison/report export in the analysis workspace. The corrected findings layout was visually inspected; text wraps and the next measurement is visible.
- P3's protocol and empty ledger are in experiment.md and experiment-results.json. No participant session, independent Windows 11 machine, or held-out accuracy result is claimed.
- Native Resources capture was attempted through the shipping helper. Windows canceled administrator elevation; no ETL was produced. The local failure and matrix are in `.artifacts/validation/native-matrix-20261001`. FileActivity and native lifecycle/coexistence remain unverified; the earlier CPU-weight discrepancy is still open.
- The first on/off fixed-work trial was invalid: its CPU phase lasted about 26 ms, too short for one-second sampling, and the result was not observably consumed. That run is retained in `.artifacts/validation/observation-benchmark-20261001`; its apparent target pass is not acceptance evidence. The workload was corrected to serialize a checksum and execute 500 million iterations per worker, and the benchmark now rejects CPU phases below two seconds.
- The corrected cold-monitor run used three counterbalanced trials per condition. Median CPU work was 3,622.08 ms off versus 3,967.04 ms on (+9.52%); median whole-task duration increased 9.07%, failing the provisional <3% slowdown target. The complete case/range/diagnostic record is `.artifacts/validation/observation-fixed-result-20261001/result.json`. Shared-machine activity, startup and small-sample variance remain confounders; this is a failed measured gate, not a demonstrated universal observer effect.

The next performance steps are explicit steady-state warm-up trials, a filled-history desktop soak, and diagnosis of any persistent slowdown. Windows 11 clean-machine use, two-hour/24-hour sessions, sleep/resume, physical disk-write cost, interactive UI cost, query p95, and independent usefulness remain open. See checkpoints.md for the full P2–P6 scope.

### Sampler and short-run follow-up

The sampler now enumerates PID values with a growing bounded buffer, rejects a potentially truncated enumeration, reads image names from the counter-owning handle, and checks creation time around the PID-based session lookup. The native regression verifies the test process's creation time, session and image name. These use the documented [EnumProcesses](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-enumprocesses), [QueryFullProcessImageNameW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew), and [ProcessIdToSessionId](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-processidtosessionid) APIs. This does not expand privilege or promise visibility of protected processes.

The final packaged smoke passed all recovery, monitor, investigation and saved-comparison flags. Local artifacts are final-investigation-desktop.json and its rendered images. A 60-second hidden-window desktop soak passed: 60 snapshots, 16,569 process rows, no missed-tick gaps, 48.11 MiB monitor peak, 181.12 MiB UI peak, 223.85 MiB maximum sampled UI-plus-monitor memory, and 247.36 MiB including the idle analysis worker. Monitor CPU was 0.47%; UI CPU was 2.36%, both including startup. Sampling median/p95/maximum were 47.77/68.23/77.01 ms. This is short-run continuity, not filled-history, interactive UI, two-hour or Windows 11 acceptance.

The first ten-second-warmed three-trial run measured median whole-task slowdown +0.35% and CPU-work difference −2.31% (observation-warm-20261001/result.json). It still recorded sampling tails around 2.75 seconds and missed ticks. The following enumeration run (observation-enumeration-20261001) was interrupted by a long pause; its first trial and combined averages are excluded from acceptance. The benchmark now rejects excessive warm-up or measurement elapsed time instead of allowing suspension to dilute CPU cost.

A fresh three-trial warmed run measured whole-task difference −7.50% and CPU-work difference −6.96% (observation-guarded-20261001/result.json). Negative changes are not an application speedup claim; small counterbalanced samples and shared-machine/cache conditions remain confounders. The final two-trial script check exercised strict variables and both elapsed guards; median task difference was −9.10% and CPU-work difference −8.88% (observation-final-20261001/result.json). Mixed results leave the observer-cost gate open; the original failed cold measurement remains in this record.

The CI capture step now invokes scripts/native-capture-matrix.ps1 for both fixed shipping profiles and preserves nested JSON/Markdown results even on failure. It subsequently acquired the successful remote native run documented below.

Reproduce the new local checks after building/publishing:

~~~powershell
.\scripts\native-capture-matrix.ps1
.\scripts\observation-benchmark.ps1 -Trials 3 -WarmupSeconds 10
.\dist\WindowsOpenIntelligence\WindowsOpenIntelligence.exe --observation-soak 7200 ".artifacts\validation\desktop-soak-2h.json"
~~~

The soak creates a results JSON at completion and exits. Its hidden-window mode does not replace active interaction. Benchmark scripts retain failed cases. The current distribution remains an unsigned preview; artifact checksums are not publisher authentication. See checkpoints.md for the open independent-use and release gates.

A privacy-reduced machine-readable summary is [investigation-checkpoints-20261001.json](verification/investigation-checkpoints-20261001.json); raw snapshots, machine identifiers, notes and local absolute paths are excluded.

### Successful remote native follow-up

[Actions run 36841914783](https://github.com/ard12/win_OiL/actions/runs/36841914783), source f7a72f6, completed every job step on Windows Server 2025 / two logical processors. Locked tests (89), self-contained publish, both shipping capture profiles, synthetic five-million aggregation, packaged desktop recovery/investigation/persistence, monitor continuity and replay stress all passed their assertions. Application and verification artifacts were uploaded.

| Profile | Indexed ETLX events | Workload is CPU leader | Logical file bytes | Network transfer records | Peak captured workload working set |
|---|---:|---|---:|---:|---:|
| Resources | 96,261 | Yes | Unavailable | 16,777,216 bytes | 168,022,016 bytes |
| FileActivity | 101,967 | Yes | 135,329,692 bytes | 16,777,216 bytes | 165,482,496 bytes |

Both captures reported zero loss, preserved source hashes, resolved the workload lifetime and its selected CPU evidence, and exported reports. Zero loss does not prove completeness; paths and unknown schemas still carry explicit limitations. These runs cover normal start/manual-save flow, not every failure or coexistence transition.

The server replay stress handled 198 requests, 50,000 paged events and two restarts. Contributor p95 was 747.14 ms, above the 300 ms gate; first-page p95 was 148.32 ms; peak worker was about 435 MiB. Synthetic cold contributor query was 343.24 ms and metrics were 339.90 ms; repeat contributor p95 was 1.07 ms. The 30-second monitor test had no missed ticks and 37.37 MiB peak but 1.293% machine CPU including startup, exceeding the <1% idle target. A green workflow does not close these failed performance budgets.

The [redacted remote ledger](verification/github-native-20261001.json) excludes raw ETLs, absolute capture paths, owner SIDs and process command lines. Supported Windows 11 desktop/clean-machine use, native failure lifecycle, CPU-weight parity, full-size real ETL, long-run observer cost and independent product evidence remain open.


### Contributor-query and filled-history follow-up

The locked Release suite now passes 92 tests. Additional real-ETL regressions compare every resource observation in a partial interval, including identities, bytes, recorded latency, sample weights and evidence coordinates, with the full detail traversal. Contributor replay omits only target display resolution; Explorer, Evidence, import and reports retain captured paths/endpoints. Allocation profiling identified approximately 66 MiB of captured target resolution in one allocator partial query; the removed per-stage instrumentation remains in local diagnostic artifacts, while shipping diagnostics retain coarse query stages, visited records and thread allocations.

The initial two-fixture 10-round measured p95 was 452.23 ms; a 20-round run after the change measured 168.92 ms. A final 30-round run across net452, SimpleAllocator and the 115 MB net462 fixture passed 638 requests, 150,000 paged events, six restarts, cancellation/damaged-import/crash recovery and source immutability. Contributor p95 was 161.34 ms, first-page p95 95.74 ms, metrics p95 4.96 ms, and peak worker working set 425.55 MiB. Runs share a development machine with concurrent observation soaks; these close this local reference-query target, not all platform or maximum-size gates. The packaged desktop also passed recovery, native observation, findings, evidence, save/reopen, comparison and export.

Observation metadata now shares stable lifetime ID/name strings between adjacent snapshots. The bounded map contains only currently sampled lifetimes; a captured name change and PID reuse remain distinct. Session-wide sample/failure/gap counters survive ring eviction, preventing a retained clean tail from hiding earlier failures. The soak writes atomic progress and final status and separately reports execution completion versus failed budgets.

The thirty-minute metadata-sharing soak completed: 1,800 snapshots, zero missed ticks/failures/invalid intervals, 114.80 MiB monitor peak and 199.47 MiB UI peak. Maximum sampled combined memory was 312.59 MiB, exceeding the 300 MiB target. Monitor CPU was 0.35% of machine capacity including startup under concurrent development; this is not isolated idle or observer-cost evidence. The ring filled and evicted under unchanged 250,000-row / 64 MiB estimated-data bounds, finishing with 249,921 rows and 787.03 seconds retained. Actual retained duration depends on process count; a 60-minute upper capacity is not a retention promise. The original two-hour baseline remains running and has also exceeded the combined memory budget. Its final cumulative coverage and peaks remain pending.

The native lifecycle validator now drives the shipping helper for deadline save/import/evidence, idempotent stop, cancellation without published ETL, pipe disconnect, verified helper crash/recovery/restart and preservation of a separately owned named tracing session. Coexistence permits explicit profile-conflict refusal, provided the separate client's recording survives and remains importable. English WPR status output is required by this validation harness; unrecognized status fails. This local shell is unprivileged; the administrator CI runner subsequently passed all five native cases.

~~~powershell
# In an elevated shell using the same Windows account:
dotnet tools/Woi.Validation/bin/Release/net10.0-windows/Woi.Validation.dll --capture-lifecycle .artifacts/validation/native-lifecycle dist/WindowsOpenIntelligence/Woi.Capture.exe
~~~

The [privacy-reduced performance ledger](verification/performance-checkpoint-20261001.json) contains measurements without snapshots, SIDs, application notes or captured process paths. Independent Windows 11 use, field usefulness, CPU-weight parity, observer cost and the combined memory budget remain open.

The [filled-history soak ledger](verification/filled-history-soak-20261001.json) preserves its failed combined-memory target and cumulative coverage without machine names or snapshot rows. The extracted preview ZIP separately passed the full desktop recovery/investigation/persistence smoke. CI run 36848658536 passed both normal native profiles but failed the new deadline harness because it observed the legitimate Saving transition prematurely; the harness now waits for Saved/Failed/Cancelled, and run 36849115092 passed the corrected native suite and every other workflow step.


[Actions run 36849115092](https://github.com/ard12/win_OiL/actions/runs/36849115092) passed for source a9c10e5: 92 tests, both production native profiles, all five native lifecycle cases, five-million synthetic aggregation, packaged desktop recovery/persistence, monitor continuity and replay stress. Deadline stop saved at 5.29 seconds and imported 52,193 indexed events. The separate coexistence recording remained intact and importable (42,773 events); the helper started alongside it and cancelled only its own instance. No completed ETL was published for cancellation, disconnect or crash/recovery cases.

The server reference replay contributor p95 is now 229.44 ms (previous run 747.14 ms); first-page p95 is 299.09 ms. Both are below the 300 ms reference-query target on this run. The server still has two logical processors; its 30-second monitor CPU was 1.24% including startup, exceeding the 1% idle target. No platform-wide or complete performance acceptance follows from the green workflow. The [redacted lifecycle ledger](verification/github-lifecycle-20261001.json) retains these limits and the first harness failure.


### Analysis 1.2: sample-weight parity and lower reply allocations

The earlier 1.55% discrepancy is resolved on the three public reference fixtures. Analysis 1.2.0 weights eligible busy samples using the previous sample on the same processor, including idle predecessor context. If spacing exceeds 110% of the recorded nominal interval, it uses that interval; absent interval metadata or nonpositive spacing stays unavailable. The threshold is an empirical match to TraceProcessing 1.12.10 across 23,433 eligible busy samples, not a documented universal Windows contract. Microsoft describes sample weight using [timestamp spacing](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/cpu-analysis); the [ICpuSample API](https://learn.microsoft.com/en-us/dotnet/api/microsoft.windows.eventtracing.cpu.icpusample?view=trace-processor-dotnet-1.0) uses different directional wording, so the native comparisons, rather than wording alone, determine this implementation.

The trace-local sparse weight index retains no TraceEvent objects and is built once with cancellation checks. Every query, page and evidence lookup uses the same global predecessor context. Cache keys and manifests now identify analysis 1.2.0, so previous aggregates rebuild from the original ETL. No new derived file format or symbol/network dependency is introduced. All samples remain available and idle samples remain excluded from busy contributors.

Native reference checks pass counts, disk bytes, eligible CPU totals within the unchanged 1% tolerance, zero timestamp/weight-group mismatches (0.0001 ms per-value tolerance), and three partial intervals per fixture. net452 eligible CPU weights are 6,214.6988 ms, allocator 4,733.9798 ms and net462 12,306.6238 ms, matching native totals to floating-point precision. Unknown early samples remain unknown; this does not make a whole interval's CPU estimate available when required metadata is missing. Shipping Resources/FileActivity server captures currently have sample counts but no usable weight metadata for their leading workload; these are not native-weight passes and missing values remain null.

The worker now writes complete serialized UTF-8 reply buffers, preserving line framing and serialization-failure atomicity while avoiding a UTF-16 copy. The desktop deserializes typed replies before disposing the received JSON document, eliminating its cloned result buffer; decoding runs away from the WPF synchronization context. Import progress is dispatched asynchronously. The isolated synthetic diagnostic measured decoding allocation 931,816 -> 420,952 bytes/reply and serialization 530,723 -> 265,593 bytes/reply. Its forced collections measure retained managed data only in the diagnostic process; production and native soaks never force GC. These allocation figures are not working-set or desktop acceptance results.

The locked Release suite passes 99 tests with no skips or warnings, including UI-context independence, typed result lifetime, bad-result isolation, cancellation/restart, worker death, Unicode framing, atomic failed serialization, per-processor weights and metadata changes. Real partial contributor weights agree with detail traversal. The packaged full investigation/persistence smoke passes. A 30-round three-fixture replay passed 638 requests, 150,000 paged events, six restarts and source integrity: contributor p95 124.35 ms, first-page p95 74.05 ms, peak worker 431.71 MiB.

A new thirty-minute reduced-copy native desktop soak and the original two-hour baseline are running. The earlier failed 312.59 MiB combined-memory result remains in the ledger; no new memory pass is claimed until terminal results. The [privacy-reduced verification ledger](verification/weight-and-allocation-20261001.json) preserves parity, allocations and replay evidence. CI now requires independent native reference checks on both standard fixtures.


[Actions run 36853621941](https://github.com/ard12/win_OiL/actions/runs/36853621941), source 13a0444, passed every workflow step, including 99 tests, the new independent native reference checks, both production capture profiles, five native lifecycle cases, synthetic scale, packaged workflow and replay. Server replay p95 was 148.42 ms contributors / 83.94 ms first-page; its 30-second monitor measured 0.931% machine CPU including startup, 37.39 MiB peak and no missed ticks. These are specific passing reference/short-run measurements, not long-run or desktop OS acceptance.

The reduced-copy thirty-minute desktop soak completed without gaps or sampling failures. Monitor peak was 117.17 MiB and UI peak 193.77 MiB; maximum sampled combined memory 308.75 MiB still fails the 300 MiB target. The final managed UI heap was about 36.36 MiB; working set also includes runtime/native graphics and committed capacity. Decoding handled 1,251 replies, allocating 408,817,704 managed bytes cumulatively, with maximum measured decode 34.42 ms. No force-GC or retention reduction was used. The [follow-up ledger](verification/analysis12-followup-20261001.json) preserves this failure and the remote passes. The original two-hour baseline remains live.


The original two-hour baseline reached terminal completion after 7,200.08 seconds: 7,200 snapshots and zero cumulative missed-tick gaps, sampling failures or invalid intervals. Hard history bounds held throughout. Maximum sampled UI-plus-monitor memory was 371.07 MiB, and the monitor peak exceeded 150 MiB; both targets failed. This baseline predates stable metadata sharing and reduced-copy replies, and ran with concurrent development/other soaks. It is not a two-hour acceptance pass for the current bundle. The [privacy-reduced terminal ledger](verification/baseline-two-hour-soak-20261001.json) retains its peaks and cumulative coverage. All three current validation soaks have now finished; several current-version two-hour sessions, sleep/resume, physical writes, observer cost and independent Windows 11 remain unverified.

### UTF-8 framing and native-envelope follow-up

104 Release tests pass with no skips or compiler warnings. The desktop now parses borrowed UTF-8 response bytes directly, with complete-frame and explicit response-memory limits. Packaged allocator smoke passed all flags: import (418,378 indexed events), worker recovery, native monitor, investigations, evidence, saved reopening, comparison and reports. The initial smoke used the wrong singular investigation flag and did not exercise those assertions; the corrected run is direct-utf8-investigations.json.

In the isolated synthetic memory diagnostic, identical 265,058-byte replies allocate 954,589.52 managed bytes with string framing versus 421,064.4 with direct UTF-8 framing (100 measured iterations after warm-up, about 56% less). The diagnostic constructs a reader per iteration over pre-encoded in-memory replies and warms the array pool; it does not establish pipe throughput, observer cost or native working-set acceptance. Retained synthetic history is still 833 snapshots / 249,900 rows. The new packaged thirty-minute soak is running; do not infer a memory pass from the allocation result.

The development-only --native-envelope <output-directory> <packaged-worker.exe> <count> check enables only its own EventSource provider, records native ETL and independently verifies every sequence and reported loss before importing through the actual desktop worker client. It checks indexed counts, unsupported-schema semantics, cached reopening, cursor pages, payload evidence, source immutability and worker memory. CI cases use 1,000, 4,990,000 and 5,010,000 generated events; the last must fail import explicitly without publishing a completed cache. The result files retain measured budget failures separately from correctness. These generated unsupported events do not certify representative resource traces, every 1 GiB recording, stacks, process churn or Windows 11 desktop operation. Native execution is pending on the administrator CI runner; no local elevation or pass is claimed.

The privilege-boundary source review and its unresolved native validation are documented in architecture.md. Existing successful functional capture tests do not establish resistance to redirected privileged writes.

### Terminal native-envelope and filled-history results

[Actions run 36861778474](https://github.com/ard12/win_OiL/actions/runs/36861778474), source 7cdfd7b, passed all steps on Windows Server 2025 / two logical processors. 104 tests passed, none skipped. Both native reference fixtures, both fixed shipping capture profiles, all five lifecycle cases, synthetic aggregation, packaged workflow, monitor and replay passed. Server replay contributor/page p95 were 91.12 / 88.52 ms; the 30-second monitor measured 0.854% machine CPU including startup. None establishes independent Windows 11 or long-run observation costs.

The native generator independently verified all 4,990,000 sequences with zero reported loss and zero missing sequences, producing 4,990,003 indexed events from a 640,090,112-byte (610.44 MiB) ETL. Cold import was 6,657.95 ms, verified cached reopening 2,025.46 ms, sequential-page p95 10.65 ms, peak worker 87.81 MiB, and derived files 514.08 MiB. Payload evidence and source immutability passed. A separate complete 5,010,000-event capture was rejected with SizeLimit and no completed cache. These are native ETL/ETLX scale results for one generated unsupported provider; rich CPU/disk/file/network traces, stacks, process churn and every 1 GiB input remain separate coverage.

The thirty-minute direct-UTF-8 desktop soak completed 1,800.09 seconds / 1,800 snapshots, with zero cumulative missed ticks, sampling failures or invalid intervals. Retention bounds held; final history was 746 snapshots / 249,850 process rows / 744.99 seconds. UI peak was 187.70 MiB, monitor peak 119.01 MiB and maximum sampled combined working set 304.96 MiB. The combined 300 MiB target failed. UI/monitor machine CPU were 0.580% / 0.369%, including startup under concurrent development activity. No forced collections or history reduction were used; no soak remains running.

Final UI managed heap was 24,089,160 bytes, while its cumulative managed allocation counter was 10,560,540,104 bytes. Decoder-thread parse/deserialization allocations were 399,010,392 bytes. The latter excludes framing and allocations on other threads; its size suggests examining rendering/binding rather than assuming replies explain all churn. It does not identify a causal allocation stack. Last-GC heap/committed/fragmentation and process private-byte diagnostics are available in the raw local report.

Failed attempts remain visible: run 36859629969 timed out one integration-test startup wait (103 passed); the bounded harness wait moved from ten to thirty seconds, without changing product budgets. Run 36860191751 compared the provider name against GUID evidence coordinates; the validator was corrected. Run 36860972639 failed near-limit independent completeness before import; exact missing/loss counts were not retained in that first report. The generator now pauses 50 ms every 8,192 records and retains exact loss/sequence diagnostics, with all completeness checks unchanged. No failed or incomplete dataset was accepted as a size pass.

The privacy-reduced result ledger is [utf8-envelope-20261001.json](verification/utf8-envelope-20261001.json). Raw original ETLs, process histories, notes and machine identifiers are excluded. Independent product usefulness, Windows 11 operation, interactive WPA review, representative maximum resource traces, physical writes, repeatable observer cost and privileged distribution trust remain open.
