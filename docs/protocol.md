# Worker protocol v1

Each stdin/stdout line is one UTF-8 JSON object. Requests contain `id`, `version: 1`, `method`, and an object `parameters`. Replies contain the same ID/version, `kind` (progress, result, error), optional result, and error/code on failure. Maximum accepted request length is one MiB of characters. The worker is local, unprivileged, and exits on stdin EOF.

| Method | Parameters |
|---|---|
| import | path |
| cancel | requestId |
| summary / capabilities / bookmarks | empty object |
| diagnostics | empty object; returns worker PID, current/peak working set and optional last successful contributor query profile |
| metrics / contributors | startMs, endMs, optional processId and ranking (CPU, Disk, Latency) |
| compareIntervals | baseline and comparison IntervalQuery objects; metric (CpuSamples/CpuMs/DiskBytes/FileBytes/NetworkBytes/DiskOperations); offset (default 0), limit 1–500 (default 200) |
| processHistory | startMs, endMs, optional trace-local focusId, offset (default 0), limit 1–500 (default 250) |
| events | startMs, endMs, processId, category, operation, target, cursor, limit 1–500, optional rules |
| cpuStacks | startMs, endMs, optional processId, view (Functions/CallTree/Callers/Callees), grouping (Function/Module), focusKey, path, offset, limit 1–500 (default 200) |
| entity | trace-local lifetime id |
| evidence | ETLX index, optional local symbolDirectory |
| settings | cpuThreshold, diskLatencyThresholdMs, consecutiveSeconds, minimumDiskOperations |
| bookmark | id, label, startMs, endMs |
| export | local path, format (json or markdown), interval object |

Example:

```json
{"id":"open-1","version":1,"method":"import","parameters":{"path":"C:\\traces\\recording.etl"}}
{"id":"cpu-1","version":1,"method":"contributors","parameters":{"startMs":1000,"endMs":5000,"ranking":"CPU"}}
```

Intervals are half-open. IDs and ETLX indices are valid only for the active trace. An event page returns an exclusive nextCursor when more matches exist. Consecutive pages keep a forward iterator; replayed or changed queries seek to the cursor time. Invalid cursors are rejected. Queries are serialized; cancel requests bypass that gate. Input lines are bounded during reading; oversized input is discarded. The worker rejects more than 32 pending requests with WorkerBusy. Unsupported versions, malformed JSON, and unsupported methods return structured errors. No request accepts SQL, shell commands, provider activation, or an HTTP listener.

Contributor query diagnostics separate selected/baseline SQLite totals, edge replay and ranking duration, edge observation counts, raw records visited and bytes allocated on the query thread. These are developer measurements, not resource evidence or a coverage claim. The profile is null before a successful contributor query and cleared when a new one begins. Resource-only replay skips target display resolution; evidence queries still resolve captured paths and endpoints.

`processHistory` returns the source hash, applied interval, focus and resolved parent ID, total lifetime count, unresolved-parent count, a bounded preorder page and `more`. A focus includes itself and captured descendants, including exited lifetimes and those outside the interval. Each row carries its lifetime, relative depth, parent label, interval state and optional exact interval contributor measurements. Totals cover all family pages without double counting; `unattributedTotals` describes whole-interval activity without a resolved process lifetime and is excluded from every family. Invalid/cyclic parent edges remain unresolved roots. Missing activity/weights remain nullable. Parentage is not a causal or wait relationship. Query changes require resetting offset to zero; IDs are valid only in the active trace.

### ETL interval comparison

`compareIntervals` is a cancellable, serialized read-only query against the active trace. Both sides use exact half-open intervals, clamped to source duration, with the same process-lifetime ID or both whole-system. Cross-trace and cross-lifetime matching are not accepted. Full one-second aggregates plus exact boundary replay produce totals; downsampled chart points never enter comparison arithmetic. System rows are excluded when summing process rows; unresolved activity remains in the `?` group.

The response contains source hash, the clamped applied query, unknown-weight counts per side, six whole-scope totals, total contributor-row count, a bounded page and limitations. `difference = comparison - baseline`; `percentChange = 100 * difference / baseline` only when baseline is positive and both values exist. Rates divide each side's total by its own clamped duration in seconds; rate difference is B/s minus A/s. CPU-ms rates are CPU milliseconds per selected second, not percent utilization. No throughput/work normalization is inferred.

