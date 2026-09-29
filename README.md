# Windows Open Intelligence

A local Windows activity analyzer with bounded, guided WPR recording. Version **0.3.0 is an engineering preview**: the offline analyzer has real-trace stress coverage; the new privileged recording path still needs a successful native capture run. See [verification](docs/validation.md) for measured results and open gates.

## Run

Extract **WindowsOpenIntelligence-win-x64.zip** into a local folder and open **WindowsOpenIntelligence.exe**. Keep the application, worker, recording helper, DLLs, and runtime files together. The package is self-contained; users do not need Visual Studio or the .NET SDK.

Windows 11 x64 is the release baseline. Local build and desktop tests ran on Windows 10 Pro build 19045 x64. Windows 11 acceptance is still pending.

### Investigate an existing recording

1. Choose **Open recording** and a local ETL.
2. Inspect resource charts; hold **Shift** and drag on the CPU chart, or enter seconds and apply the interval.
3. Compare contributors ranked by CPU, completed disk bytes, or summed recorded latency. Double-click a contributor to inspect its lifetime.
4. Filter the paged **Explorer**, then double-click an event to see captured **Evidence**.
5. Review **Capture quality**, adjust heuristic thresholds, bookmark intervals, and export Markdown or JSON.

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
- Symbols load only from a local directory on request. No cloud analysis, HTTP service, online symbols, live recorder, custom driver, or automatic remediation is included.

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

The [Windows GitHub workflow](.github/workflows/windows.yml) builds locked dependencies, tests real ETLs, runs a native controlled recording, checks report export and desktop recovery, stresses the packaged worker, and uploads the application only after those checks succeed. It has been prepared but has not run remotely yet. Native capture coverage is intentionally a failing gate if it cannot be demonstrated.

To upload the project to GitHub, use the generated **WindowsOpenIntelligence-source.zip** contents. It contains source, locks, workflow, docs, and notices, excluding SDKs, binaries, traces, caches, and local test artifacts. Publish the separate application ZIP as a release asset after reviewing the verification gates.

The worker also has a diagnostic CLI:

~~~powershell
.\dist\WindowsOpenIntelligence\Woi.Worker.exe --analyze "C:\traces\recording.etl" --output "C:\reports\incident.json"
~~~

See [the product reassessment](docs/product-reassessment.md), [the engineering timeline](docs/engineering-plan.md), [architecture](docs/architecture.md), [capture design](docs/capture.md), [protocol](docs/protocol.md), [roadmap](docs/roadmap.md), and [contributing](CONTRIBUTING.md). Project code is MIT licensed; dependency notices are in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and licenses/.
