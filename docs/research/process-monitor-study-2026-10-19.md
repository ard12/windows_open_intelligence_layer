# Process Monitor filter study and WOI extraction contract

Study date: 2026-10-19. Scope: saved event filters and recorded-event navigation. This is documentation and static resource evidence, not an executed Procmon behavior comparison.

## Reference identity and evidence boundary

The live [official Microsoft page](https://learn.microsoft.com/en-us/sysinternals/downloads/procmon) identifies Process Monitor **4.12**, published October 15, 2026. A search result still said 4.11; the opened page and downloaded executable version agree on 4.12. The page documents non-destructive filtering, event-property filters, detailed events, captured process relationships and cancellable search. Its current client requirement is Windows 11 or later; the WOI validation machine is Windows 10.

Downloaded from Microsoft's [ProcessMonitor.zip](https://download.sysinternals.com/files/ProcessMonitor.zip), retained locally under `.artifacts/procmon-study/`. ZIP SHA-256: `477754bf299c9e5b311c5666f7a4a48dfdb21265eaa8f0cca70ef9d9552e3a10`. It contains Eula.txt and three executables, with no standalone CHM help file. Procmon64.exe is 2,237,720 bytes, version 4.12, SHA-256 `28495e642b2a465474a4edc58380eaf8b7ee580f3fbcb4141061deadafe9b885`.

REA `inspect_pe_resources` examined the x64 executable without launching it or opening a disassembler: evidence `ev_dc96228afd52fc229cb4e16f4751a35a46206fa3b34ae267f439b571114fd334`, provider `rea-pe-resources` version 1, complete inventory of 597 directory entries and 292 resources. Named resources include FILTER_CONTROL, ORGANIZE_FILTERS and SAVE_FILTER. This establishes shipped UI-resource presence only, not rule evaluation or persistence behavior. No named CHM/HTML resource was found; opaque payloads were not decoded. The selected inventory is retained in [procmon-resource-evidence-2026-10-19.json](procmon-resource-evidence-2026-10-19.json). No target process, tracing driver, native capture, binary decompilation or private application state was used.

## What the documentation supports

Microsoft's [Defrag Tools episode index](https://learn.microsoft.com/en-us/shows/defrag-tools/3-process-monitor) identifies Find, bookmarks and highlighting at 10:34, event navigation and quick filtering at 14:52, and load/save/organize filters at 22:48. The index was read; the video was not replayed. The [official troubleshooting walkthrough](https://learn.microsoft.com/en-us/troubleshoot/windows-client/shell-experience/troubleshoot-apps-start-failure-use-process-monitor) documents selecting a process from its tree, adding it to the include filter, and resetting filters to broaden the investigation.

The [archived Process Monitor help](https://documentation.help/Process-Monitor/Filtering_and_Highlighting.htm) is a third-party-hosted copy of product documentation, not a version-pinned Microsoft publication. It describes OR within one attribute and AND across attributes, illustrated by two process names combined with a path. Rules have a field, relation, value and include/exclude action; individual rules can be disabled. Selected-event attributes supply quick filters. Named filter sets can be saved, loaded and organized, including rename/export/import. Highlight navigation uses F4 and Shift+F4. It distinguishes normal display filtering from explicitly destructive capture-time filtering. These are historical documented behaviors; current 4.12 runtime parity remains unverified. The wording does not precisely resolve conflicting include/exclude rules, missing-field comparisons, case sensitivity, or malformed presets. We do not borrow Sysmon's separately documented semantics to fill those Procmon gaps.

## WOI implementation contract

These are independent WOI design decisions, with a deliberately narrower field set than Procmon. They are testable without copying Procmon code or its private preset format.

1. Apply reusable rules to normalized Category, Operation, Target and ProcessName. Keep interval and exact trace-local process lifetime as separate visible scope. Do not persist a raw PID or a lifetime ID into a portable preset.
2. For include rules, accept at least one rule in each field group. A field with no include rules is unrestricted. Any matching exclude rule rejects the event, regardless of include matches. Rule order does not matter. Exclusion precedence is an explicit WOI contract, not a claimed observed Procmon result. This slice adds/removes rules; individual enable/disable toggles are deferred.
3. Define text comparison and empty/missing values explicitly. Prefer ordinal case-insensitive Equals, Contains, StartsWith and EndsWith. Reject invalid fields/operators/actions and overlong values rather than interpreting them as unrestricted filters. Preserve whitespace in literal values; trim only names and user input where the UI explicitly states that behavior.
4. Save a bounded versioned local preset containing its name and reusable rules, separate from the recording and cache. Load/replace named rules predictably. Reject corrupt or future-version storage without overwriting it. A preset has no acquisition effects and does not change resource aggregation or report scope.
5. Add selected-row include/exclude actions for supported normalized fields, plus recorded-event evidence inspection and exact-lifetime navigation. Distinguish name matching (all matching captured lifetimes) from one selected lifetime. Never bind recorded PIDs or paths to current OS objects.
6. Freeze the applied query while paging. Previous/Next preserve that query even when the editor contains unapplied drafts. Apply / first page captures current controls and resets cursor history only after a successful reply. Commit successful replies atomically and keep current rows on cancellation/failure. Import clears applied queries before replacing the source; requests are serialized. Refresh captures the event query before awaiting metrics/contributors. Global interval/process controls and other scope-changing actions are locked while busy, keeping chart axes, history and event scope aligned.
7. Clear event filters restores all normalized events within the visible interval/lifetime scope. It cannot recover providers absent from the ETL or remove an independently selected interval/process scope.

The first slice should exclude generic scripts/regex, arbitrary provider payload fields, destructive filtering, PML/PMF compatibility, live Procmon capture, registry collection, cloud preset sync and external Jump To actions. Those are separate requirements, not implied by saved filters.

## Matching truth table and verification targets

For includes ProcessName=A OR ProcessName=B, plus Operation=Read, and exclude Target contains scratch:

| Event | Expected WOI result | Reason |
|---|---|---|
| A, Read, ordinary target | Included | One match in each include field |
| B, Read, ordinary target | Included | Same-field OR |
| C, Read, ordinary target | Hidden | No matching process-name include |
| A, Write, ordinary target | Hidden | Different-field AND |
| A, Read, scratch target | Hidden | Explicit exclusion override |
| A, Read, empty target | Included | Positive scratch comparison does not match |
| No rules | Included within interval/lifetime | Unrestricted event-rule layer |

Acceptance: operator/grouping/exclusion cases; invalid-rule rejection; saved-preset save/reopen/delete and corruption preservation; real-ETL exact event-ID reproducibility; first/next/previous page continuity; applied-query cursor reset; cancellation and worker recovery; unchanged source digest; UI smoke showing controls, row-derived filtering and retained evidence navigation. See [executed verification](../verification/event-filters-2026-10-19.md) for results and failed intermediate attempts. Individual rule toggles, highlighting and find-next matching are deferred.

Next extraction stage remains PerfView CPU-stack aggregation, followed by WPA scheduling/wait evidence. Complete this slice and its verification before broadening the collector or implementing another analysis model.
