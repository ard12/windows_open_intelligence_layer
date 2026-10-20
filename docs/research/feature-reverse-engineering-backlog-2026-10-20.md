# Features to study and implement next

20 October 2026 · Windows Open Intelligence · one full-time engineer

**Recommendation: extend the investigation workflow in small, verifiable steps.** The first packet is export review, thread-scoped CPU analysis and one-click hottest-path navigation. These reuse the current evidence engine. Memory explanations, hard-fault contributors and captured-module search are the next candidates using existing data. New collection belongs behind separate qualification gates.

This updates the [19 October study](apps-feature-research-2026-10-19.md); it does not replace its historical evidence or claim that every reference app was reverse engineered. This pass compares 13 reference applications, carries forward focused source studies, rechecks official documentation, and reproduces one shipped-artifact resource inspection through REA. No competitor application, driver or collector was executed.

## Current boundary

The inspected checkout is `main`, HEAD `610f2c1`, with unrelated modified/untracked application, accessibility, capture-test, dependency and workflow files. Recommendations describe the inspected source tree, not just the older downloadable preview. This research changes no production code, dependencies, release metadata or existing edits.

These features already exist and must not become new work items:

| Foundation already implemented | Existing study / source boundary |
|---|---|
| Historical process families and captured parent/child lifetimes | [System Informer study](system-informer-study-and-feature-plan-2026-10-19.md); parentage is not a blocking dependency. |
| Saved event filters, include/exclude rules, row-derived filters and paging | [Procmon study](process-monitor-study-2026-10-19.md); event-display filtering has a separate scope from resource and stack analysis. |
| CPU-stack summaries, contextual tree, callers/callees and function/module grouping | [PerfView study](perfview-cpu-stack-study-2026-10-20.md); missing stacks and unknown weights remain explicit. |
| Synchronized interval navigation and same-trace A/B resource comparison | [WPA study](wpa-timeline-study-2026-10-20.md); cross-trace matching and scheduling analysis are separate. |
| Observation, marks, saved investigations, comparison and evidence export | Current README and investigation/monitor models; collection must precede a historical incident. |

No build or test count was refreshed for this documentation-only study. Source availability is not proof of correct runtime behavior.

## What to learn from each app

The reference behavior below is documented unless explicitly identified as source or artifact evidence. The proposed WOI adaptations are our engineering decisions.