Metrics unavailable at source remain null. No matching observations in an available category yields recorded zero. CPU milliseconds are null if any selected sample has unknown weight; known sample counts remain available. This records observations, not proof of interval-wide provider availability or capture completeness. Reported event loss is trace-wide and cannot be assigned to either interval from these totals.

Contributor rows use the union of exact lifetime IDs from both ranges. Rows with two known zeros for the selected metric are omitted; unknown rows remain visible. Sort order is unavailable differences first, then descending absolute difference, then ordinal lifetime ID. Paging uses the applied query; changing sides/metric resets offset. Evidence lists are bounded representative CPU/disk observations, not exhaustive or metric-specific records. `Open A/B` enables further Explorer inspection when no representative is retained. Desktop export serializes complete totals and only the displayed page, with `totalRows`/`more` making coverage explicit.

The desktop keeps draft selection separate from the applied analysis scope and pinned A/B ranges. All responses needed by a zoom/refresh are staged before publication, so errors/cancellation retain the previous coherent view. Initial/lazy tabs use the applied scope. Reimport clears pins/history/comparison. Back history is bounded to 32 successful interval changes. Explicit stack/history/filter actions retain their own documented scope controls.

### CPU stack aggregation

`cpuStacks` is an additive, cancellable, serialized read-only query. It uses the existing exact half-open interval and trace-local process identity, independently of event filters. Responses contain source hash, applied query, selected/stack-bearing/no-stack metrics, unresolved/depth-truncated sample counts, current path totals and paged rows. Metrics contain sample count, weighted-sample count and known millisecond subtotal; `cpuMs` is null unless every sample has a known weight. A zero-count metric is zero within this selected set, not evidence of complete CPU capture.

Flat inclusive counts include each frame key at most once per sample; self counts credit the leaf. Contextual paths retain recursion. Callers/Callees focus the occurrence nearest the leaf and count each sample once. In caller paths, self still means sampled leaf, while `contextSelf` means samples whose traversal ends here at an older-stack boundary. Path conservation is `context.samples = contextSelf.samples + sum(child.inclusive.samples)`. Function/module flat inclusive values overlap and are not additive totals. Percentages divide inclusive sample counts by all selected samples.

Methods use trace-local method indices; unresolved addresses retain code-address identity qualified by lifetime. Module grouping uses trace-local module-file indices and folds adjacent frames in one module. Missing stacks receive an explicitly labelled accounting bucket. Frames beyond depth 256 receive an older-frame truncation marker, never guessed callers. Pages default to 200 rows, maximum 500; queries reject more than one million selected samples or 100,000 distinct rows rather than returning misleading partial totals. Each navigation query streams the selected events again; narrow large selections for responsiveness. Symbol attempts invalidate desktop stack state before resolution, including failure/cancellation, because address keys can become method keys. Keys are not portable across sources or symbol state. No regex folding, baseline differences or capture changes are part of this method.

### Event filter rules

`rules` is an optional array of at most 32 objects. Every rule requires `field` (ProcessName, Category, Operation, Target), `operator` (Equals, Contains, StartsWith, EndsWith), `value` (nonblank literal text, at most 512 characters), and `action` (Include, Exclude). Comparisons are ordinal case-insensitive. Any matching Exclude rejects the event; each field with Include rules must have at least one match. No Include rules means no include restriction. Empty captured fields do not match positive nonempty rules. Unsupported or missing rule members are rejected. Legacy category equality and operation/target substring constraints are combined with rules using AND.

Rule arrays are compared structurally when reusing the paging iterator across serialized requests. Clients must reset cursor/history on an applied query or source change. The desktop retains the applied query for Previous/Next and publishes rows/cursor state only after a successful, uncancelled reply. Presets are desktop-local version-1 JSON, limited to 32 names and 1 MiB total, atomically replaced after validation; corrupt/future-version files are preserved. Presets are not worker methods, trace evidence, collection configuration or resource-export filters.

