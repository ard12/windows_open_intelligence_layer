# Windows Open Intelligence

A local Windows activity analyzer with optional, unprivileged recent-history observation and bounded guided WPR recording. This remains an **engineering preview**: offline analysis and observation have local checks, while the privileged recording path still needs a successful native capture run. See [verification](docs/validation.md) for measured results and open gates.

## Run

Extract **WindowsOpenIntelligence-win-x64.zip** into a local folder and open **WindowsOpenIntelligence.exe**. Keep the application, worker, recording helper, DLLs, and runtime files together. The package is self-contained; users do not need Visual Studio or the .NET SDK.

Windows 11 x64 is the release baseline. Local build and desktop tests ran on Windows 10 Pro build 19045 x64. Windows 11 acceptance is still pending.

### Investigate an existing recording

1. Choose **Open recording** and a local ETL.
2. Inspect resource charts; hold **Shift** and drag on the CPU chart, or enter seconds and apply the interval.
3. Compare contributors ranked by CPU, completed disk bytes, or summed recorded latency. Double-click a contributor to inspect its lifetime.
4. Filter the paged **Explorer**, then double-click an event to see captured **Evidence**.
5. Review **Capture quality**, adjust heuristic thresholds, bookmark intervals, and export Markdown or JSON.

### Observe a slowdown without elevation

1. Open **Observe** and choose **Start observing** before the activity you want to investigate. The app collects roughly one-second Windows API snapshots in a separate unprivileged worker.
2. When a slowdown occurs, choose **Mark slowdown**. Review accounted process CPU, available physical memory, process I/O accounting, current contributors, and the retained lifetime list.
3. Choose **Stop observing** when done. Unsaved history is in memory only and disappears when you stop or close the app. For event-level evidence, use a guided WPR recording or import an ETL.

The first sample is a counter baseline. Unreadable processes, missed ticks, clock changes, and counter resets remain visible as unavailable values or collection gaps. The ring allows up to 60 minutes but also caps process rows and estimated bytes; the displayed retained duration is the actual one.

### Investigate and compare recorded history

1. In **Observe**, start collection before reproducing the symptom. Mark the time it occurs, then open **Investigate**.
2. Choose the recent interval or marked time, describe the symptom, and optionally select an observed application lifetime. **Understand interval** shows up to three deterministic observations, their coverage/limits, and a next measurement. Double-click a contributor or inspect a finding to see its snapshot coordinates.
3. Create two investigations for equivalent attempts. Confirm the same task and the change you made before comparing. Duration and absolute resource use are shown together; incompatible measurements receive no calculated difference.
4. **Save evidence** writes a bounded local `.woi` investigation. Reopen saved files to compare separate sessions. Matching application path/version context requires explicit user confirmation; PID alone cannot match applications across sources.
5. **Attach selected ETL** preserves the selected imported trace report as a separate evidence source. Attachment does not imply clock alignment or a common cause. Export investigation/comparison reports after reviewing included names, machine metadata, notes, and references. Original ETL files and full snapshot records are excluded from report export.

Saved investigations contain recorded snapshot evidence and optional ETL report metadata, with a checksum for accidental damage detection. Findings are recalculated on reopen. This is not a tamper-proof evidence container. Local file limits are 128 MiB/container and 96 MiB/payload; pinned workspaces are bounded to eight investigations and 250,000 snapshot process rows. Close a pinned investigation to free its budget. Stopping observation discards its unsaved pinned investigations; saved files remain available.

### Record an investigation

1. Choose **Record activity**, then **Resources** or **File activity** and a duration from 5 to 120 seconds.
2. Select **Start recording** and approve Windows administrator elevation for the same Windows account. The UI and analyzer stay unprivileged.
3. Reproduce the activity. Choose **Stop and analyze**, wait for the deadline, or cancel to discard the active session.
4. A completed ETL is fingerprinted and imported automatically. Review its actual coverage, then use **Save recording copy** if you want another local copy.

Profiles request a fixed 128 MiB kernel buffer pool in WPR memory mode. Older buffers may be overwritten; the duration limit does not guarantee complete retained history. Saved recordings remain under %LOCALAPPDATA%\WindowsOpenIntelligence\recordings; derived analyzer caches are in the sibling cache directory. Recording needs at least 512 MiB free space at start. A save can still fail if space runs out.

