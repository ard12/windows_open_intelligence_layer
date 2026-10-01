# Architecture and evidence boundaries

```text
Canonical read-only ETL
  -> unprivileged Woi.Worker / Woi.Trace adapter
  -> SHA-256 + parser version + analysis version cache key
  -> temporary ETLX, lifetime metadata, SQLite aggregates
  -> completed cache published by directory rename
  -> typed queries over redirected stdio
  -> Woi.Desktop WPF / CommunityToolkit / ScottPlot
```

Woi.Core owns TraceSession, EntityLifetime, Observation, MetricPoint, Incident, EvidenceReference, coverage states, one-second aggregation, interval queries, deterministic descriptions, and exports. TraceEvent implementation objects remain in Woi.Trace. analysis.db stores metadata, lifetime records, and one-second process/system aggregates. Mutable bookmarks use bookmarks.db. Indexed ETLX supplies paged event detail and captured stacks.

The source is held read-only during hashing and conversion. No failed, canceled, damaged, over-limit, or truncated import receives a completed-cache marker. A crash can leave a pending directory; it is ignored when reopening. The next import creates a new staging directory. Old incompatible caches are retained rather than deleting possible evidence. Cache corruption triggers reconstruction. A per-cache file lock serializes publication. The completed manifest fingerprints ETLX, import metadata, and analysis.db; SQLite integrity/statistics and version checks are required before reuse. Bookmarks are recovered independently, including when analysis.db is corrupt.

TraceEvent process/thread indices are combined with the source hash to produce immutable trace-local IDs. Partial/rundown lifetimes retain unknown start/end flags. Resource attribution checks lifetime boundaries and uniqueness among reused PIDs. Thread identities must agree with the owning process, their lifetime boundaries, and a unique matching TID lifetime. Parent links are resolved only against a unique parent lifetime at an observed child start; unknown starts or ambiguous parents stay unresolved.

Calculations use high-resolution trace-relative milliseconds. Evidence retains QPC, source hash, parser version, provider/event identity, and ETLX event index. Wall-clock display derives from the recording's clock conversion. Imported records are never enriched with today's running processes.

Coverage is based on supported observations present in the recording. It cannot prove that a provider was enabled throughout a selection. Byte capabilities are separate from operation capabilities. Unknown schemas are inspectable but do not enter resource aggregates. Idle CPU samples do not represent busy CPU. Interrupt/DPC samples stay unattributed. Analysis 1.2 CPU weights use same-processor predecessor spacing, including idle predecessor context, with the recorded nominal interval for spacing above 110%. Missing interval metadata or nonpositive spacing stays unavailable. A trace-local sparse index makes weights independent of query edges and paging order; it retains no TraceEvent objects and observes cancellation while building. This empirically matches the named native parser on the verified fixtures; wider WPA/OS agreement remains a gate in validation.md.

Partial interval edges replay indexed events; whole one-second buckets use SQLite. Baselines use the preceding available interval, up to thirty seconds; absent usable weighted samples yield an unavailable baseline. Disk p95 uses nearest rank over completed operations. Latency sums can overlap. Working-set charts sum the latest snapshot of each captured process within a bucket; gaps are not filled, and shared pages may overlap.

Incident detection uses consecutive observed buckets. Defaults require at least 80% estimated CPU for three buckets, or disk p95 above 20 ms for three buckets with at least ten completed operations in each. These are conditions for investigation, not diagnoses. Observed records and resource attributions are labeled; no cross-process causal hypotheses are generated.

The worker serializes trace access, accepts bounded JSON requests with at most 32 pending operations, and supports cancellation outside the query gate. The UI pages 500 observations with row/column virtualization and downsamples charts to at most 2,000 visible points while retaining resource peaks. Import and replay stay off the UI thread. Local symbol resolution is requested separately and uses CacheOnly behavior.

Consecutive event pages retain one iterator and one immutable lookahead observation. Query changes or nonconsecutive cursors reset that state and seek by recorded event time. Cancellation and reader disposal release the iterator. Opening after a worker exit creates a fresh worker; previous in-flight requests fail explicitly.

Guided recording uses a separate elevated Woi.Capture helper over a SID-restricted, PID-verified local named pipe. Woi.Capture.Core owns fixed profiles, bounded lifecycle, ownership recovery, and capture protocol; see capture.md. Completed recordings enter the existing unprivileged import pipeline. Whole-bucket contributor totals use SQL grouping and a bounded sixteen-entry immutable query cache. Partial edges replay only resource-bearing records; lifecycle details remain in Explorer.

## Recent observation boundary

Woi.Monitor samples Windows process and memory APIs in a separate unprivileged Woi.Worker mode. The desktop starts it only on request and passes its PID and start time for parent-liveness checks. ETL imports run in another worker process, so a blocked import does not suspend observation. The sampler obtains process times, extended memory counters, process I/O counters, and system performance information; it samples accessible processes before selecting a top list.

Rates use monotonic elapsed time. UTC is retained for display, with clock changes and missed ticks reported as gaps. Session, PID, and creation FILETIME identify one process lifetime; a first sample or a different identity has no rate. Access-denied processes remain unreadable. Accounted process CPU is not a machine-wide utilization measurement; process I/O bytes are not physical disk bytes. Available physical memory, system commit, process private bytes, and working sets retain distinct meanings.

The in-memory ring caps snapshots, process rows, and estimated bytes. The UI reports actual retained duration and shows downsampled CPU, available-memory, and accounted process-I/O charts plus recent rows; downsampling preserves CPU peaks, memory lows, and I/O peaks. Marks are point-in-time references only. Snapshot data does not enter the immutable ETL cache or acquire an ETLX evidence coordinate. Stopping or losing the parent discards unsaved observation history.
## Investigation and comparison boundary

