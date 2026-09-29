# Product reassessment and proposed engineering plan

30 September 2026. Capacity: one full-time engineer. This is a proposal, not an implementation or a declaration that existing validation gates have passed. It supplements the existing engineering-plan.md; it does not silently replace its release requirements.

**Agreed success definition.** The user is content with a dependable, useful open-source Windows utility. A large addressable market, daily engagement, monetization, or a defensible commercial moat is not required. Judge the project by whether it helps during a real investigation, works without developer assistance, earns trust, and remains maintainable by one engineer. Occasional use can be successful. Project code remains open source under its existing MIT license; dependency obligations still apply. Funding and personal time budget are separate constraints, not assumed product requirements.

**Recommendation.** Test a local performance investigation app that helps users see what changed during a slowdown, investigate the affected application, and check whether a repeat attempt improves. Reports support this workflow. The previous CPU-only support-handoff proposal was too restrictive as a product definition: it required a particular incident, tracing workflow, and escalation recipient simultaneously. A broader user problem is plausible, but demand and frequency remain hypotheses.

**What was inspected.** README.md, architecture.md, engineering-plan.md, validation.md, pilot.md, project files, capture profiles, core models, analysis and query code, and desktop markup. No tests or native capture were rerun for this assessment. This checkout already implements WPF UI, a separate analysis worker, TraceEvent parsing, SQLite aggregates, process lifetime identities, CPU/disk heuristic incidents, contributor views, evidence navigation, capture-quality reporting, Markdown/JSON export, and bounded elevated WPR capture.

The verification record reports 58 tests, offline stress, a 1.55% native CPU-weight discrepancy against a 1% target, contributor-query p95 of 514 ms against a 300 ms target, and open native-capture and Windows 11 gates. Those are recorded results, not fresh measurements. Current capture profiles omit context-switch/ready-thread data; resource summaries cannot become wait-chain diagnoses without additional collection and analysis.

**The product in ordinary words.** Open the app and start observing while you work. When the machine or an application feels slow, mark the relevant time. See what changed, which applications contributed to the measured activity, and which conclusions the measurements support. Record more detail if needed. After making a change yourself, repeat the same task and compare its duration and activity. Save or share the investigation if useful.

History exists only after collection starts. One-second snapshots cannot reconstruct missed events or every short-lived process. A symptom predating collection gets a clear explanation and an option to record the next occurrence. No automatic startup, Windows service, or permanent detailed ETW session is needed for the initial experiment.

**Initial breadth.** Three entry points use one engine: the whole PC feels slow; one application feels slow; compare two attempts. CPU activity, memory conditions, process I/O accounting, and process lifetimes support recent history. The existing ETL analyzer provides deeper disk/file/network observations when captured. GPU frame pacing, boot diagnosis, thermals, malware, and remote-server latency are separate investigations and are not promised.

A useful answer can be a precise next measurement. High memory consumption alone does not prove a shortage; a busy application does not necessarily explain another application's delay; process I/O counters do not measure physical disk saturation. Keep those distinctions visible without making every screen a wall of caveats.

**Competition and differentiation.** History is not unique. System Informer already documents historical graph inspection, including a process that has exited. PerfView supports trace comparisons and flight recording. Microsoft documents WPA/ETW MCP previews for natural-language analysis. The hypothesis to test is that this app reduces the work across symptom selection, explanation, and verification for non-specialists. Free, local, open source, or an AI summary alone does not establish that advantage. Benchmark against participants' real workflows and System Informer, not only WPA.

**Architecture: retain the investment.** Keep .NET/WPF, CommunityToolkit, ScottPlot, TraceEvent, SQLite, Woi.Worker and the capture helper. Add Woi.Monitor as a lightweight telemetry library hosted in a separate unprivileged worker mode. A blocked ETL import must not stop observation. Reuse the existing typed process protocol rather than introducing a web server or general plugin system.

