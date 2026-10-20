using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainViewModel
{
    public IntervalQuery? AppliedInterval { get; private set; }
    private readonly List<IntervalQuery> timelineHistory = [];
    private bool rememberTimeline = true;
    public IntervalMetric[] IntervalMetrics { get; } = Enum.GetValues<IntervalMetric>();
    public ObservableCollection<IntervalDelta> IntervalTotals { get; } = [];
    public ObservableCollection<IntervalContributorDelta> IntervalRows { get; } = [];
    [ObservableProperty] private IntervalMetric comparisonMetric = IntervalMetric.CpuMs;
    [ObservableProperty] private IntervalComparisonPage? intervalComparison;
    [ObservableProperty] private IntervalContributorDelta? selectedIntervalRow;
    [ObservableProperty] private string baselineCaption = "A: not pinned";
    [ObservableProperty] private string comparisonCaption = "B: not pinned";
    [ObservableProperty] private string intervalComparisonCaption = "Pin two ranges from this recording, then Compare A/B.";
    [ObservableProperty] private string intervalComparisonCoverage = "Same-recording lifetime matches. Differences do not establish causality.";
    [ObservableProperty] private string intervalComparisonLimits = "";
    [ObservableProperty] private string timelineCaption = "Shift-drag either chart to select; Zoom / apply updates the analysis interval.";
    private IntervalQuery? baselineInterval, comparisonInterval;

    private void ResetTimeline()
    {
        AppliedInterval = null; timelineHistory.Clear();
        baselineInterval = comparisonInterval = null;
        BaselineCaption = "A: not pinned"; ComparisonCaption = "B: not pinned";
        ClearIntervalComparison();
    }
    private void ClearIntervalComparison()
    {
        IntervalComparison = null; IntervalTotals.Clear(); IntervalRows.Clear(); SelectedIntervalRow = null;
        IntervalComparisonCaption = "Pin two ranges from this recording, then Compare A/B.";
        IntervalComparisonLimits = ""; IntervalComparisonCoverage = "Same-recording lifetime matches. Differences do not establish causality.";
    }
    private void CommitTimeline(IntervalQuery interval)
    {
        if (AppliedInterval is { } previous && previous != interval && rememberTimeline) {
            if (timelineHistory.Count == 32) timelineHistory.RemoveAt(0);
            timelineHistory.Add(previous);
        }
        AppliedInterval = interval;
        TimelineCaption = $"Applied [{interval.StartMs / 1000:F6}, {interval.EndMs / 1000:F6}) s · Shift-drag selects; Zoom / apply updates tables · {timelineHistory.Count} back steps";
    }
    partial void OnStartSecondsChanged(string value) => ChartsChanged?.Invoke();
    partial void OnEndSecondsChanged(string value) => ChartsChanged?.Invoke();

    public void SelectTimelineRange(double firstSeconds, double lastSeconds)
    {
        if (Busy || Summary == null || !double.IsFinite(firstSeconds) || !double.IsFinite(lastSeconds)) return;
        var start = Math.Clamp(Math.Min(firstSeconds, lastSeconds), 0, Summary.Session.DurationMs / 1000);
        var end = Math.Clamp(Math.Max(firstSeconds, lastSeconds), 0, Summary.Session.DurationMs / 1000);
        if (end <= start) return;
        StartSeconds = start.ToString("R", CultureInfo.InvariantCulture);
        EndSeconds = end.ToString("R", CultureInfo.InvariantCulture);
        Status = $"Selected {start:F6}–{end:F6}s. Zoom / apply to update analysis, or pin as A/B.";
    }
    private void SetTimelineRange(IntervalQuery interval)
    {
        StartSeconds = (interval.StartMs / 1000).ToString("R", CultureInfo.InvariantCulture);
        EndSeconds = (interval.EndMs / 1000).ToString("R", CultureInfo.InvariantCulture);
        SelectedProcess = Summary?.Processes.FirstOrDefault(x => x.Id == interval.ProcessId);
        Ranking = interval.Ranking;
    }
    public Task TimelineBack() => Run(async token => {
        if (timelineHistory.Count == 0 || Summary == null) return;
        var previous = timelineHistory[^1]; SetTimelineRange(previous);
        rememberTimeline = false;
        try { await RefreshCore(token); timelineHistory.RemoveAt(timelineHistory.Count - 1); CommitTimeline(previous); }
        finally { rememberTimeline = true; }
        ChartsChanged?.Invoke();
    });
    public Task TimelineFull() => Run(async token => {
        if (Summary == null) return;
        SetTimelineRange(new(0, Summary.Session.DurationMs, SelectedProcess?.Id, Ranking));
        await RefreshCore(token);
    });
    public Task TimelinePan(int direction) => Run(async token => {
        if (Summary == null || AppliedInterval is not { } current) return;
        var width = current.EndMs - current.StartMs;
        var start = Math.Clamp(current.StartMs + Math.Sign(direction) * width / 2, 0, Math.Max(0, Summary.Session.DurationMs - width));
        SetTimelineRange(current with { StartMs = start, EndMs = start + width });
        await RefreshCore(token);
    });
    private IntervalQuery BoundedInterval()
    {
        if (Summary == null) throw new WoiException("NoTrace", "Open a recording first.");
        var value = Interval();
        if (!double.IsFinite(value.StartMs) || !double.IsFinite(value.EndMs) || value.StartMs >= Summary.Session.DurationMs)
            throw new WoiException("InvalidInterval", "Choose an interval inside the recording.");
        return value with { EndMs = Math.Min(value.EndMs, Summary.Session.DurationMs) };
    }
    public Task PinInterval(bool baseline) => Run(_ => {
        var interval = BoundedInterval();
        var text = $"[{interval.StartMs / 1000:F6}, {interval.EndMs / 1000:F6}) s · {(SelectedProcess?.DisplayLabel ?? "all processes")}";
        if (baseline) { baselineInterval = interval; BaselineCaption = "A: " + text; }
        else { comparisonInterval = interval; ComparisonCaption = "B: " + text; }
        ClearIntervalComparison();
        Status = $"{(baseline ? "A" : "B")} pinned. The other range and displayed analysis scope are unchanged.";
        return Task.CompletedTask;
    });
    public Task SwapIntervals() => Run(_ => {
        (baselineInterval, comparisonInterval) = (comparisonInterval, baselineInterval);
        var a = BaselineCaption; BaselineCaption = "A:" + ComparisonCaption[2..]; ComparisonCaption = "B:" + a[2..];
        ClearIntervalComparison(); return Task.CompletedTask;
    });
    public Task CompareTraceIntervals() => Run(async token => {
        if (baselineInterval == null || comparisonInterval == null)
            throw new WoiException("MissingComparison", "Pin both A and B before comparing.");
        await LoadIntervalComparison(new(baselineInterval, comparisonInterval, ComparisonMetric), token);
    });
    private async Task LoadIntervalComparison(IntervalComparisonQuery query, CancellationToken token)
    {
        var page = await AnalysisClient.Send<IntervalComparisonPage>("compareIntervals", query, token);
        token.ThrowIfCancellationRequested();
        IntervalComparison = page; IntervalTotals.Clear(); foreach (var row in page.Totals) IntervalTotals.Add(row);
        IntervalRows.Clear(); foreach (var row in page.Rows) IntervalRows.Add(row);
        SelectedIntervalRow = IntervalRows.FirstOrDefault();
        IntervalComparisonCaption = $"B − A · {page.Query.Metric} · rows {Math.Min(page.Query.Offset + 1, page.TotalRows)}–{page.Query.Offset + page.Rows.Count} / {page.TotalRows:N0} · " +
            $"CPU samples without weights A: {page.BaselineUnknownWeights?.ToString("N0") ?? "unavailable"}, B: {page.ComparisonUnknownWeights?.ToString("N0") ?? "unavailable"}.";
        IntervalComparisonLimits = string.Join("\n", page.Limitations);
        IntervalComparisonCoverage = $"Trace-wide reported loss: {Summary!.Session.ReportedLostEvents:N0} events; {Summary.Session.Warnings.Count} source warnings (see Capture quality). " +
            "Available categories with no observations show recorded zero; unavailable data stays —. Differences do not establish causality.";
        Status = "Interval comparison loaded. Totals and rates are separate; a difference does not prove a cause.";
    }
    public Task IntervalPage(bool next) => Run(async token => {
        if (IntervalComparison is not { } page || (next && !page.More)) return;
        await LoadIntervalComparison(page.Query with { Offset = next ? page.Query.Offset + page.Query.Limit : Math.Max(0, page.Query.Offset - page.Query.Limit) }, token);
    });
    public Task IntervalEvidence(bool baseline)
    {
        var row = SelectedIntervalRow;
        var evidence = (baseline ? row?.BaselineEvidence : row?.ComparisonEvidence)?.FirstOrDefault();
        if (evidence != null) return ShowEvidenceAt(evidence.EtlxEventIndex);
        if (!Busy) Status = "No representative event is retained for this side; open its interval in Explorer.";
        return Task.CompletedTask;
    }
    public Task OpenComparedInterval(bool baseline) => Run(async token => {
        if (IntervalComparison is not { } page) return;
        SetTimelineRange(baseline ? page.Query.Baseline : page.Query.Comparison);
        await RefreshCore(token);
    });
    public Task ExportIntervalComparison(string path) => Run(_ => {
        if (IntervalComparison == null) throw new WoiException("MissingComparison", "Compare two intervals first.");
        File.WriteAllText(InvestigationStore.Local(path), JsonSerializer.Serialize(IntervalComparison, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
        Status = "Comparison totals and the displayed contributor page saved with source hash and exact ranges.";
        return Task.CompletedTask;
    });
}
