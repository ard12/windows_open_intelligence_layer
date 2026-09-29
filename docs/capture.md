# Guided bounded capture

Woi.Capture is a separate administrator helper. Woi.Desktop and Woi.Worker remain unprivileged. The helper accepts only two fixed profiles and a 5–120 second duration; it accepts no destination, command, arbitrary provider, or profile path from IPC.

## Ownership and transport

The UI creates a random local named pipe, with protected access for its current Windows SID and LocalSystem, and denies network access. Both endpoints verify the actual peer PID using the Windows named-pipe APIs. Elevation must use the same Windows account. Messages have correlated IDs, protocol version 1, a 64 KiB pre-parse line bound, and an allowlisted command set.

Every recording has a random folder and WPR instance named WoiCapture plus its GUID. All WPR commands put the owned instance name last. System32 WPR runs without a shell; stdout/stderr and command duration are bounded. The helper keeps the generated profile open read-only while WPR uses it.

State transitions: Idle → Starting → Recording → Saving → Saved. Cancellation and failure are explicit terminal states. Stop is idempotent after Saved. Automatic stop checks the configured deadline every half second using monotonic elapsed time, so wall-clock corrections do not extend recording. A disconnected UI or terminated parent triggers cancellation of the owned session. A stop already in progress may finish saving before disconnection cleanup; a saved ETL is preserved.

Interrupted-session recovery checks folder GUID, metadata schema, owner SID, exact instance name, and helper PID/start time. It refuses live or unverified ownership. It recognizes WPR's no-running-session result. It never cancels another tool's session.

## Profiles and persistence

Both profiles use memory mode, 128 buffers of 1,024 KiB, process/thread, loader, CPU configuration, sampled profile, disk I/O and initialization, hard faults, memory information, working-set information, and network keywords. File activity adds FileIO and FileIOInit. Stacks are not requested.

Memory buffers overwrite older history when full. Saving creates and merges an ETL and can require additional disk space. The helper checks at least 512 MiB free at start, rejects a completed ETL above the analyzer's 1 GiB limit, and computes SHA-256 before reporting Saved. A failed save may leave an ETL on disk for inspection; it is not auto-imported as a successful recording.

Capture metadata stores ownership, requested profile, duration, state, source hash, and size beside the saved ETL. These requested capabilities are kept distinct from observations found by the importer. No missing coverage becomes zero.

## Validation status

Installed WPR 10.0.19041 accepted both generated profile definitions and their configured keyword/buffer details. Controller tests use an injected runner to verify ownership, deadline, failures, cancellation, recovery, and protocol bounds. These are lifecycle tests, not native recording tests.

The local native capture attempt did not start because Windows elevation was canceled. The GitHub workflow therefore requires the production helper to record a controlled CPU/file/network/memory workload, import the ETL, resolve its lifetime and dominant CPU contribution, inspect evidence, and export reports. The workflow has not yet executed remotely. Coexistence and recovery still require native acceptance on supported machines.