```text
Windows APIs -> monitor worker -> bounded recent history
                                         |
User marks slowdown ---------------------+-> investigation
                                         |
Existing capture helper -> ETL -> analysis worker
                                         |
                         explanation -> evidence -> comparison/report
```

Parent-liveness checks stop the monitor if the owning app disappears. Only deeper WPR recording elevates. Do not turn the capture helper into a general privileged command runner.

**Measurement and storage contract.**

- Poll at roughly one-second intervals. Collect process CPU-time deltas, private allocation and working set, system available physical memory and commit, process I/O-counter deltas, and observable process lifetimes. GetProcessTimes, GetProcessMemoryInfo, GetPerformanceInfo and GetProcessIoCounters are the initial Windows API building blocks.
- Use monotonic time for rates and record the UTC mapping for display. Represent sleep/resume, missed ticks, access-denied processes and invalid deltas as gaps or unavailable values. A first counter sample is a baseline, not a zero rate.
- Identify a process by session, PID and creation time. Unresolved creation time must not result in merging unrelated PID lifetimes.
- Define CPU percentage as CPU-time delta divided by elapsed time and supported logical processor count. Label the metric; do not assume numerical equivalence to frequency-adjusted CPU utility. Handle unsupported processor topology explicitly.
- Keep process I/O accounting separate from physical disk bytes. Keep commit, available memory, private allocation and working set separate. Do not add overlapping working sets to claim physical memory consumed.
- Sample accessible processes before selecting the display's top list. Sampling only current leaders would lose future contributors.
- Begin with a 60-minute in-memory ring bounded by both rows and bytes. Show actual retained time if process count reduces it. Persist saved investigations in batches; unsaved sessions need no steady-state disk writes.
- Keep snapshot data separate from immutable ETL caches. Every value retains source, units, interval and availability. Never fabricate an ETL event index for a snapshot.

**Core model and analysis changes.** Add Investigation, SourceDescriptor, SnapshotReference, ObservationGap, Finding and Comparison models. Investigation holds the symptom, interval, optional affected application, sources, notes and findings. Finding contains a statement, measurements, evidence references, prerequisites, limitations and an optional next measurement. Comparison holds two user-confirmed attempts, task description, time definition, comparable metrics and limitations. Reports and the UI consume the same objects.

Start with deterministic rules for sustained CPU activity, changes in memory conditions, and changes in process I/O activity. Rules declare required inputs and abstain when those inputs are missing. Use sustained conditions and explicit before/during comparisons; the preceding interval is not automatically a healthy baseline. Calibrate minimum duration and change magnitude using examples, not a universal health score.

The first screen shows the selected interval, affected app, up to three useful observations and the next step. Contributor tables and event exploration remain one level deeper. Distinguish observed measurements from possible explanations. Low CPU during an application delay can motivate a scheduling capture; it does not prove which wait caused the delay.

Review EvidenceLanguage.Explain against QueryEngine's sorting: the explanation currently identifies the sample-count leader, while contributor sorting may use weighted CPU time. Ensure descriptions, displayed ordering and units are consistent. This was identified by code inspection, not a reproduced test failure. Preserve existing CPU-weight correctness gates rather than merely relabeling a disputed estimate as verified.

**Comparison closes the workflow.** The first prototype supports manually marked attempts within a session. The later beta supports saved, separate sessions. Ask the user to identify the same task and the change they made. Show task duration as well as absolute resource use; lower CPU alone does not imply improvement. Only normalize throughput when the amount of work is known. Compare equivalent coverage; match applications across sessions with user-confirmed path/version context, never PID alone. Repeated trials address variability. Before/after observations support a hypothesis, not proof that the intervention caused the improvement.

**Engineering packages and definitions of done.** Estimates are focused engineering days. Budget about 30 implementation hours and 10 validation/user-work hours per week. Hardware access, capture defects and user scheduling affect calendar time.