| Reference | Useful behavior / remaining extraction |
|---|---|
| **System Informer** | Carry forward its inspected process-history/resource drill-down patterns. The first process-family extraction is already implemented. Use a precise remaining question, not another broad clone effort. [Source study](system-informer-study-and-feature-plan-2026-10-19.md). |
| **Process Monitor** | Configurable event columns and cancellable search complement the filtering already implemented. Study one named layout and find-next workflow only when needed. [Official capabilities](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon). |
| **PerfView** | Preserve its distinction between stack filtering, contextual paths and flat summaries. Existing algorithms were studied from public source; narrow inclusion filters are a useful next slice. [Existing source study](perfview-cpu-stack-study-2026-10-20.md). |
| **WPA** | Process/thread CPU analysis and running/ready/waiting evidence. Sampled CPU and context-switch-derived scheduling answer different questions. [CPU analysis](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/cpu-analysis). |
| **Visual Studio Profiler** | Thread filtering and Show/Expand Hot Path reduce navigation work. Borrow those interactions before adding another stack visualization. [CPU profiling](https://learn.microsoft.com/en-us/visualstudio/profiling/cpu-usage?view=vs-2022). |
| **Firefox Profiler** | Its sharing workflow lets users exclude information; transfer the review interaction to local exports. Its call-tree filters explicitly select samples. [Sharing guide](https://raw.githubusercontent.com/firefox-devtools/profiler/main/docs-user/guide-getting-started.md), [filtering guide](https://raw.githubusercontent.com/firefox-devtools/profiler/main/docs-user/guide-filtering-call-trees.md). |
| **Perfetto** | Pinning/hiding tracks helps keep relevant activity visible. Apply the interaction to recorded lifetime lanes once those lanes exist. [UI guide](https://perfetto.dev/docs/visualization/perfetto-ui). |
| **Process Explorer** | Find processes associated with a DLL. Adapt this to captured module inventory and historical lifetimes. [Official documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/process-explorer). |
| **VMMap** | Separate committed virtual memory from resident working sets. Start with explanations of existing counters; region classification is a later acquisition task. [Official documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/vmmap). |
| **RAMMap** | Distinct physical-memory categories and saved snapshots support disciplined before/after analysis. WOI's existing counters do not reproduce all its categories. [Official documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/rammap). |
| **TCPView** | Endpoint ownership, state and added/removed/changed highlighting suggest a bounded snapshot-difference feature. [Official documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/tcpview). |
| **PresentMon** | Frame-level output is a useful external evidence source for visible stutter. Import one qualified CSV schema before considering an overlay or collector integration. [Console documentation](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md). |
| **LatencyMon** | Hard-fault and driver-timing investigations are useful questions. Study their evidence requirements; its measurement instrumentation can affect the workload. [Product overview](https://resplendence.com/latencymon), [technical description](https://resplendence.com/latencymon_technical). |

## Ranked implementation candidates

Effort is a planning estimate including focused tests and ordinary local checks. It excludes signing, independent Windows 11 acceptance, participant scheduling and unforeseen schema qualification. It is not a delivery commitment. Rank balances usefulness, evidence readiness and maintenance cost.

| Rank / feature | Smallest complete WOI slice | Data / effort | Required acceptance |
|---|---|---|---|
| **R1 · Export review and selectable omissions** — Firefox | Preview included sensitive categories, then produce a deliberately smaller Markdown/JSON report. Cover ETL, investigation, saved-attempt and A/B-page exports. Keep originals locally. | Existing reports; **3–5 days**. | Insert synthetic names, paths, command lines, endpoints and notes into every export route; omitted fields must disappear from values, labels, references and nested attachments. Explain residual disclosure. Do not promise anonymity. |
| **R2 · Thread-scoped CPU investigation** — VS / WPA / Firefox | Select one recorded thread lifetime; reuse the interval, stack summary/tree and representative-event navigation. | `Observation.ThreadId` and thread lifetimes already exist; `CpuStackQuery` lacks thread scope. **3–5 days**. | Propagate the lifetime through worker, reader, query, UI and export. Cover TID reuse, process scope, exited threads, unresolved attribution and cancellation. Matched thread totals plus an explicit unattributed bucket reconcile to equivalent process totals. CPU samples do not describe waiting time. |
| **R3 · Show hottest sampled path** — VS | One tree action follows the largest contextual child, with visible metric basis and representative evidence. | Existing contextual tree; **1–3 days**. | Define deterministic ties and stop conditions. Test branching, recursion, missing stacks and unknown weights. Use sample counts initially; offer weighted ranking only when compatible weights are complete. Avoid one full replay per expanded depth. |
| **R4 · Memory explanation panel** — VMMap / RAMMap | Explain available physical memory, system commit, selected-process private bytes and working set, with selected-window changes. | Existing snapshot fields; **2–4 days**. | Controlled reserve → commit → touch → release phases; unavailable counters stay unavailable. Do not add these overlapping dimensions into a fake total or label growth a leak. Percentages needing total/limit counters require collecting and persisting those fields separately. |
| **R5 · Hard-fault contributors and evidence** — WPA / LatencyMon | Rank the selected interval by hard-fault count, recorded bytes and recorded duration, then open representative events. | Parser already exposes `HardFault`, bytes, duration and captured targets; **2–4 days after attribution qualification**. | Prove the faulting lifetime/thread mapping for supported event schemas. Compare equivalent filters with a reference. Include file/image-backed faults, unknown targets, known zero, unavailable capture and reported loss. Overlapping durations are not elapsed freeze time; faults do not prove pagefile activity or a cause. |
| **R6 · Find captured module across process lifetimes** — Process Explorer | One literal module/path query returns bounded historical lifetime results and opens recorded details/evidence. | Existing per-entity module inventory; **1–3 days**, conditional on a bounded query path. | Test duplicate basenames, full paths, exited lifetimes, PID reuse and missing rundown. Captured presence does not prove the module was loaded at an arbitrary selected instant. Do not enrich old evidence from current host files. |
| **R7 · Literal stack inclusion filter** — Firefox / PerfView | Retain samples containing a selected function/module; reuse current stack views and add a visible filter chip and Reset. | Existing captured stacks; **2–4 days**. | Show original and retained sample counts and the percentage denominator. Test recursion, unresolved identities, missing stacks and zero matches. Keep event-display filters separate. Defer regex, arbitrary folding and a transformation language. |
| **R8 · Pin/hide relevant activity lanes** — Perfetto | Pin a few recorded lifetimes and hide unrelated lanes without changing their shared time axis or source accounting. | Display work **2–3 days after suitable lanes exist**; creating lanes is additional scope. | Hidden rows remain in whole-trace totals and quality accounting. Display visibility must not silently change query attribution. Pin by lifetime identity, not PID. |
| **R9 · Qualified scheduling/wait timeline** — WPA / Perfetto | First import a controlled scheduling-rich ETL; show running, ready and waiting intervals with source events and unknown boundaries. Add one capture profile only after the imported model qualifies. | New schemas and state reconstruction; current shipping profiles lack explicit CSwitch/ReadyThread requests. **2–4 weeks plus native validation**. | Running + ready + waiting + unknown reconcile within each qualified lifetime/window. Handle start/rundown, loss, CPU migration and reused IDs. Compare with WPA on controlled CPU, sleep and synchronization workloads. Readying relationships alone do not prove application-level cause. |
| **R10 · On-demand endpoint snapshot and difference** — TCPView | User-triggered TCP/UDP IPv4/IPv6 snapshots attached as a separate source; show added, removed and state-changed endpoints. | New acquisition and persisted schema; **3–6 days**. | Handle process exit/PID reuse and collection races without inventing ownership. Test loopback, listeners, IPv6 and UDP. Preserve numeric endpoints and acquisition time. Skip DNS, packet inspection and connection closing. Snapshots miss brief connections and do not establish latency. |
| **R11 · PresentMon CSV attachment** — PresentMon | Import one versioned schema; show frame-pacing distributions per process/swap chain in a separate evidence view. | New importer, not a collector; **4–8 days**. | Preserve source hash, schema, units and presentation/display semantics. Test missing/dropped rows and multiple swap chains. Overlay with ETL only with a verified clock mapping; otherwise retain separate time axes. Frame delay does not identify its cause. |
| **R12 · DPC/ISR timing by driver** — LatencyMon / WPA | Imported-trace count, maximum and total recorded duration with source-event navigation. | Qualified duration events and module/address-lifetime mapping; **1–3 weeks after qualification**, capture work additional. | Compare known traces with WPA. Cover nested/interrupted activity, unresolved modules, event loss and boundaries. Ordinary sampled interrupt/DPC activity is not a DPC-duration measurement. No global audio-readiness verdict. |

The feature names in R1–R12 are independent WOI designs. They are not claims of binary or runtime parity with the reference products.

## First packet and stop rules

Implement **R1, R2 and R3**, sequentially. Their estimated sum is **7–13 engineering days**, plus roughly **1–2 integration days**; external release gates remain separate. This is a small release candidate, not permission to open all twelve workstreams.

- R1 is complete when a reviewer can see and control what an exported report contains across every export path.
- R2 is complete when a developer can isolate a known busy worker thread without confusing another lifetime with the same TID.
- R3 is complete when the developer can reach the known dominant sampled call path and its evidence without manually expanding every branch.

Use the existing WPF UI, typed worker messages, TraceEvent adapter, lifetime IDs, nullable metrics and evidence references. Preserve current query/paging limits and cancellation semantics. No new frontend, HTTP API, plugin system or trace engine is needed.

Then choose **one** of R4–R7 based on a real investigation. Choose R9 when the accepted task is genuinely about waiting or scheduling, and budget its acquisition/reference work explicitly. A CPU-sampling screen cannot substitute for that evidence.

Stop a candidate if its evidence cannot reconcile, users repeatedly mistake it for a cause, or it adds another screen without helping them reach an evidence-backed next step. Existing correctness, collector security, Windows 11 and overhead gates remain independent.

## REA artifact evidence and depth limits

The existing official-download artifact is **Procmon64.exe 4.12**, 2,237,720 bytes, SHA-256 `28495e642b2a465474a4edc58380eaf8b7ee580f3fbcb4141061deadafe9b885`. The freshly opened publisher page identifies 4.12, published 15 October 2026; a search snippet still reported 4.11 and was not used for version identity. Acquisition provenance is retained in the [earlier Procmon study](process-monitor-study-2026-10-19.md).

REA's execution-free `inspect_pe_resources` returned evidence **`ev_dc96228afd52fc229cb4e16f4751a35a46206fa3b34ae267f439b571114fd334`**, provider `rea-pe-resources` version 1: complete resource inventory within 597 directory entries and 292 resources. Named resources include `COLUMNCHOOSER`, `FILTER_CONTROL`, `ORGANIZE_FILTERS`, `SAVE_FILTER` and `CONTEXT_PROCESSTREE`. This corroborates shipped resource presence; it does not establish filtering semantics, persistence behavior or runtime availability. Non-icon payloads remain opaque. The identical evidence ID matches the earlier inspection of the same bytes and operation; it is not a second independent algorithm validation.

A complete portable Evidence record is saved locally at `.artifacts/apps-feature-study-20261020/rea-evidence-bundle.json` (one record). The compact [evidence index](feature-reverse-engineering-evidence-2026-10-20.json) retains the identity, selected observations and limitations for this report. Native deep analysis is unavailable because no native decompiler is configured; no setup or installation was attempted. No native target was opened, so no native session required closing.

For the open-source references, reuse the earlier exact-source studies before decompiling corresponding binaries. System Informer/PerfView source findings remain source evidence, not observed GUI behavior. For closed-source apps, reproduce documented interactions independently; this study supplies no redistribution rights to their code or assets.

## Park these ideas

- A new flame graph before users have tried the existing call tree plus R3. It is an alternative presentation, not new evidence.
- Live handle search, full VMMap region classification and an on-demand WCT hang probe. These need distinct acquisition work; a current observation cannot explain a past hang. Reconsider after the current task warrants them.
- Procmon rule toggles, find-next highlighting and named column layouts: bounded polish candidates, not reasons to rebuild the query engine. Current 4.12 conflict/precedence behavior has not been observed.
- Automatic priority/affinity changes, memory cleaning, connection killing, drivers, autostart inventories, cloud assistants and arbitrary SQL/plugin workbenches. They expand the product before the investigation workflow has earned that scope.

## Study checklist

| Question | Result |
|---|---|
| Which comparable apps and specific interactions are useful? | Answered at target-selection depth: thirteen references and twelve ranked candidates. |
| Which suggestions duplicate current work? | Answered from current README, source seams and existing studies; four implemented families excluded above. |
| What should be reverse engineered first? | Answered with a three-feature packet, explicit minimum scope, tests and effort estimates. |
| Is every reference behavior runtime/binary verified? | No. Official documentation and earlier source studies supply most behavior evidence; one Procmon resource inventory was reproduced. |
| Are usefulness, overhead, cross-tool parity and release readiness proven? | No. Those require executed reference comparisons, native validation and independent investigations. |

Skipped: competitor execution, collectors/drivers, decompilation setup, implementation, application tests and publication. Estimates and user benefit remain hypotheses until the stated qualification tasks pass.
