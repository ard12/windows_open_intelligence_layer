# Export review and thread CPU workflow verification

20 October 2026. The first three candidates from the [feature backlog](../research/feature-reverse-engineering-backlog-2026-10-20.md) are implemented and pass local checks. The Release build has zero warnings/errors; **166 tests pass, with zero failures or skips**. A self-contained x64 desktop run passes the new workflows and existing analysis/recovery/observation/investigation checks on Windows 10 build 19045. This does not close Windows 11 or privileged-collector release gates.

## Implemented behavior

| Feature | Delivered behavior | Evidence boundary |
|---|---|---|
| R1 export review | One shared renderer and modal review for ETL reports, investigations, saved-attempt comparisons, interval comparison pages and CPU-stack pages. Six initially excluded text categories, field counts, JSON/Markdown preview, explicit prefix limit, and destination selection after acceptance. | Structured measurements, nulls, coverage statuses, fingerprints, exact timestamps, numeric IDs and evidence coordinates remain. Field selection is not anonymization or secret detection. |
| R2 thread CPU scope | Optional immutable `ThreadId` flows through the query, worker, reader, desktop and export. Process-owned thread choices list busiest first. All-thread totals separate resolved lifetimes from unattributed samples and reconcile to process totals. | TID is a display attribute. Unresolved samples remain in All threads and are never assigned to a plausible thread. Sampled CPU cannot describe waiting. |
| R3 hottest sampled path | A bounded single pass builds contextual paths and selects the largest sample-count child, using ordinal frame-key tie breaks. A dominant self leaf ends descent. The tree opens at the selected leaf's parent and supports representative-event inspection. | Counts, not CPU-weight estimates, determine this action. Missing/truncated stacks remain accounting entries. Unresolved frames remain captured addresses/module offsets. |

No dependencies, capture providers, automatic symbol downloads, release versions or tags changed. The existing WPF, typed stdio protocol, trace adapter and nullable measurements are reused.

## Export contract

`ReportExport` copies the supported report model before processing. Known name, machine, path, endpoint and command-line fields follow their switches. Opaque notes, findings, provider/event text and warnings require all five identifier categories plus explicit free-text inclusion, because those strings can mix identifiers. Opted-in fields are verbatim. Unknown text identifiers become stable `ref:` values; validated recorded lifetime/frame identities remain usable. Source evidence is unchanged.

JSON preserves report measurements and structure, blanks excluded text and appends `exportPrivacy` schema 1. Markdown includes a measurement table, disclosure notice and the complete reviewed JSON; opted-in table text escapes HTML and Markdown links. Default reports can omit narrative limitations, so the exported notice directs readers to review capture quality locally. `.woi` remains a separate evidence save and does not apply report omissions.

The dialog shows the complete payload up to 65,536 characters; larger previews explicitly show only a prefix. Saving applies the same selections throughout the full payload. Writes use a same-directory temporary file and atomic rename. Cancellation, invalid format/path or a persistent sharing lock preserves the existing destination. Transient Windows sharing/access-denied errors receive at most one second of retries. `.json`/`.md` extension checks and recorded source/cache exclusions protect evidence destinations.

The worker adds read-only `exportReport` and `investigation.compare.report` operations. Existing export RPCs accept optional `privacy` and use conservative defaults when omitted. CPU queries add optional `threadId` and `hottestPath`; responses add hot-path metadata and per-thread accounting. The stdio protocol remains version 1 with additive fields. CLI report files use the shared renderer; diagnostic stdout can still contain identifiers.

## Executed checks

