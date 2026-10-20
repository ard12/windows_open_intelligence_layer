# Apps and features to study for Windows Open Intelligence

Research date: 19 October 2026. Project: `D:\windows open intelligence`.

## Recommendation

Study **System Informer, Process Monitor, WPA and PerfView first**. Build three additions on WOI's existing evidence pipeline: a historical process-family view, reusable event filters, and aggregated CPU stacks. Next, improve memory explanations and add narrowly scoped endpoint and hang observations. PresentMon is a promising optional extension for visible stutter. Autoruns and LatencyMon address useful but more distant questions.

This is a target-selection and implementation research report. Competitor capabilities below are documented by their publishers; they were not exercised locally. WOI's implementation was inspected in source. No competitor binaries were downloaded, executed, decompiled or benchmarked, and no product code was changed.

The product opportunity is to help someone move from “this app became slow here” to an inspectable explanation and a useful next measurement. History, comparison and natural-language trace analysis already exist elsewhere. Whether WOI makes that workflow easier remains a hypothesis to test.

## Current project: what we can actually build on

Inspected branch `main`, commit `5ba9bdfe917ec27726210e14249a5652daa3d2dc`; origin is `https://github.com/ard12/windows_open_intelligence_layer.git`. Git reported no changes before this report. The sandbox could not read the user's global ignore file, so that warning is retained as an inventory limitation. No repository-local AGENTS.md was found in the file inventory, and neither checked ancestor location supplied one.

| Existing foundation | Source evidence inspected | Consequence for this work |
|---|---|---|
| Immutable trace identities, process/thread lifetimes, parent links, nullable metrics and provenance | `src/Woi.Core/Models.cs:17`; `src/Woi.Trace/EtlReader.cs:46` and `:52` | Build an ETL process-family view without inventing a new identity system. Parentage is not a wait or causal dependency. |
| Interval contributor rankings and chart data | `src/Woi.Core/QueryEngine.cs:49` and `:95` | Extend the current queries and views; retain the worker and SQLite architecture. |
| Basic process/category/operation/target filters, 500-row event pages | `src/Woi.Core/Models.cs:45`; `src/Woi.Trace/EtlReader.cs:203` | Saved filters and include/exclude rules are additions; basic filtering is already implemented. |
| Per-event stacks and local symbol lookup | `src/Woi.Core/Models.cs:48`; `src/Woi.Trace/EtlReader.cs:250` | Stack aggregation is new; displaying one recorded stack is not. |
| CPU-chart interval selection and coordinated CPU/secondary chart bounds | `src/Woi.Desktop/MainWindow.xaml.cs:25` and `:182` | Add selection from other views and clearer navigation instead of claiming synchronized charts are entirely absent. |
| One-second process CPU/I/O, private bytes, working sets, available memory and system commit | `src/Woi.Monitor/WindowsSampler.cs:28`; `src/Woi.Core/MonitorModels.cs:4` | A first memory explanation view can reuse measurements already collected. It cannot reconstruct allocation categories or short stalls. |
| Deterministic findings, saved investigations and guarded comparisons | `src/Woi.Core/InvestigationAnalyzer.cs:8`; `src/Woi.Core/ComparisonEngine.cs:5` | Preserve evidence references, equivalent-work confirmation and incompatibility handling. |
| Fixed Resources/File profiles | `src/Woi.Capture.Core/CaptureProfiles.cs:32` | Profiles request sampled CPU, disk, hard faults, memory and network, with optional file events. They do not request scheduling/wait analysis or an explicit stack-walk configuration. Stack-rich imports and stack acquisition are separate tasks. |

The README and roadmap explicitly describe an engineering preview. Windows 11 acceptance, privileged helper trust/storage, observer overhead and other validation gates remain open. Tests and native behavior were not rerun for this research; existing test counts in project documents are historical evidence, not results of this pass.

## Ranked reference apps

Priority means fit to the current project and data, not a claim that one app is universally better. Each “build” entry is our proposed design.

