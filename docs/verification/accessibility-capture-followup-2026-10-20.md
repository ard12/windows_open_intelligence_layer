# 20 October: WPF automation and WPR save follow-up

This is a draft engineering-preview candidate, not stable acceptance. Existing export review, thread-lifetime CPU navigation, hottest-path navigation and concurrent research work were preserved in 193e1ef. Original development inputs were hash-checked before and after the follow-up: 284 files unchanged in the original checkout. Work, build outputs and private native evidence use J:.

## WPF compatibility mitigation

The desktop runtime configuration now enables `Switch.System.Windows.Automation.Peers.ItemAutomationPeerKeepsItsItemAlive` before automation peers exist. Production smoke asserts that configuration; accessibility, virtualization and exception reporting remain enabled. This mitigates the demonstrated expired weak-item cell-peer failure on the exact shipped WPF 10.0.13 runtime. The original intermittent application race and responsible table transition were not reproduced, so this is not a proven root-cause repair.

The [official WPF source](https://raw.githubusercontent.com/dotnet/wpf/main/src/Microsoft.DotNet.Wpf/src/PresentationFramework/MS/Internal/FrameworkAppContextSwitches.cs) warns that retaining items can reintroduce leaks. The option affects the whole desktop process. The new dependency-free [automation regression](../../tools/Woi.AutomationRegression/README.md) uses the real window, view model, worker, charts and collection changes with an independent UIA selection listener. It supports paired fresh-process baseline/candidate runs and a deliberately retained-provider phase.

| Bounded Windows 10 run | Cycles | Sampled peak UI + monitor MiB | Live tracked entries at four batches | After provider release |
|---|---:|---:|---|---:|
| Ordinary baseline, switch false | 20 | 239.320 | 6 / 6 / 6 / 6 | 6 |
| Ordinary candidate, switch true | 20 | 237.875 | 19 / 19 / 19 / 19 | 13 |
| Held-provider candidate | 20 | 237.184 | 24 / 24 / 24 / 24 | 13 |

The unchanged 300 MiB sampled UI+monitor threshold was not exceeded in these runs. Analysis-worker memory is separate. Tracked weak-reference entries can duplicate objects; the diagnostic tracking list itself adds heap allocations. Held providers grew from 44 to 179 without increasing the observed retired-entry count, then released to ordinary candidate levels. Both baseline and candidate window/view-model references remained alive after close, so complete recovery and leak freedom remain unresolved. These forced-GC, single-fixture, shared-host diagnostics do not replace the historical failed thirty-minute memory/coverage run or long-session acceptance.

The checked-in runner also passed its candidate arm (239.20 MiB). Its final memory guards reject missing PIDs and mismatched start times; unavailable active monitor memory remains null and fails the gate. The guard-only correction was rebuilt and checked separately; the paired long arms were not repeated for that diagnostic-only correction. The actual production executable, with external UIA attached, passed its full recovery/monitor/investigation and existing feature smoke, including 57 accessible control names and the effective startup flag. Physical screen-reader/keyboard-user acceptance remains open.

## WPR save

Timeout and cancellation now retain separately labeled stdout/stderr, capped at 4,096 characters per stream while draining excess output. Truncation and incomplete draining have separate markers. Process-tree termination, a five-second reap bound and a two-second drain bound preserve bounded cleanup. Tests verify output and JSON wire limits, cancellation identity, nonzero exits, owned-process cleanup and inherited pipe handles. A controller regression confirms that a partial final-path ETL never becomes Saved and retains the native diagnostic in failed metadata.

A native Resources run with the diagnostic-only candidate reproduced the unchanged 60-second stop timeout. Both streams were empty. The protected collector retained a 23,068,672-byte partial ETL; it remained Failed with no published trace path, byte count or hash. Raw flush/rundown files existed, but they do not establish the exact delayed merge phase.

The candidate now passes fixed `-skipPdbGen` on stop. [Microsoft's command documentation](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpr-command-line-options) describes this as disabling dynamic generation of ngen and embedded PDBs to reduce stop time; the [WPR engineering article](https://devblogs.microsoft.com/performance-diagnostics/wpr-start-and-stop-commands/) explains that optional generation can take tens of seconds. This is a targeted save-time mitigation, not evidence of a universally diagnosed root cause. Recorded providers, Loader/module events, instance ownership, canonical volume paths, ACLs, directory leases, memory mode and the 60-second command deadline are unchanged. Managed symbol availability can be reduced; symbols are not generated or downloaded automatically by this app.

The Windows 10 retry of this candidate did not reach recording: Windows reported elevation cancelled. Native Resources/FileActivity save acceptance for this change remains PENDING; the diagnostic-only timeout is retained as a failure. The self-contained helper candidate is prepared for a fresh approved functional test.

## Verification and remaining gates

- Final cached locked Release build: zero warnings/errors; 171 tests passed, zero failed/skipped, including real ETL fixtures and inherited-pipe cleanup.
- Source/application packaging privacy: 34 synthetic private filenames excluded, new source retained, untracked personal documents omitted and redirected paths rejected.
- SDK 10.0.402 and runtime 10.0.13; no new external packages. Self-contained publication and dependency checks passed: 12 lock files, 80 package/version pairs and 435 asset declarations, with no known advisory matches. Clean-byte, one-byte tamper and unaccounted-binary controls passed. The final self-contained desktop also passed the full smoke with external UIA attached. CI results are tracked by exact commit.
- Earlier feature commit 193e1ef passed both [PR](https://github.com/ard12/windows_open_intelligence_layer/actions/runs/39437579510) and [push](https://github.com/ard12/windows_open_intelligence_layer/actions/runs/39437560787) workflows; downloaded TRXs each contain 166 passes and zero skips. These do not substitute for later-head results.

Windows 11 remains pending. Historical observer cost (+14.57% task duration, +6.19% CPU work), sampling continuity and memory failures are preserved. Controlled repeat performance/retention, sleep/resume, independent usability/assistive-technology acceptance, privileged trust/storage attack testing, service-side temporary writes and protected installation/signing remain open. Functional capture is not a security audit. PR #2 stays draft; no merge, release or tag is included.
