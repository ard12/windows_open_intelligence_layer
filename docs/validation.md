# Verification record — v0.3.0 preview

Verified locally on 2026-09-29: Windows 10 Pro build 19045 x64, twelve logical processors, approximately 32 GiB RAM; SDK 10.0.401, runtime 10.0.12, installed WPR 10.0.19041. Analysis cache version is 1.1.1. Windows 11 acceptance remains pending.

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

GitHub CI requires that test before uploading a release artifact. [GitHub documents administrator Windows runners with UAC disabled](https://docs.github.com/en/actions/reference/runners/github-hosted-runners), allowing a noninteractive run. The workflow has not executed remotely yet.

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

A successful native capture-to-report run; real automatic stop, cancel, disconnect, recovery and coexistence; native rich-adapter and schema/loss cases; native CPU-weight agreement and WPA checks; maximum-envelope decoding; contributor tail latency; Windows 11 and local PDB testing; and the [eight-user pilot](pilot.md) remain open. This preview is not certified for all roadmap acceptance targets.