| Priority / app | Documented capabilities worth studying | Proposed WOI feature and inspection route |
|---|---|---|
| **1. System Informer** | Historical resource graphs can identify a process even after it exits; handle/DLL search, detailed process/resource views, disk/network activity and stacks. [Official features](https://systeminformer.com/) | Historical process-family navigation, graph-point → lifetime → evidence, and useful detail panels. Public MIT source makes focused source study the first route; inspect exact module/third-party notices before reuse. [Repository](https://github.com/winsiderss/systeminformer) |
| **2. Process Monitor** | Non-destructive event filtering, configurable columns, event properties/stacks, process trees and reloadable logs. [Microsoft documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon) | Saved query presets, row-to-filter actions, explicit include/exclude rules and cancellation. Study documented behavior; independently implement against WOI's event schema. Registry and operation-result filtering need new normalized data before parity can be claimed. |
| **3. WPA / WPR** | ETL graphs/tables, synchronized interval zoom, and scheduling-based wait analysis. [Workspace](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/analysis-tab), [wait-analysis guidance](https://learn.microsoft.com/en-us/troubleshoot/windows-server/support-tools/support-tools-xperf-wpa-wpr) | Unified interval navigation now; thread running/ready/waiting timelines later. Use documented workflows and matched traces as the reference. Scheduling requires additional events and validation. |
| **4. PerfView** | ETW CPU/memory analysis, stack grouping/filtering, callers/callees and baseline comparisons. Public MIT source. [Repository](https://github.com/microsoft/perfview), [user guide](https://raw.githubusercontent.com/microsoft/perfview/main/src/PerfView/SupportFiles/UsersGuide.htm), [license](https://github.com/microsoft/perfview/blob/main/LICENSE.TXT) | Weighted stack aggregation with inclusive/exclusive views, then compatible stack comparison. WOI already uses TraceEvent; retain it and study the relevant stack algorithms/UI, avoiding a second complete trace engine. |
| **5. Process Explorer** | Process-owned handles, DLLs and mapped files; searches identify which process holds a handle or loads a DLL. [Microsoft documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/process-explorer) | An on-demand process detail panel and, later, “which process has this file open?” Study documented interaction. Begin with imported module evidence; live handles are a separate collection feature, not derivable from existing CPU/I/O counters. |
| **6. VMMap** | Separates virtual-memory commitment from resident working sets, with memory-type/region detail and saved exports. [Microsoft documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/vmmap) | Explain private allocation versus resident memory first; add selected-process region snapshots later. Full heap/type classification is a larger problem than calling one Windows API. |
| **7. RAMMap** | System physical-memory categories, paging lists, file-backed memory and saved snapshots. [Microsoft documentation](https://learn.microsoft.com/en-gb/sysinternals/downloads/rammap) | A clear system-memory explanation and before/after evidence. Reuse available/commit counters first. Advanced physical-page/file-cache attribution needs an independently established acquisition path. Do not turn cache size into an automatic “memory shortage” finding. |
| **8. TCPView** | TCP/UDP endpoints, addresses, owning processes/services, connection states and change highlighting; CSV via Tcpvcon. [Microsoft documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/tcpview) | On-demand endpoint snapshots linked to current process lifetimes, with added/removed/changed rows. Keep these separate from ETL network bytes. A connection does not establish latency, payload content or the cause of an app stall. |
| **9. PresentMon** | Frame-level ETW analysis, CPU/GPU/display timing, process filtering and per-frame CSV; MIT source. [Repository and limits](https://github.com/GameTechDev/PresentMon), [console schema](https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md), [license](https://github.com/GameTechDev/PresentMon/blob/main/LICENSE.txt) | An optional CSV importer for frame pacing before any overlay/service. Preserve process/swap-chain separation and presented-versus-displayed semantics. Treat hardware scheduling/API coverage limits as part of the evidence. |
| **10. LatencyMon** | Driver/DPC/ISR timing, hard-pagefault observations and distributions. Its technical documentation describes instrumentation that can influence measurements. [Usage](https://www.resplendence.com/latencymon_using), [technical details](https://www.resplendence.com/latencymon_technical) | Trace-backed driver latency tables and symptom-window correlation, after collector support. Vendor thresholds are not universal diagnoses. Start with report/schema comparison; an open-source reuse grant was not established. [Edition information](https://www.resplendence.com/latencymon_pro) |
| **11. Autoruns** | Autostart locations, services, scheduled tasks, signatures and CSV output through Autorunsc. [Microsoft documentation](https://learn.microsoft.com/en-us/sysinternals/downloads/autoruns) | Later: a read-only startup inventory and explicit before/after differences. Configuration presence does not prove execution or startup cost. Import a user-supplied inventory first rather than immediately implementing every startup location. |

**Adjacent reference: Process Lasso.** It documents persistent affinities/priorities, rule-based actions, activity logging and a responsiveness metric. Study how it presents activity and responsiveness, but keep automatic priority/power changes outside this roadmap: they would change WOI from an observer into a controller. Vendor performance claims were not validated here. [Bitsum](https://bitsum.com/)

**AI overlap matters.** Microsoft now documents WPA MCP and a headless ETW MCP early preview using TraceProcessing. The reviewed preview documentation specifies GitHub Copilot support and changing availability. A generic “ask questions about this trace” screen is therefore not a sufficient product distinction. A later WOI assistant should call bounded evidence queries and expose the same inspectable results as the desktop. [WPA MCP](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpa-mcp-early-preview-july-2026), [ETW MCP](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/etw-mcp-early-preview-july-2026)

## What to implement, in order

These are planning estimates for one engineer familiar with this code, including focused verification. They are not delivery commitments and exclude closing the existing release gates. Keep each item reviewable on its own.

| Order | Deliverable / estimated effort | Existing code to extend | Acceptance condition |
|---|---|---|---|
| **A1** | Historical process-family view, **3–5 days** | `EtlReader`, `EntityLifetime`, `QueryEngine`, `MainViewModel`, desktop markup | Every tree node uses a trace-local lifetime ID. Exited children remain navigable. Unknown/ambiguous parentage is explicit. Family totals count each selected lifetime once. The view says “parent/child,” never “blocked by.” |
| **A2** | Saved filters and faster evidence navigation, **3–5 days** | `EventQuery`, `EtlReader.QueryEvents`, worker dispatch, `MainViewModel`, desktop markup | Save/reopen gives the same selected event IDs for the same trace. Clearing filters restores the full query scope. Filter changes invalidate paging state. Cancellation and late replies cannot mix result sets. Start with existing normalized fields. |
| **A3** | Aggregated CPU stacks, **5–10 days**, initially imported traces only | `ITraceReader`, `EtlReader`, `QueryEngine`, worker protocol, desktop view | Aggregate the selected interval/process by captured stack. Report samples lacking stacks and symbols. Reconcile exclusive leaf totals with eligible samples/weights; inclusive frames must not be summed as a second total. Test recursion, unresolved frames and partial interval edges. |
| **B1** | Memory explanation and snapshot comparison, **2–4 days** using existing counters; **5–10 additional days** for regions | `MonitorModels`, `InvestigationAnalyzer`, comparison/UI models; separate on-demand probe for regions | Distinguish available physical, system commit, private bytes and working set. Preserve unavailable data. An owned allocation/touch/release fixture verifies semantics. Never label growth a leak without supporting evidence. |
| **B2** | Read-only endpoint snapshots, **3–6 days** | New focused acquisition function behind existing worker RPC; source descriptor and investigation attachment | Cover IPv4/IPv6 and TCP/UDP separately; retain collection timestamps and process identity confidence. PID reuse/exit races cannot attach an endpoint to the wrong lifetime. No DNS lookup is required by default. |
| **C1** | “Inspect this current hang” using WCT, **5–10 days** | New bounded on-demand worker operation, evidence attachment and dedicated UI | A known owned mutex/critical-section fixture produces the expected observed chain. Handle access denied, exit, unsupported waits and cancellation. A current observation is never presented as the explanation of an earlier incident. |
| **C2** | Recorded scheduling/wait timeline, **2–4 weeks** plus native validation | `CaptureProfiles`, `EtlReader`, new typed scheduling model, query/UI and reference tooling | Add and validate required context-switch/ready events and applicable stacks. Reconstruct running/ready/wait intervals against a known workload and WPA. Lost events, boundary states and ambiguous identities remain explicit. |
| **D1** | Optional PresentMon CSV attachment, **4–8 days** | Bounded importer, source-specific frame model, investigation attachment/UI | Validate schema/units and retain source identity. Align only with a verified clock mapping; otherwise show a separate timeline. Preserve dropped/displayed-frame distinctions and unavailable metrics. |
| **D2** | Driver latency view, **1–3 weeks** after event-schema qualification | Trace adapter, coverage model, specialized queries | Prove event pairing, driver attribution and duration/count definitions with known traces. Do not derive ISR/DPC duration from ordinary CPU sample counts. No global audio-suitability verdict. |
| **D3** | Startup inventory difference, **3–6 days** for a bounded import | Source attachment and comparison UI; separate inventory schema | Show exact added/removed/changed entries, scope and acquisition time. Exclude disabled/delete actions. Tie performance claims to a separate measured startup interval. |

For A1, start with imported ETL: current live snapshot models do not retain the same parent information. For A3, do not silently promise that today's built-in recorder produces stack-rich traces; add a separately validated capture profile only after the imported-trace view works.

Tests should extend the existing `CoreTests`, `TraceTests`, `InvestigationTests`, `ProtocolTests`, `DesktopClientTests` and relevant monitor/capture suites. Choose the cases touched by each feature; this report does not call for rebuilding the whole test strategy.

## Practical inspection plan

Use two primary deep-study targets first: **System Informer** for process/history navigation and **PerfView** for stack interpretation. Use **Procmon** and **WPA** as documented behavior and measurement references. Source study is preferable to decompilation when complete source is available.

| Investigation | Bounded scenario to run later | Evidence to retain / question answered |
|---|---|---|
| Historical navigation | One owned parent launches two short-lived children, one busy and one idle; inspect after they exit | Version/commit, scenario timing, screenshots and saved trace. Can a graph point lead to the correct exited lifetime? How is missing parent data represented? |
| Filters | Use one retained trace with a known file operation, two processes and a supplied expected event set | Filter rules, matching event IDs/counts, save/reopen behavior and cancel result. Does hiding rows preserve evidence and predictable paging? |
| CPU stacks | Analyze the same stack-bearing CPU trace in PerfView/WPA and WOI with identical interval/process/symbol settings | Raw sample/weight totals, unresolved fraction and top stacks. Explain differences before accepting the new aggregate. |
| Memory | Owned test process: baseline → reserve/commit → touch pages → release | Source timestamps and category/counter changes. Which values represent commitment versus residency? |
| Endpoints | Owned loopback client/server with a short connection and a longer connection | OS endpoint snapshot and process creation identity. What short connections are missed by polling? |
| Hangs | Owned process deliberately waits on a known synchronization object | WCT observation and, separately, scheduling trace. Which relations are observed, which unsupported, and which historical intervals remain unknown? |
| Frames | Existing graphics ETL plus associated PresentMon CSV | Clock basis, process/swap-chain identity, frame schema and reported limits. Is alignment demonstrable without guessing? |

For each candidate, freeze its exact release/commit, source URL, applicable license and artifact SHA-256 where an artifact is used. Retain expected versus observed behavior, coverage and unresolved questions. These scenarios are proposed, not executed in this research.

REA routing for a later artifact question: use `open_binary` for native artifacts, `inspect_managed_artifact` for an actual managed assembly, and `analyze_javascript_application` only for a supplied JavaScript/Electron artifact. A .NET-based product may have a native launcher, so choose by inspected file identity. Retain returned evidence IDs and limitations. Ordinary complete-source study does not need a REA session. No REA binary session was opened here.

**Access/reuse boundary:** System Informer, PerfView and PresentMon publish MIT source at the reviewed repository level. Verify the chosen revision and individual dependencies before copying or packaging code. Sysinternals publishes terms restricting reverse engineering/decompilation/disassembly except where applicable law permits; this report uses its documentation and proposes independent implementation with public APIs, without assuming binary-inspection permission. [Sysinternals terms](https://learn.microsoft.com/en-us/sysinternals/license-terms). Proprietary LatencyMon/Process Lasso internals and license permissions were not established.

## Implementation feasibility and traps

Windows provides useful public starting points: [VirtualQueryEx](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualqueryex) describes virtual regions, [QueryWorkingSetEx](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-queryworkingsetex) describes selected pages, and [GetExtendedTcpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable) provides endpoint tables. These do not provide complete VMMap classification, physical-memory accounting or network delay diagnosis by themselves.

[Wait Chain Traversal](https://learn.microsoft.com/en-us/windows/win32/debug/wait-chain-traversal) supports a defined set of synchronization mechanisms and asynchronous cancellable queries. Use it for an explicitly requested current observation in a bounded worker operation; do not put a potentially blocking wait query inside the one-second sampler. Recorded scheduling analysis is a different source and needs the C2 work above.

Keep these distinctions visible in the implementation:

- Parent/child, shared-file use, an observed wait chain and a temporal overlap are different relationships. Give them different types, labels and supporting evidence.
- Live process I/O accounting, logical file bytes, physical disk transfers and network bytes remain separate measurements.
- A high working set, a large cache or a hard fault is an observation, not a diagnosis by itself.
- Imported event stacks do not imply that the built-in profiles capture those stacks. Unsupported trace categories must stay unsupported.
- Cross-source joins require a verified clock and identity mapping. PID, process name or similar wall-clock timestamps alone are insufficient.
- New UI convenience must not remove missing-data states, paging limits, source hashes or evidence coordinates.

Do not expand this pass into a kernel driver, general plugin framework, continuous full-event recorder, memory cleaner, optimizer, malware verdict engine or cloud assistant. The existing roadmap already excludes several of these. They add substantial acquisition, safety or maintenance work before the proposed first features have demonstrated value.

## Concrete next step and stop rules

Start with **A1: historical process-family investigation**. It is visible to users, builds on existing identities and tests the desired broader execution view with a limited claim. Select one real multiprocess application trace and one controlled parent/child trace. Build the lifetime tree and family-filtered resource view, then inspect the same interval in System Informer or the reference trace tool.

Proposed usefulness gate: three people unfamiliar with the code should identify the relevant process family, inspect its recorded activity and open supporting evidence without being told which screen to use. Record completion, wrong conclusions and time; compare against their usual tool. This is a small usability check, not proof of market demand.

Stop or narrow the feature if its relationships require guesses, if its totals cannot reconcile, or if it merely duplicates an existing screen without helping the investigation. Stop collection expansion if overhead or loss obscures the event being investigated. Preserve WOI's existing release gates separately; a successful feature demo does not close them.

## Research coverage

- **Answered:** which apps fit; which documented features are worth studying; existing WOI overlap; ranked implementation slices; likely source/API/artifact routes.
- **Partially answered:** detailed acquisition feasibility and effort. Public APIs and source entry points were identified, but new collectors and competitor behavior have not been executed.
- **Unresolved:** measured user benefit, native Windows 11 behavior, overhead of proposed collectors, exact component-level reuse decisions, and binary/runtime findings.

The matrix covers **11 primary apps plus Process Lasso as an adjacent reference**. Primary publisher documentation and repositories were consulted on the research date. Repository source was read without a build/test run. Current source evidence supersedes older project-context notes; no previous plan was treated as proof of implementation.
