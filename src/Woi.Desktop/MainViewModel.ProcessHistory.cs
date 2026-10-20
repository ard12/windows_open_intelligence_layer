using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainViewModel
{
    internal bool ProcessHistoryVisible { get; set; }
    private bool historyLoadPending;
    public ObservableCollection<ProcessHistoryDisplayRow> HistoryRows { get; } = [];
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HistoryCaption)), NotifyPropertyChangedFor(nameof(HistoryTotalsText))]
    private ProcessHistoryPage? processHistory;
    [ObservableProperty] private ProcessHistoryDisplayRow? selectedHistoryRow;

    public string HistoryCaption => ProcessHistory is not { } page ? "Open a recording to explore captured process families." :
        $"{(page.Focus == null ? "All captured families" : page.Focus.DisplayLabel + " and descendants")} · " +
        $"{page.StartMs / 1000:n3}–{page.EndMs / 1000:n3}s · " +
        $"{page.TotalLifetimes:n0} lifetimes · rows {(page.Rows.Count == 0 ? 0 : page.Offset + 1):n0}–{page.Offset + page.Rows.Count:n0} · " +
        $"{page.UnresolvedParents:n0} unresolved parents";
    public string HistoryTotalsText {
        get {
            if (ProcessHistory is not { } page) return "Totals cover the selected family and interval, including every page.";
            static string Number(double? value) => value?.ToString("N2", CultureInfo.CurrentCulture) ?? "unavailable";
            var t = page.Totals;
            return $"Interval totals across all {page.TotalLifetimes:n0} lifetimes: CPU samples {Number(t.CpuSamples)} · " +
                $"sampled CPU {Number(t.CpuMs)} ms · physical disk {Number(t.DiskBytes / 1048576d)} MiB · " +
                $"logical file {Number(t.FileBytes / 1048576d)} MiB · network events {Number(t.NetworkBytes / 1048576d)} MiB. " +
                $"Unattributed activity is excluded; whole-interval unattributed CPU samples: {Number(page.UnattributedTotals.CpuSamples)}.";
        }
    }
    private void ClearProcessHistory()
    {
        ProcessHistory = null; SelectedHistoryRow = null; HistoryRows.Clear();
        historyLoadPending = ProcessHistoryVisible;
    }
    private async Task LoadProcessHistoryCore(CancellationToken token, string? focusId, int offset, bool useCurrentInterval, IntervalQuery? scope = null)
    {
        if (Summary == null) return;
        var interval = scope ?? (useCurrentInterval || ProcessHistory == null ? Interval() : new IntervalQuery(ProcessHistory.StartMs, ProcessHistory.EndMs));
        var page = await AnalysisClient.Send<ProcessHistoryPage>("processHistory",
            new ProcessHistoryQuery(interval.StartMs, interval.EndMs, focusId, offset), token);
        token.ThrowIfCancellationRequested();
        PublishProcessHistory(page);
    }
    private void PublishProcessHistory(ProcessHistoryPage page)
    {
        var selectedId = SelectedHistoryRow?.Row.Entity.Id;
        HistoryRows.Clear(); foreach (var row in page.Rows) HistoryRows.Add(new(row));
        ProcessHistory = page;
        historyLoadPending = false;
        SelectedHistoryRow = HistoryRows.FirstOrDefault(x => x.Row.Entity.Id == selectedId) ?? HistoryRows.FirstOrDefault();
    }
    public Task LoadProcessHistory(bool useAppliedInterval = false)
    {
        if (Busy) { historyLoadPending = true; return Task.CompletedTask; }
        historyLoadPending = false;
        return Run(token => LoadProcessHistoryCore(token, ProcessHistory?.Focus?.Id, 0, true, useAppliedInterval ? AppliedInterval : null));
    }
    public Task OpenProcessFamily(string? id) => Run(token => LoadProcessHistoryCore(token, id, 0, true));
    public Task FocusHistoryFamily() => Run(async token => {
        if (SelectedHistoryRow is { } row) await LoadProcessHistoryCore(token, row.Row.Entity.Id, 0, false);
    });
    public Task HistoryParent() => Run(async token => {
        if (ProcessHistory?.FocusParentId is { } id) await LoadProcessHistoryCore(token, id, 0, false);
    });
    public Task AllHistoryFamilies() => Run(token => LoadProcessHistoryCore(token, null, 0, false));
    public Task HistoryPage(bool next) => Run(async token => {
        if (ProcessHistory is not { } page || (next && !page.More)) return;
        await LoadProcessHistoryCore(token, page.Focus?.Id, next ? page.Offset + page.Rows.Count : Math.Max(0, page.Offset - 250), false);
    });
    public async Task ExploreHistoryProcess()
    {
        if (Busy || SelectedHistoryRow is not { } selected || ProcessHistory is not { } page) return;
        SelectedProcess = selected.Row.Entity;
        StartSeconds = (page.StartMs / 1000).ToString("0.######", CultureInfo.InvariantCulture);
        EndSeconds = (page.EndMs / 1000).ToString("0.######", CultureInfo.InvariantCulture);
        await Refresh();
    }
}

public sealed record ProcessHistoryDisplayRow(ProcessHistoryRow Row)
{
    public string Name => string.IsNullOrWhiteSpace(Row.Entity.Name) ? "Unnamed captured process" : Row.Entity.Name;
    public Thickness Indent => new(Math.Min(Row.Depth, 12) * 14, 0, 0, 0);
    public string Start => Row.Entity.StartKnown ? (Row.Entity.StartMs / 1000).ToString("N3") : "Unknown / rundown";
    public string End => Row.Entity.EndKnown ? (Row.Entity.EndMs / 1000).ToString("N3") : "Unknown / trace end";
}