## Capture protocol v1

The separate named pipe accepts capture requests with id, version, method, and object parameters, bounded to 64 KiB before JSON parsing. Replies carry the same identity/version, status or structured error/code. Methods are start (profile, durationSeconds), stop, cancel, and status. Only Resources and FileActivity profiles and durations from 5 to 120 seconds are accepted. Capture requests never accept destinations, SQL, arbitrary commands, provider names, or profile paths. The helper also monitors parent exit and IPC EOF; the fixed deadline does not depend on UI polling.

## Observation worker mode

The unprivileged worker also accepts `--monitor --parent PID --parent-start UTC_TICKS`. It runs as a separate process from ETL import. Each roughly one-second sample is kept in a bounded in-memory ring; no unsaved snapshot file is written. The worker checks its parent's PID and start time and exits if ownership disappears.

Monitor messages use the same versioned JSON request/reply envelope over stdio:

| Method | Parameters | Result |
|---|---|---|
| monitor.status | empty object | Retained duration, bounded history points, latest top processes, marks, and grouped gaps |
| monitor.lifetimes | empty object | Up to 500 process lifetimes observed in retained snapshots |
| monitor.mark | label, 1–80 characters | Mark with UTC and monotonic timestamp |
| monitor.stop | empty object | Acknowledgement and worker exit |

CPU and byte rates are nullable until a matching process lifetime has two valid counter samples. Snapshot read/write totals sum only known process rates and are labelled accounted activity. Session, PID, and creation FILETIME form the local identity. The monitor never returns ETLX indices or claims that snapshots are ETL events. Process I/O counters remain separate from physical disk transfers.
Monitor status includes cumulative session statistics (total snapshots, missed-tick gaps, sampling failures and invalid intervals), independent of the evicting retained-history diagnostics. They reset on a new monitor session. Older clients may ignore these additive fields; absent statistics mean unavailable.

## Investigation operations (protocol v1, additive)

The monitor's `monitor.investigate` accepts a typed `InvestigationRequest`: window start/end seconds from the recorded monotonic source start, symptom, optional process lifetime ID, notes, and CPU threshold/sustained-duration settings. It pins bounded immutable evidence and returns an InvestigationView, never the full process-row history to WPF. Rate integration clips partial interval edges with an explicit proportional-activity assumption; UTC is display metadata. The request must fit retained history.

Both unprivileged worker modes expose these fixed operations before an ETL is required:

| Method | Parameters | Result |
|---|---|---|
| investigation.open | path | View; saved findings are recalculated from recorded evidence |
| investigation.get | id | View of a pinned investigation |
| investigation.close | id | Release pinned evidence |
| investigation.save | id, path | Atomic versioned local SQLite container with bounded JSON evidence and SHA-256 damage checksum |
| investigation.evidence | id, reference | Snapshot detail with source, tick, recorded UTC, units/rates, optional exact lifetime, and gaps |
| investigation.compare | firstId, secondId, task, change, sameTaskConfirmed, applicationContext, applicationContextConfirmed | Duration/resource comparison and per-metric compatibility |
| investigation.export | id, path, format, includeLocalPaths (false by default) | Markdown or JSON view report; full snapshots and original ETL excluded |
| investigation.compare.export | comparison (ComparisonRequest), path, format | Shared comparison report with both source references |
| investigation.attach.report | id, report | User-attached imported ETL report; fingerprint/parser identities remain separate |

The analysis worker additionally provides `report` for the existing selected ETL interval, and `investigation.attach` (id, interval) to attach that report directly. Cross-source application comparison requires user-confirmed path/version context. Attempts below three seconds, coverage below 80% or materially different process readability suppresses resource differences; user-marked duration remains a separate measurement. No HTTP endpoint, arbitrary SQL, shell command, or privileged operation is added.

Snapshot source version is snapshot-analysis-1; saved container schema is 1. Unsupported source versions and damaged/oversized containers fail explicitly. A checksum detects accidental changes; it does not authenticate a recording. Saved prose is never used instead of recalculating from evidence. Imported ETL attachments are not revalidated without the original trace, and do not establish timestamp alignment or causality.