| Package | Estimate | Code areas | Demonstrable result |
|---|---:|---|---|
| P0: baseline and capture triage | 2-3 days | Validation/Workload tools, capture projects, validation.md | Attempt production-helper capture on Windows 11; preserve failures, verify wording/calculations, establish known limitations. Recruit users now. |
| P1: session history | 4-6 days | New Woi.Monitor, Core models, worker protocol | Unprivileged bounded collection, explicit gaps, correct identities, marking a slowdown, CPU/memory/process-I/O timeline. |
| P2: readable investigation | 3-4 days | Analysis, findings, desktop view model/XAML | Up to three supported observations, a next measurement, basic within-session attempt comparison; attach existing ETL evidence when available. |
| P3: product experiment | 2-3 engineer days plus participant time | Pilot protocol and results ledger | Comparisons against normal workflows, independent accuracy review and reuse opportunities; decision by end of Week 3. |
| P4: saved investigations | 4-6 days | Investigation store, comparison service, Core/desktop | Save/reopen, separate-session comparison, source/coverage compatibility, shared report model. |
| P5: capture integration and correctness | 5-8 days | Capture projects, Trace/Core, validation tools | Native resource/file capture-to-evidence passes; stop/cancel/recovery/coexistence; resolve interpretation discrepancies before stable release. |
| P6: performance and distribution | 5-8 days | Monitor/worker, query engine, scripts | Measured observation cost, hard bounds, failure handling, clean Windows 11 package and signing/install decision. |
| P7: beta field iteration | 4-6 days plus elapsed use | Desktop/findings/pilot | Two rounds of observed use, corrected misunderstandings, independent successful investigations and release evidence. Reuse is supporting evidence when an opportunity occurs. |

Approximately 29-44 focused engineering days before contingency; plan roughly 8-12 calendar weeks for a credible beta if the experiment passes. A production persistent background recorder is additional work. The first three weeks test usefulness and identify the next release scope. They do not require proof of a large market or frequent repeat use. A useful small release can justify maintenance without executing the rest of the roadmap.

Utility-first release ordering: Week 1 establishes P0 and the existing capture-to-result path. Week 2 improves the first explanation and runs early supervised trials; prototype lightweight history only where observing a missed incident would materially help. Week 3 evaluates practical usefulness through P3. The first small release can center on Record -> Understand -> Inspect or Save. P1 history and P4 richer comparison remain coherent extensions, not mandatory conditions for a worthwhile utility. The full package table describes the larger 8-12-week option; re-estimate the smaller release after native-capture triage. Correctness, privilege-boundary checks and independent clean-machine use remain mandatory.

Do not wait for every maximum-size ETL optimization before obtaining user evidence. Keep existing stable-release gates; explicitly restrict and enforce the tested pilot envelope. A 514 ms query may be acceptable for a labeled early trial, but that does not close the existing 300 ms release target. Incorrect metrics and unsafe elevated operations are a different class of issue and require correction or removal from the trial path.

**Performance acceptance, all provisional targets.** Test on two named Windows 11 x64 machines, including an ordinary 8-16 GiB machine. Target average monitor CPU below 1% of machine capacity at idle and below 2% during representative work; monitor memory below 150 MiB; UI plus monitor below 300 MiB with history filled. Detailed ETL analysis has a separate budget. Unsaved sessions should generate no regular persistence writes. Run repeated workloads with monitoring on/off, aiming for less than 3% median task-duration increase; report variance and tail sampling time. These are targets, not existing measurements.

Use several two-hour sessions during the experiment. Before auto-start/background release, require 24-hour idle/workload tests, sleep/resume, process churn, memory/write bounds and crash recovery. Storage capacity bounds do not establish a low observer effect.

