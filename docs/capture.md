# Guided bounded capture

Woi.Capture is a separate administrator helper. Woi.Desktop and Woi.Worker remain unprivileged. The helper accepts only two fixed profiles and a 5–120 second duration; it accepts no destination, command, arbitrary provider, or profile path from IPC.

## Ownership and transport

The UI creates a random local named pipe, with protected access for its current Windows SID and LocalSystem, and denies network access. Both endpoints verify the actual peer PID using the Windows named-pipe APIs. Elevation must use the same Windows account. Messages have correlated IDs, protocol version 1, a 64 KiB pre-parse line bound, and an allowlisted command set.

Every recording has a random folder and WPR instance named WoiCapture plus its GUID. All WPR commands put the owned instance name last. System32 WPR runs without a shell; stdout/stderr and command duration are bounded. The helper keeps the generated profile open read-only while WPR uses it.

State transitions: Idle → Starting → Recording → Saving → Saved. Cancellation and failure are explicit terminal states. Stop is idempotent after Saved. Automatic stop checks the configured deadline every half second using monotonic elapsed time, so wall-clock corrections do not extend recording. A disconnected UI or terminated parent triggers cancellation of the owned session. A stop already in progress may finish saving before disconnection cleanup; a saved ETL is preserved.

Interrupted-session recovery checks folder GUID, metadata schema, owner SID, exact instance name, and helper PID/start time. It refuses live or unverified ownership. It recognizes WPR's no-running-session result. It never cancels another tool's session.

## Profiles and persistence

Both profiles use memory mode, 128 buffers of 1,024 KiB, process/thread, loader, CPU configuration, sampled profile, disk I/O and initialization, hard faults, memory information, working-set information, and network keywords. File activity adds FileIO and FileIOInit. Stacks are not requested.

Memory buffers overwrite older history when full. Saving creates and merges an ETL and can require additional disk space. The helper checks at least 2 GiB free at start, rejects a completed ETL above the analyzer's 1 GiB limit, and computes size and SHA-256 from one validated read handle before reporting Saved. A failed save may leave an ETL on disk for inspection; it is not auto-imported as a successful recording.

Capture metadata stores ownership, requested profile, duration, state, source hash, and size beside the saved ETL. These requested capabilities are kept distinct from observations found by the importer. No missing coverage becomes zero.

## Protected storage

The production helper creates `WindowsOpenIntelligence.Collector\<current Windows SID>` on its application's volume, requiring a fixed local NTFS drive. Running the D: package places this storage on D:. Administrators and SYSTEM have full control; the signed-in user has read/execute access to their subtree. An inherited OWNER RIGHTS ACE grants only READ_CONTROL, suppressing the file owner's implicit permission-changing rights. Completed recordings are copied to a chosen destination by the ordinary UI, without passing that destination to elevated IPC.

Directory creation supplies protected ACLs atomically. Existing folders must already have the exact owner/permission policy; the helper rejects mismatches and never repairs or adopts a user-writable tree. Keep existing evidence and ask an administrator to inspect a conflicting folder. Legacy AppData recordings are preserved but never used for privileged recovery. Collector files are not automatically deleted; administrators must manage retained files after saving needed user-owned copies.

Opened directory handles validate attributes and security from the handles, reject reparse points, and prevent directory/ancestor replacement until capture or recovery ends. Untrusted ancestors also deny write sharing. Validated protected directories allow writer sharing so atomic child-file publication works; their ACLs deny ordinary-user writes. Actual writes use a volume-GUID path obtained from the opened directory, avoiding later DOS-drive remapping. Profile and pending metadata files use exclusive creation. Reads reject redirects and multiple hard links, and metadata publication flushes a fresh temporary file before same-directory replacement.

WPR receives the protected volume-GUID profile/output paths and TEMP, TMP and SystemTemp environment values. This does not prove that out-of-process WPR services honor those environment values or never write on C:. Native profile loading, merging, service temporary writes, ACL inheritance and split-token access checks remain release acceptance work.

## Validation status

Installed WPR 10.0.19041 accepted both generated profile definitions and their configured keyword/buffer details. Controller tests use an injected runner to verify ownership, deadline, failures, cancellation, recovery, and protocol bounds. These are lifecycle tests, not native recording tests.

The initial Windows 10 elevation attempt was canceled. On 2 October, a later Resources recording started through the production helper but its WPR save exceeded the 60-second command timeout. FileActivity was not attempted in that run. An incomplete file is preserved locally and is not a completed capture. The 13 October protected-storage change supersedes the earlier location; normal controller regressions exercise it with a fake WPR runner. Windows 10/11 capture-to-report acceptance, isolated privileged storage attacks and portable helper trust validation remain open.

On 1 October, [the full CI run](https://github.com/ard12/win_OiL/actions/runs/36841914783) succeeded on Windows Server 2025. Both production profiles passed start, controlled CPU/file/network/memory workload, manual save, fingerprint preservation, import, workload lifetime/ranking, CPU evidence and report export. FileActivity logical bytes were 135,329,692; both profiles captured 16,777,216 network transfer bytes and more than 128 MiB working set. Requested coverage still differs from observed coverage: Resources did not capture logical file bytes. Later [server lifecycle checks](https://github.com/ard12/win_OiL/actions/runs/36849115092) passed deadline/cancel/disconnect/recovery/coexistence; these do not close desktop OS acceptance. See [checkpoints](checkpoints.md) and verification/github-native-20261001.json.