| Check | Result |
|---|---|
| Locked restore using existing packages | Passed with SDK 10.0.402/runtime 10.0.13; offline package source and advisory retrieval disabled for this restore. Dependency lock files are unchanged. |
| Release solution build | Passed, zero warnings/errors. |
| Full fixture-aware suite | 166 passed, 0 failed, 0 skipped; approximately 61 seconds. |
| Export-focused repetition | Seven export/coverage cases passed in three consecutive isolated runs before the final Markdown-escaping case was added; the final suite includes that case. |
| Real ETL stack reconciliation | Allocator and .NET 4.5.2 fixtures pass recorded stack/count/weight checks, exact thread filtering, ownership rejection, hot-path evidence lookup, cancellation and original-file SHA-256 preservation. |
| Synthetic edge cases | Same TID across distinct lifetime IDs, unattributed accounting, unknown weights, recursion, self-dominant paths, deterministic ties, empty scopes and a 100,001-node rejection are covered. |
| Privacy and write failures | Marker omissions across all five report shapes and nested attachments; explicit inclusions; stable references; exact numeric/null/loss preservation; preview/output equality; cancellation; original/cache path rejection; transient/persistent locks and Markdown text escaping pass. |
| Self-contained publish | Worker, capture helper and WPF publish for `win-x64`; no SDK required by the tested app. The helper was packaged but not elevated or invoked for capture. |
| Packaged desktop | Passed on the 418,378-indexed-event allocator fixture. Modal reviews exercise cancel, initial exclusions, all fields, dependent free-text disabling, format selection, preview and written-payload equality across all five report types. |
| Thread and path desktop workflow | Passed process/thread selection, reconciled thread accounting, hottest-path selection, captured event/TID inspection, clearing scope and reimport. |
| Layout and existing workflows | Passed minimum-size result-table assertions, event filters, timeline A/B comparison, process history, worker recovery, saved investigation reopening, snapshot evidence and keyboard/automation-peer checks. Screenshots of the review and thread views were inspected. |

An initial UI check selected a CPU contributor whose samples lacked resolved thread attribution; it now selects a contributor with recorded resolved-thread evidence, while keeping unresolved samples explicit. A separate minimum-window failure was fixed by bounding and scrolling the CPU control area. The final native run passes both assertions. A deliberate held-file test also exposed Windows' access-denied variant of rename failure; both lock error forms are covered.

Native smoke automation drives actual WPF modal controls and the shared file writer. It does not automate the operating system's Save File picker or constitute a physical mouse/keyboard or independent screen-reader session. The allocator fixture has no qualifying recorded parent-family edge, so its optional family-edge assertion remains false rather than being reported as a pass.

## Reproduction and retained evidence

Use the SDK pinned by `global.json`, an existing package cache, and the fixture directory:

```powershell
dotnet restore WindowsOpenIntelligence.slnx --locked-mode
dotnet build WindowsOpenIntelligence.slnx --no-restore -c Release --disable-build-servers
$env:WOI_FIXTURES = '<fixture-directory>'
$env:DOTNET_HOST_PATH = '<pinned-sdk-directory>\dotnet.exe'
dotnet test tests/Woi.Tests/Woi.Tests.csproj --no-build --no-restore -c Release
# After publishing worker/helper/desktop into one self-contained directory:
.\WindowsOpenIntelligence.exe --smoke-test '<allocator.etl>' '<scratch>\desktop.png' --verify-recovery --verify-monitor --verify-investigations
```

The local validation used the already installed pinned SDK and task scratch on J: because D: had limited free space. Source and the pre-existing working-tree snapshot remain on D:. Ignored evidence is retained at `.artifacts/first-three-20261020/` (build/test logs, TRX, restore log, baseline copies, exact validation scripts). The self-contained app and native screenshots/results are at `J:\Codex\woi-first-three-20261020\app` and `J:\Codex\woi-first-three-20261020\e2e`. These private local outputs include live snapshot evidence and are not publication assets. The adjacent [redacted result](first-three-features-2026-10-20.json) records assertions and source/binary hashes without copying that evidence.

The starting checkout was based on `610f2c1` with in-progress monitor/accessibility/CI edits. Those edits were independently committed as `6ae5768` during this work; the feature changes remain layered on that commit. Earlier bytes/hashes were saved before editing, and unrelated monitor, capture-test, dependency and workflow files remain unchanged from that commit. No reset, stash, history rewrite, push, tag or release publication was performed for this packet.

## Open gates

Independent Windows 11 acceptance, WPA GUI comparison, external user investigations, maximum-envelope/long-duration benchmarks, fresh dependency-advisory verification and the privileged helper's trust/storage qualification remain separate. This packet establishes the three local workflows and bounded regression checks; it makes no new low-overhead, universal-visibility, causality or stable-release claim.
