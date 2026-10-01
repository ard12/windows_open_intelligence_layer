# Worker protocol v1

Each stdin/stdout line is one UTF-8 JSON object. Requests contain `id`, `version: 1`, `method`, and an object `parameters`. Replies contain the same ID/version, `kind` (progress, result, error), optional result, and error/code on failure. Maximum accepted request length is one MiB of characters. The worker is local, unprivileged, and exits on stdin EOF.

| Method | Parameters |
|---|---|
| import | path |
| cancel | requestId |
| summary / capabilities / bookmarks | empty object |
| diagnostics | empty object; returns worker PID, current/peak working set and optional last successful contributor query profile |
| metrics / contributors | startMs, endMs, optional processId and ranking (CPU, Disk, Latency) |
| events | startMs, endMs, processId, category, operation, target, cursor, limit 1–500 |
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
