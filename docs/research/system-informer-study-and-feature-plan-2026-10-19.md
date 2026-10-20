# System Informer study and feature implementation plan

19 October 2026 · WOI base commit `5ba9bdfe917ec27726210e14249a5652daa3d2dc` · local changes, not published.

## Decision and scope

Implement the historical process-family view first, then take the remaining starting points in this order: **Process Monitor filtering → PerfView CPU stacks → WPA scheduling/wait analysis**. Reuse WOI's WPF desktop, worker protocol, trace identities, TraceEvent parser, SQLite aggregates and evidence views. No new frontend, service, kernel driver or general plugin layer is needed for the first two stages.

System Informer was inspected from public source at commit `066e2f2d7d635ff74480fbb6ccd24b46b6ccde51`. Findings below are source-established, not observed through a running System Informer binary. WOI is an independent implementation; no System Informer code or assets were copied. The upstream root license is [MIT](https://github.com/winsiderss/systeminformer/blob/066e2f2d7d635ff74480fbb6ccd24b46b6ccde51/LICENSE.txt); component notices are separately listed in [COPYRIGHT.txt](https://github.com/winsiderss/systeminformer/blob/066e2f2d7d635ff74480fbb6ccd24b46b6ccde51/COPYRIGHT.txt).

## What the source establishes

System Informer separates a live process tree from its retained graph-contributor records. Its live tree is not a durable historical family database.

| Question | Upstream evidence | WOI decision |
|---|---|---|
| How are parents validated? | `PhpValidateParentProcessNode` uses process sequence identity when available and otherwise creation time. It warns about zero sequence numbers and PID-reuse cycles. [proctree.c:423–440](https://github.com/winsiderss/systeminformer/blob/066e2f2d7d635ff74480fbb6ccd24b46b6ccde51/SystemInformer/proctree.c#L423-L440) | Use captured trace-local lifetime IDs. Require the parent to overlap the known child start; never join to a currently running PID. Reject invalid/self/cyclic relationships. |
| How is the live tree assembled and removed? | `PhAddProcessNode` attaches matching roots as children. Removal drops the PID lookup, then deletes the node and promotes children to roots. [proctree.c:478–619](https://github.com/winsiderss/systeminformer/blob/066e2f2d7d635ff74480fbb6ccd24b46b6ccde51/SystemInformer/proctree.c#L478-L619) | Keep captured exited ancestors and descendants in the loaded trace. This intentionally differs from a live task manager. |
| What does graph double-click mean? | CPU graph double-click resolves the maximum contributor for that history sample and opens its record. [syssccpu.c:1072–1087](https://github.com/winsiderss/systeminformer/blob/066e2f2d7d635ff74480fbb6ccd24b46b6ccde51/SystemInformer/syssccpu.c#L1072-L1087) | Preserve interval → contributor → lifetime navigation. A later graph-point shortcut should select captured evidence, not today's same-PID process. |
| How are old records retained? | CPU/I/O statistics hold contributor records; references can be released once the process has died and its exit predates retained statistics. Historical lookup is PID/time-based. [procprv.c:3636–3680](https://github.com/winsiderss/systeminformer/blob/066e2f2d7d635ff74480fbb6ccd24b46b6ccde51/SystemInformer/procprv.c#L3636-L3680), [procprv.c:3915–4077](https://github.com/winsiderss/systeminformer/blob/066e2f2d7d635ff74480fbb6ccd24b46b6ccde51/SystemInformer/procprv.c#L3915-L4077) | Use the existing immutable ETL lifetime inventory, not a new live retention store. Bound each response to 500 rows; the desktop requests 250. |
| Can a retained record open live properties? | Rebinding requires equal sequence identity or exact creation time. Parent text/properties may become unavailable after exit. [procprv.c:4117–4144](https://github.com/winsiderss/systeminformer/blob/066e2f2d7d635ff74480fbb6ccd24b46b6ccde51/SystemInformer/procprv.c#L4117-L4144), [procrec.c:124–154](https://github.com/winsiderss/systeminformer/blob/066e2f2d7d635ff74480fbb6ccd24b46b6ccde51/SystemInformer/procrec.c#L124-L154) | Open recorded entity details and events only. No live process control or stale-PID lookup. |

## Implemented: historical process families

Open an ETL and select **Process history**. The view shows captured process names, PIDs, known start/exit times, incomplete boundaries, parentage and resource measurements for the applied interval. Exited processes and lifetimes outside that interval remain present with explicit interval-state labels.

Select a row and choose **Focus family** to view it and all captured descendants; **Parent family** navigates upward and **All families** restores the forest. **Explore process** opens the existing event explorer at that exact lifetime and interval. **Inspect lifetime** opens captured metadata, threads and modules. The Explorer also has a **Process family** entry point.

Totals cover the full family and interval, independently of the current page. Unattributed activity is excluded from family totals and retained separately in the response; the UI reports the whole interval's unattributed CPU samples. The view does not sum shared working sets, treat unknown counters as zero, or interpret a parent link as a blocking dependency. Its scope is explicitly separate from the global single-process picker.

Implementation map:

- `src/Woi.Core/ProcessHistory.cs`: typed query/page/row/totals; validated parent forest; iterative traversal and subtree ranges; interval states and page-independent totals.
- `src/Woi.Core/QueryEngine.cs`: history query reuses exact contributor aggregation and source hash.
- `src/Woi.Worker/Program.cs`: additive `processHistory` operation with existing serialized/cancellable request handling.
- `src/Woi.Desktop/MainViewModel.ProcessHistory.cs`: paging/family navigation, interval preservation and display state.
- `src/Woi.Desktop/MainWindow.ProcessHistory.cs` and `MainWindow.xaml`: desktop controls and recorded-evidence navigation.
- `tests/Woi.Tests/ProcessHistoryTests.cs`: controlled relationship, paging, measurement and deep-tree cases.
- `tests/Woi.Tests/DesktopClientTests.cs`: real-ETL worker paging, identity, total reconciliation, detail lookup and reopen.
- Existing desktop smoke test now checks selecting the tab during import, reopening while selected, family focus, and screenshot rendering.

Review caught a dropped initial load when the tab was selected while another operation was busy. The implementation now defers that initial request until the current operation finishes and reloads when a recording is opened with history visible.

The first real-ETL test also exposed a distinction in its expected total: 5,316 whole-interval CPU samples included 187 unattributed samples; captured families accounted for 5,129. The response now preserves separate unattributed totals, and the test reconciles both categories rather than assigning unknown activity to a family. The initial failed result is retained in `.artifacts/process-history-validation/process-history-focused.trx`.

### Validation record

Validation: **132 tests passed, zero skipped; final desktop build had zero warnings/errors; desktop smoke passed**, including recorded-family navigation, worker recovery, cancellation and error preservation. Details and the retained initial failed comparison are in `docs/verification/process-history-2026-10-19.md`. Existing release gates remain open. A controlled 10,000-level traversal test is not certification of maximum-size ETL parsing or UI response time on every machine.

## Staged extraction plan for the remaining starting points

The word extraction here means producing a precise behavior/data specification and then implementing it against WOI's own evidence model. Use complete public source when available. Use documented behavior and ordinary interaction for closed-source references; binary decompilation is not the default.

### Stage 2 — Process Monitor: reusable filters and event navigation

**Question:** Can users move from one interesting event to a repeatable, inspectable event selection without losing the recording or their place?

**Study:** [official Procmon capabilities](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon), especially non-destructive filtering, arbitrary event properties, process trees and cancellable search. Freeze the reference version and record the behavior of include/exclude combinations, missing fields, filter edits, save/reopen and selection changes using an owned small file-operation workload. Sysinternals' published [license terms](https://learn.microsoft.com/en-us/sysinternals/license-terms) restrict reverse engineering/decompilation except applicable-law exceptions; this plan uses documentation and independent implementation with public interfaces.

**Extract:** a truth table for matching rules; saved-preset schema; cursor invalidation rules; row-action behavior; before/after event IDs and counts. Do not assume Procmon's Registry/result/driver capture exists in WOI.

**Implement first:** named local presets over existing category/operation/target/lifetime filters; include/exclude actions for normalized fields; clear-visible-filters; cancellation and explicit result scope. Extend `EventQuery`, `EtlReader.QueryEvents`, desktop state and the existing worker protocol. Presets should be versioned/bounded local preferences, separate from immutable trace evidence. Cross-trace presets must not silently reuse trace-local lifetime IDs.

**Gate:** table-driven matching tests, missing-field cases, cancellation, stale replies, saved-preset round-trip and paging across filter changes. The same source/preset/interval must reproduce the same event IDs. Clearing filters must recover the complete query scope. A preset cannot change or delete source evidence.

**Estimate:** 3–5 engineering days after a short behavior study. Stop before implementing general regex/scripts or new Registry collection unless the existing field set proves insufficient for a concrete scenario.

### Stage 3 — PerfView: aggregated CPU stacks

**Question:** Which recorded call paths account for the selected process's sampled CPU, and what changed between equivalent attempts?

**Study:** pin a [PerfView source](https://github.com/microsoft/perfview) commit, inspect its [stack-view guide](https://raw.githubusercontent.com/microsoft/perfview/main/src/PerfView/SupportFiles/UsersGuide.htm), then trace inclusive/exclusive weights, caller/callee navigation, grouping and baseline comparison through the relevant source. Identify the actual files/functions in that pinned revision before writing an implementation brief. No need to decompile a managed release when the complete source answers the question.

**Extract:** measurement definitions; stack identity/recursion rules; sample-count versus weighted-time behavior; unresolved-symbol states; filtering order; comparison compatibility rules. Retain one stack-rich CPU ETL and an independently calculated top-stack reference with exact interval/process/symbol settings.

**Implement first:** a bounded aggregate query over captured stacks, with exclusive/inclusive views and links to representative source events. Keep all-sample versus stack-bearing denominators explicit. Reuse TraceEvent; do not import PerfView's whole application. Stack comparison comes only after one-trace totals reconcile. Today's built-in WOI profiles have no explicit stack-walk configuration, so start with qualified imported traces.

**Gate:** exclusive totals reconcile to eligible weighted samples; unresolved stacks are counted, not dropped; recursive frames do not inflate totals; partial boundaries and unavailable weights stay explicit. Compare against the same PerfView/WPA trace selection. Preserve incompatibility handling for differing units, task scope and source coverage.

**Estimate:** 5–10 days for imported-trace aggregation; comparison and a new capture profile are separate follow-ups. No automatic symbol downloads are required.

### Stage 4 — WPA: scheduling and wait explanations

**Question:** During an observed delay, was the selected thread running, ready but unscheduled, or waiting, and which supported evidence identifies the next dependency?

**Study:** Microsoft's [WPR/WPA wait-analysis guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-xperf-wpa-wpr) and [critical-path walkthrough](https://learn.microsoft.com/en-us/shows/defrag-tools/156-critical-path-analysis-windows-performance-analyzer). Capture a deliberately blocked owned test application and compare its known synchronization sequence with WPA's precise CPU/readying views. Ordinary sampled CPU cannot answer this question.

**Extract:** provider/event/version prerequisites, state-transition table, boundary handling, clock basis, identity joins, stack association, loss behavior and what WPA leaves unresolved. Record the columns and measurement definitions used in each comparison. Avoid inferring dependencies from process parentage or simultaneous spikes.

**Implement in two slices:** an on-demand current [Wait Chain Traversal](https://learn.microsoft.com/en-us/windows/win32/debug/wait-chain-traversal) observation attached as a separate source; then a recorded scheduling model and validated capture profile for running/ready/wait intervals. WCT does not reconstruct history and supports a limited set of synchronization primitives. Run probes separately from the one-second sampler.

**Gate:** known mutex/critical-section wait matches the owned workload; timeout/access-denied/exit/unsupported cases remain explicit. Recorded intervals reconcile against WPA, including loss and partial boundaries. An observed readying thread is evidence for a specific relationship, not blanket application-level causality.

**Estimate:** 5–10 days for bounded current WCT; 2–4 weeks plus native validation for recorded scheduling. Stop if the acquisition overhead or missing schemas prevent a trustworthy interpretation.

## Later feature extraction queue

| Reference | Extract next | Smallest WOI slice | Required gate |
|---|---|---|---|
| VMMap / RAMMap | Commitment/residency/category semantics and before/after behavior | Explain existing memory counters, then selected-process region snapshots | Owned reserve/commit/touch/release case; no leak/pressure claim from size alone |
| TCPView | Endpoint identity, state changes, snapshot gaps | Read-only TCP/UDP snapshots, timestamped separately from ETL network bytes | IPv4/IPv6, exit/PID races, brief missed connections, no inferred remote delay |
| PresentMon | CSV field definitions, units, process/swap-chain identity and clock mapping | Optional importer attached to an investigation | Qualified time alignment; otherwise separate timelines; preserve missing/dropped-frame states |
| LatencyMon | Driver duration/count/distribution meanings and measurement side effects | DPC/ISR tables only after event-schema qualification | Pairing/attribution agreement with reference traces; no universal audio verdict |
| Autoruns | Inventory categories and entry identity | Read-only supplied-inventory comparison | Exact added/removed/changed entries; configuration is not proof of execution |

## Rules for using REA in follow-up work

The requested REA skill was used to route the investigation. This pass required source study, so it did not open a binary session or claim artifact/runtime evidence. If a later question cannot be answered from available source, identify the exact supplied artifact and hash first. Use native `open_binary`, managed `inspect_managed_artifact`, or JavaScript analysis according to actual artifact type; preserve evidence IDs and limitations. Static findings do not count as executed behavior. Do not inspect unrelated installed programs or alter tracing/driver settings merely to broaden the study.

For each stage keep a small evidence ledger: reference revision; inspected functions/docs; reproducible scenario; expected/observed result; WOI schema/code touchpoints; validation; unresolved gaps. Complete one vertical feature and its checks before adding another collector. The next implementation after this change is Stage 2's saved filters, with the existing data fields.