The helper owns a unique WPR instance and cancels only that instance. Existing tracing tools can still cause provider conflicts; a failed start is reported explicitly. No global WPR cancel or automatic takeover is used. Reopening recording controls attempts recovery of interrupted, verifiably owned sessions.

## Evidence and limits

The original ETL is read-only and SHA-256 fingerprinted. Reopening verifies fingerprints of ETLX, import metadata, and aggregate SQLite. Bookmarks are stored separately. Reports exclude the original ETL, but can contain paths, command lines, endpoints, and addresses; review them before sharing.

- Import limits: 1 GiB source, five million indexed events, 2 GiB combined derived files. Exceeding a limit fails explicitly. Maximum-size ETL decoding is not certified.
- Supported observations: process/thread lifetimes, modules, sampled CPU, physical disk transfers and recorded latency, logical file operations, TCP/IP or UDP, process working-set snapshots, and hard faults when captured.
- Unavailable measurements stay nullable. Provider requests and zero reported loss do not establish complete capture.
- CPU estimates use recorded sampling intervals and processor count. Rankings use sample weights when all contributing samples have known weights; otherwise they use counts. Native WPA weights differ on one older fixture by 1.55%, above the planned 1% agreement gate.
- Physical disk bytes, logical file bytes, and network event bytes are separate measurements. Summed latency can overlap; it is not elapsed disk busy time.
- ETLX indices are derived coordinates, not original ETL byte offsets. Lifetime identities govern PID/TID joins; ambiguous attribution remains unattributed.
- Incident candidates are configurable heuristics, not diagnoses. Descriptions classify observations, resource attribution, and temporal correlation.
- Symbols load only from a local directory on request. No cloud analysis, HTTP service, online symbols, automatic startup, Windows service, custom driver, or automatic remediation is included.

## Build and verify

Use x64 Windows and .NET SDK **10.0.401**, pinned in global.json. NuGet versions are locked. You can install the SDK normally or use the local setup script:

~~~powershell
.\scripts\setup-sdk.ps1
.\scripts\build.ps1
$env:DOTNET_ROOT = (Resolve-Path .\.tools\dotnet).Path # local SDK development apphost
.\src\Woi.Desktop\bin\Debug\net10.0-windows\WindowsOpenIntelligence.exe
~~~

~~~powershell
.\scripts\test.ps1 -DownloadFixtures
.\scripts\publish.ps1
.\scripts\export-source.ps1
~~~

SDK, package, and public fixture downloads use the network during development. Product analysis remains local. Node.js is needed only for the developer replay stress script. See [reproduction commands](docs/validation.md).

The [Windows GitHub workflow](.github/workflows/windows.yml) builds locked dependencies, tests real ETLs, runs a native controlled recording, checks report export and desktop recovery, stresses the packaged worker, and uploads the application only after those checks succeed. The 2026-09-30 push triggered Actions, but GitHub rejected the job before any step because of an account payment or spending-limit issue. Native capture coverage is intentionally a failing gate if it cannot be demonstrated.

To upload the project to GitHub, use the generated **WindowsOpenIntelligence-source.zip** contents. It contains source, locks, workflow, docs, and notices, excluding SDKs, binaries, traces, caches, and local test artifacts. Publish the separate application ZIP as a release asset after reviewing the verification gates.

The worker also has a diagnostic CLI:

~~~powershell
.\dist\WindowsOpenIntelligence\Woi.Worker.exe --analyze "C:\traces\recording.etl" --output "C:\reports\incident.json"
~~~

See [the product reassessment](docs/product-reassessment.md), [the engineering timeline](docs/engineering-plan.md), [architecture](docs/architecture.md), [capture design](docs/capture.md), [protocol](docs/protocol.md), [roadmap](docs/roadmap.md), and [contributing](CONTRIBUTING.md). Project code is MIT licensed; dependency notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and licenses/.

The next-five-checkpoint status is recorded in [checkpoints](docs/checkpoints.md). The P3 [experiment protocol](docs/experiment.md) and empty results ledger distinguish preparation from participant evidence.