Core owns Investigation, SourceDescriptor, SnapshotReference, Finding, Comparison, the deterministic analyser, and the bounded workspace/store. MonitorSession copies immutable retained snapshot references under its lock and performs analysis outside the sampling lock. This keeps finding queries from serializing the sampler. Recorded Stopwatch frequency and start tick govern interval math, chart coordinates, partial-edge integration, and saved evidence; UTC rollback does not reorder history.

CPU and accounted I/O totals integrate known per-lifetime rates over overlapping measurement intervals. First-sample and missed-tick gaps stay unavailable. Memory changes use point-observation endpoints and do not establish pressure. The preceding interval is explicitly unconfirmed. At most three primary findings carry quantities, units, prerequisites, limitations, representative snapshot references, and a next measurement. The complete retained inputs used are preserved when saving.

Investigation workspaces pin at most eight documents and 250,000 process rows. The local .woi SQLite container contains one versioned bounded payload and a damage checksum; saving uses a same-directory temporary container and publishes only after successful commit. Reopening validates bounds/identities and recalculates findings. Source-specific IDs govern joins; original ETLs remain separate and are excluded from containers unless represented by report metadata only. Reports and WPF use the same computed models.

Comparisons require equivalent-work confirmation. Application matching across different lifetimes requires user-confirmed path/version context. Rate definitions, scope, measured coverage and readability govern metric compatibility. Duration and absolute resources remain separate; no causal or throughput claim is inferred. Export removes original local ETL/cache paths by default, but process names, machine metadata, notes and application context can still be private.

Live process enumeration returns PID values only. Counter and image metadata come from an opened process handle; a second creation-time check brackets the PID-based session lookup. Failed identity reads stay unreadable, and unavailable image names are explicit gaps. Enumeration grows its buffer when full and fails explicitly at its supported bound.

Worker replies serialize completely to UTF-8 before a gated write, avoiding an intermediate UTF-16 string while preserving serialization-failure atomicity and line framing. The desktop decodes typed results within the received JSON document lifetime on a background reader; it does not clone another result document. Import progress is queued to WPF. Developer diagnostics report decoding allocations/timing without changing resource evidence or forcing garbage collection.

### Direct UTF-8 reply reading

The desktop reads version-1 JSON lines from the worker's stdout byte stream into a pooled buffer. It parses and deserializes each complete reply before reusing that buffer. This removes the intermediate UTF-16 line and its subsequent UTF-8 encoding; typed result lifetimes, request IDs, progress and cancellation stay unchanged. Split UTF-8 code points, CRLF, multiple replies in one read and buffer growth are covered by regression tests. EOF inside a response fails pending requests instead of accepting a partial result. Replies above 128 MiB fail explicitly; this bounds protocol memory and does not authorize truncating imported evidence. Very large entity summaries still need a paged interface before their maximum coverage can be certified.

Soak diagnostics record non-forcing managed allocation/collection and private-byte observations. GC heap/committed/fragmentation values refer to the last collection, while working set and private bytes are process counters. None alone explains all WPF/runtime/native memory. Production and native soaks do not force collections or reduce retained coverage to satisfy a budget.

### Capture storage review status

The focused source review identifies a remaining boundary to validate: the elevated controller writes profile.wprp, capture.json.pending and a WPR output beneath a user-writable recording root. Recovery writes capture.json there directly. EnsureLocalDirectory checks existing reparse-point ancestors at a point in time; it does not establish protection against later directory replacement or existing hard-linked leaf files. This is a source-grounded hypothesis requiring isolated native validation, not a reproduced exploit or a completed security audit.

The storage design must prevent a lower-privilege process from redirecting elevated writes outside the collector's owned storage. Evaluate a protected collector scratch root, directory/leaf handle ownership and exclusive creation, atomic metadata publication, and a separate unprivileged export path. WPR's path-based output and recovery metadata need the same invariant. A CreateNew change to the pending file alone would not close directory races or WPR writes. The existing pipe peer-PID checks, per-user pipe ACL and fixed WPR commands are separate controls; they do not close this storage boundary. Portable helper integrity/signing and clean Windows 11 use also remain release gates.

No privileged filesystem attack reproduction was run. The security-audit skill requires an OS-enforced execution sandbox with no external network, an allowlisted environment, read-only source/toolchain, scratch-only writes and resource limits; this host does not provide all those controls. Continue that validation in an appropriately isolated Windows environment.

### Stable monitor display rows

Monitor history and marks preserve the matching old-suffix/new-prefix records during refresh, removing only evicted points and appending newly received points. Incompatible or revised windows still replace their full contents. Process display rows retain one trace-local lifetime identity and replace only their immutable measurement property; WPF bindings observe that update. Incoming ranking moves existing rows, while a new process lifetime receives a new row even if its PID matches an old one. Duplicate lifetime IDs and inconsistent identity fields fail before collection mutation. These display changes do not mutate saved evidence, sampling cadence, attribution, history capacity or missing measurements.

The development-only Woi.Validation --ui-allocations command launches and profiles only its own WPF process, using CLR GC allocation ticks over EventPipe with a 32 MiB buffer and bounded duration. It retains summarized type weights and reported loss, without heap dumps, raw event streams, addresses, stacks, source paths or process activity payloads. Tick weights are approximate diagnostic candidates rather than precise source-stack totals. Its short instrumented soak is separate from uninstrumented memory and observer-cost acceptance.