# WPA timeline and interval comparison study

Date: 2026-10-20. Read-only independent study. Target: Microsoft Windows Performance Analyzer (WPA). Scope: selection, zoom, comparison direction, unequal durations, missing data. No WPA execution, installation, capture, decompilation, or runtime parity claim.

## Evidence and conclusions

1. **Documented selection:** drag across a graph to select a time range. Highlight Selection preserves it when clicking elsewhere and displays the highlighted range across graphs; Clear Selection removes the highlight. This is distinct from zoom. Source: [Highlight a Time Interval Selection](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/highlight-a-time-interval-selection), updated 2019-01-18.
2. **Documented zoom:** Zoom to selected time range expands the chosen interval; Unzoom restores the original view. Source: [Zoom In on a Time Interval](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/zoom-in-on-a-time-interval), updated 2019-01-18.
3. **Documented synchronization:** WPA shares the timeline across a tab. Zoom synchronizes graphs, timeline, and tables. The table contains only the zoomed interval's data. Consequently, WPA zoom is an analysis-scope change, not merely a viewport transform. Sources: [Analysis Tab](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/analysis-tab), updated 2020-11-05; [Data Tables](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/data-tables), updated 2019-01-18.
4. **Documented comparison:** choose baseline and comparison explicitly. Difference = comparison minus baseline; positive means higher in comparison. WPA supports two sections of the same trace. Each side can have its own zoom range, selected by toggling the active trace. Its graph shows one trace at a time; the table compares both selected ranges. Source: [Comparative Analysis Views](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/comparative-analysis-views), updated 2019-01-18.
5. **Documented units:** sampled CPU Count and Weight are different measurements. Weight is estimated CPU milliseconds; sampling can miss short activity. The percentage depends on the visible time interval. Source: [CPU Analysis](https://learn.microsoft.com/en-us/windows-hardware/test/wpt/cpu-analysis), CPU Usage (Sampled) Graph section. Do not substitute one millisecond per sample or label a sample-count share as utilization.
6. **Documented incompleteness:** ETW can lose events; disabled providers ordinarily do not emit events. Session statistics include lost events/buffers. Source: [About Event Tracing](https://learn.microsoft.com/en-us/windows/win32/etw/about-event-tracing), Controllers, Providers, Missing Events sections; updated 2026-07-17.

**Unresolved donor behavior:** the consulted Microsoft pages and bounded searches for WPA comparison normalization do not establish automatic normalization of unequal-duration totals, per-column missing-value arithmetic, exact endpoint inclusion, or process matching across traces. Do not attribute any of these policies to WPA. Search absence is not proof the app lacks a feature. Some Learn pages display an authorization banner while providing their full article body; claims above use the returned body. The English step-by-step page returned only its outline, so the dedicated pages above supply the actual evidence.

## Minimal proposed WOI contract — design decisions, not WPA observations

- Keep a draft selection, an applied analysis interval, and a chart viewport as explicit concepts. Label actions by their effect. If Zoom is viewport-only in WOI, state that it differs from WPA and keep the applied interval visibly labeled. Alternatively, a WPA-style Zoom to selection must apply the same interval to tables and charts atomically. Never let wheel zoom silently make a chart imply a different data scope from its table.
- Use immutable baseline A and comparison B snapshots containing source hash, exact applied start/end, process lifetime scope, metric definition, coverage, and evidence coordinates. Editing the current selection does not move an already pinned baseline. Start with two intervals of the same source; source replacement clears pinned state. Scope must be explicit and identical for a minimal first version.
- Preserve the repository's half-open [start,end) event membership and clamp to actual trace bounds before recording either interval or denominator. Display both durations. Reject empty/nonfinite ranges. Do not compute exact totals by summing downsampled chart points.
- Show A, B, and signed B−A. Swapping sides changes the sign. Increased CPU or bytes means more recorded resource activity, not automatically a regression. Distinguish percent change from percentage-point change. Relative percent change is unavailable when A is zero or missing; an absolute delta can still be valid for known zero.
- For unequal durations, show totals and explicitly labeled rates together: bytes/second = recorded bytes / actual interval seconds; samples/second = count / actual seconds; sampled CPU machine utilization = 100 * weighted CPU milliseconds / (actual interval milliseconds * logical processors). Preserve unknown CPU weights as unavailable utilization. These are mathematical WOI definitions, not inferred WPA normalization.
- Do not divide latency percentiles, peak memory, or already-normalized percentages by duration. Do not call elapsed-time normalization throughput normalization: equivalent completed work is unknown unless separately established.
- A duration example: A=10s/100MiB and B=20s/200MiB gives +100MiB total but zero delta in MiB/s. A=0 known bytes and B=10 known bytes gives +10 bytes absolute but unavailable relative percentage. Missing A is neither case.
- Distinguish captured/no matching observations, unavailable provider/metric, unknown weights, and reported event loss. Inherit existing nullable contributor semantics rather than converting a missing row into zero by default. Only produce a delta when both values have compatible definitions and usable coverage. Show source-level loss as source-level; do not invent which interval lost events. Missing stack coverage does not by itself invalidate CPU samples but must remain visible in stack analysis.
- Compare same-trace process lifetime IDs, not PID/name. Cross-trace matching needs an explicit new contract; exclude it from the minimal implementation. Evidence navigation must use the frozen source and interval, not the user's current draft filter.

## Baseline WOI seams verified before implementation

Canonical checkout: D:\windows open intelligence; HEAD 5ba9bdfe917ec27726210e14249a5652daa3d2dc, with working-tree changes. These observations describe source inspected during this study, not executed behavior.

- MainWindow.xaml.cs constructor at the start of this iteration applied Shift-drag on CpuPlot directly into StartSeconds/EndSeconds and Refresh; this conflates selection and apply. RenderCharts uses the selected range for chart X limits.
- QueryEngine.Clamp validates intervals and caps end to trace duration. Contributors uses ExactRows with cached full buckets plus edge replay, rather than plotted downsampled values. Reuse this seam.
- Contributors already computes CPU utilization with interval duration and processor count. Its BaselineCpuPercent is an automatic preceding interval up to 30 seconds, not an explicitly selected A interval; do not relabel it as the new baseline comparison.
- QueryEngine.Metrics downsamples chart output; it is unsuitable for exact comparison totals.
- ComparisonEngine is an existing monitor-investigation comparison with user-confirmed equivalent work, source/schema/scope checks and coverage gates. It is not an ETL interval-comparison engine. Preserve its meaning rather than silently reusing its models for incompatible data.

## Qualification cases

1. Selection/highlight does not alter an applied result until its documented action; zoom and table scope agree with chosen WOI contract.
2. Reversed drag, trace-edge clamp, identical A/B, adjacent half-open boundaries, and unequal durations.
3. Swapping A/B negates absolute delta and preserves side-specific denominators; percentage-point delta is labeled correctly.
4. Unknown weights, missing provider, zero observations, source-level loss, PID reuse, and source replacement.
5. Cancellation leaves the old complete result; no mix of old baseline/new comparison controls. Evidence opens the correct sample/source.
6. Exact totals reconcile with independent raw ETLX selection; chart decimation does not affect results.

## Artifact/runtime boundary

A bounded local lookup found no wpa.exe on PATH or at C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\wpa.exe. A single guessed Store version path also did not resolve and is not an installation inventory. Get-AppxPackage could not load its Appx module in this host (Operation is not supported on this platform, 0x80131539). WPA availability therefore remains unconfirmed; do not claim globally absent. No local WPA runtime was started. The reference study used documentation because no usable local WPA artifact was identified in the bounded lookup. No REA binary tool ran; the invoked skill's evidence boundaries were followed. Binary/runtime parity remains unresolved.

## Implemented slice

WOI now stages Shift-drag selection on both resource plots, applies one synchronized analysis interval through Zoom / apply, and offers bounded Back, Full trace and half-width pan. Explicit A/B pins feed a same-ETL, same-process-scope comparison with raw totals, per-second rates, B-minus-A deltas, lifetime-matched contributor pages, representative evidence and page JSON export. Refresh publishes only after all queries succeed. The existing monitor-attempt comparison and automatic preceding CPU baseline retain their separate contracts.

These are WOI implementation decisions grounded in the documented workflow, not claims of complete WPA replication. Exact half-open endpoints, recorded-zero versus unavailable arithmetic, rate normalization and lifetime matching are WOI contracts. The implementation does not add cross-trace matching, arbitrary WPA table layouts or scheduling analysis.
