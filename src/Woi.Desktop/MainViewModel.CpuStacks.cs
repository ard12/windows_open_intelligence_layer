using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainViewModel
{
    internal bool CpuStacksVisible { get; set; }
    private bool stackLoadPending;
    public ObservableCollection<CpuStackRow> CpuStackRows { get; } = [];
    public CpuStackGrouping[] StackGroupings { get; } = Enum.GetValues<CpuStackGrouping>();
    [ObservableProperty] private CpuStackGrouping stackGrouping;
    [ObservableProperty] private CpuStackPage? cpuStacks;
    [ObservableProperty] private CpuStackRow? selectedStackRow;
    [ObservableProperty] private string stackCaption = "Open a recording and choose Use interval to inspect captured CPU stacks.";
    [ObservableProperty] private string stackCoverage = "No stack analysis loaded.";
    [ObservableProperty] private string stackPath = "";
    [ObservableProperty] private string stackContext = "";
    private readonly List<string> stackPathNames = [];
    private void ClearCpuStacks()
    {
        CpuStacks = null; CpuStackRows.Clear(); SelectedStackRow = null; stackPathNames.Clear(); StackPath = "";
        StackCoverage = "No stack analysis loaded."; StackContext = "";
        StackCaption = "Use interval to inspect captured CPU stacks."; stackLoadPending = CpuStacksVisible;
    }
    public Task LoadCpuStacks(bool useAppliedInterval = false)
    {
        if (Busy) { stackLoadPending = true; return Task.CompletedTask; }
        return Run(async token => {
            if (Summary == null) return;
            var interval = useAppliedInterval && AppliedInterval != null ? AppliedInterval : Interval();
            await LoadCpuStacksCore(new(interval.StartMs, interval.EndMs, interval.ProcessId, Grouping: StackGrouping), [], token);
        });
    }
    private async Task LoadCpuStacksCore(CpuStackQuery query, IReadOnlyList<string> names, CancellationToken token)
    {
        var page = await AnalysisClient.Send<CpuStackPage>("cpuStacks", query, token);
        token.ThrowIfCancellationRequested();
        PublishCpuStacks(page, names);
    }
    private void PublishCpuStacks(CpuStackPage page, IReadOnlyList<string> names)
    {
        CpuStacks = page; CpuStackRows.Clear(); foreach (var row in page.Rows) CpuStackRows.Add(row);
        SelectedStackRow = CpuStackRows.FirstOrDefault(); stackLoadPending = false;
        stackPathNames.Clear(); stackPathNames.AddRange(names);
        StackPath = names.Count == 0 ? "Root" : string.Join(" → ", names);
        var process = page.Query.ProcessId == null ? "all processes" : Summary?.Processes.FirstOrDefault(x => x.Id == page.Query.ProcessId)?.DisplayLabel ?? page.Query.ProcessId;
        StackCaption = $"{page.Query.View} · {page.Query.Grouping} grouping · {page.Query.StartMs/1000:F3}–{page.Query.EndMs/1000:F3}s · {process}";
        static string Ms(CpuStackMetric value) => value.CpuMs?.ToString("N3") ?? $"unavailable ({value.KnownCpuMs:N3} ms from {value.WeightedSamples:N0} weighted samples)";
        StackCoverage = $"{page.Selected.Samples:N0} selected CPU samples · sampled CPU {Ms(page.Selected)} · " +
            $"{page.WithStacks.Samples:N0} with captured stacks / {page.WithoutStacks.Samples:N0} without · " +
            $"{page.UnresolvedSamples:N0} with unresolved frames · {page.TruncatedSamples:N0} truncated stacks.";
        StackContext = $"This path: {page.Context.Samples:N0} samples; {page.ContextSelf.Samples:N0} end here · " +
            $"rows {(page.Rows.Count == 0 ? 0 : page.Query.Offset + 1):N0}–{page.Query.Offset + page.Rows.Count:N0} of {page.TotalRows:N0}. Percentages use all selected CPU samples.";
        Status = "CPU stacks loaded. Event filters do not affect this analysis; sampled CPU does not measure blocked or waiting time.";
    }
    public Task StackView(CpuStackView view) => Run(async token => {
        if (CpuStacks is not { } page) return;
        var focus = view is CpuStackView.Callers or CpuStackView.Callees ? SelectedStackRow?.Key : null;
        if (view is CpuStackView.Callers or CpuStackView.Callees && focus == null) return;
        await LoadCpuStacksCore(page.Query with { View = view, FocusKey = focus, Path = null, Offset = 0 }, [], token);
    });
    public Task DescendStack() => Run(async token => {
        if (CpuStacks is not { } page || SelectedStackRow is not { } row || page.Query.View == CpuStackView.Functions || !row.HasChildren) return;
        await LoadCpuStacksCore(page.Query with { Path = (page.Query.Path ?? []).Append(row.Key).ToArray(), Offset = 0 }, stackPathNames.Append(row.Name).ToArray(), token);
    });
    public Task StackParent() => Run(async token => {
        if (CpuStacks is not { } page || page.Query.Path is not { Count: > 0 } path) return;
        await LoadCpuStacksCore(page.Query with { Path = path.Take(path.Count - 1).ToArray(), Offset = 0 }, stackPathNames.Take(stackPathNames.Count - 1).ToArray(), token);
    });
    public Task StackPage(bool next) => Run(async token => {
        if (CpuStacks is not { } page || next && !page.More) return;
        await LoadCpuStacksCore(page.Query with { Offset = next ? page.Query.Offset + page.Rows.Count : Math.Max(0, page.Query.Offset - page.Query.Limit) }, stackPathNames.ToArray(), token);
    });
    public Task StackEvidence() => SelectedStackRow is not { } row ? Task.CompletedTask : ShowEvidenceAt(row.Evidence.EtlxEventIndex);
}