**Verification.** Extend risk-based tests for PID reuse, access denial, process exit between API calls, first sample, counter reset, missed ticks, clock changes, sleep, ring eviction, mismatched comparisons, and snapshot/ETL provenance. Reuse Woi.Workload for known CPU, allocation and I/O phases. Include busy innocent processes and unsupported causes. Independently compare equivalent counter definitions and reference trace output. Hold evaluation fixtures separate from tuning examples.

Native capture testing uses the shipping helper and real WPR, not only mocks. Review pipe identity, privileged writes, fixed profiles and owned-session cleanup before external use. No silent success after partial/failed import. Keep original ETLs immutable. Raw traces are excluded from default report sharing; preview included paths/command lines and avoid claims of complete anonymization. No accounts, remote telemetry or automatic remediation are needed.

**Three-week decision criteria.** Recruit 8-10 power users and hands-on IT people with recent real slowdown investigations; complete at least eight sessions across at least three independent contexts. Include whole-PC symptoms, application-specific delays and benign/unsupported/missing-data controls. Use seeded known-answer tasks alongside real incidents and counterbalanced distinct tasks. Measure from app launch to a correct next step, including setup and capture.

- At least 6/8 independently identify a useful interval and correctly explain what a finding does and does not establish.
- At least 5/8 reach an independently accepted useful next step without developer help. Document at least three real investigations where the output changes the next measurement or decision. Measure time against the normal workflow; a 30% improvement is a useful target, not a mandatory market-entry gate. Enabling a correct investigation that a user could not otherwise complete also counts as value. Compare history against System Informer when that feature is being tested.
- On at least 12 held-out scenarios, require correct expected observations/limitations on at least 10, zero confidently unsupported causal statements, and useful non-abstaining output on at least 75% of supported cases. Always saying unknown cannot pass.
- Record voluntary reuse, recommendations and saved reports when a relevant opportunity occurs. There is no mandatory short-window retention count: a utility can be valuable even if a person only needs it occasionally. Successful task completion is stronger evidence than compliments, downloads or stated intent.
- Meet provisional monitoring budgets and never silently lose history or join the wrong process lifetime.

Report-recipient approval is secondary. The primary question is whether the investigator gets value without needing to send anything to anyone. This sample tests task utility, not market size or a business model. Rare incidents do not disqualify an occasional utility. Require independent package use on at least two Windows 11 machines and record recurring support problems. Track maintenance effort during the beta and agree a sustainable personal time budget before expanding scope.

**Decision branches.** Ship and maintain a small utility if people can complete useful investigations independently and its support burden is sustainable. If history is valued but explanations fail, test whether focused comparison helps; do not add every subsystem. An expert-oriented analyzer can also be a successful open-source project. Reduce scope or pause new feature work if findings are repeatedly misleading, users get no practical benefit after iteration, or maintenance exceeds the time the owner wants to commit. Low daily engagement, a small audience, rare incidents and lack of monetization are not reasons by themselves to stop.

**Long-term project strength; a moat is optional.** Validated rules, reproducible incidents, outcome-linked comparisons, trustworthy uncertainty and community distribution can accumulate. UI features and local AI cannot prevent imitation. Private local traces create no automatic network effect. A public corpus is forkable; stewardship and validation quality would be the advantage.

**Primary sources used.**

- [System Informer](https://systeminformer.com/): historical graph/process inspection already exists.
- [WPR overview](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/introduction-to-wpr): Windows CLI, profiles and memory/file capture.
- [PerfView scenarios](https://github.com/microsoft/perfview/blob/main/documentation/Scenarios.md): capture, flight recording and comparisons.
- [WPA MCP Early Preview](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/wpa-mcp-early-preview-july-2026) and [ETW MCP Early Preview](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/etw-mcp-early-preview-july-2026): the competing AI-analysis direction, with preview limitations.
- [GetProcessTimes](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes), [GetProcessMemoryInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo), [GetPerformanceInfo](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getperformanceinfo), [GetProcessIoCounters](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getprocessiocounters): measurement building blocks, not proof of product accuracy or overhead.
