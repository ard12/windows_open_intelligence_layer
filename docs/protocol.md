# Worker protocol v1

Each stdin/stdout line is one UTF-8 JSON object. Requests contain `id`, `version: 1`, `method`, and an object `parameters`. Replies contain the same ID/version, `kind` (progress, result, error), optional result, and error/code on failure. Maximum accepted request length is one MiB of characters. The worker is local, unprivileged, and exits on stdin EOF.

| Method | Parameters |
|---|---|
| import | path |
| cancel | requestId |
| summary / capabilities / bookmarks | empty object |
| diagnostics | empty object; returns worker PID and current/peak working set |
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

## Capture protocol v1

The separate named pipe accepts capture requests with id, version, method, and object parameters, bounded to 64 KiB before JSON parsing. Replies carry the same identity/version, status or structured error/code. Methods are start (profile, durationSeconds), stop, cancel, and status. Only Resources and FileActivity profiles and durations from 5 to 120 seconds are accepted. Capture requests never accept destinations, SQL, arbitrary commands, provider names, or profile paths. The helper also monitors parent exit and IPC EOF; the fixed deadline does not depend on UI polling.
