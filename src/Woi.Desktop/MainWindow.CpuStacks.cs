using System.Windows;
using System.IO;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainWindow
{
    private async void StackLoadClicked(object sender, RoutedEventArgs e) => await model!.LoadCpuStacks();
    private async void StackAllThreadsClicked(object sender, RoutedEventArgs e) => await model!.AllStackThreads();
    private async void StackHotPathClicked(object sender, RoutedEventArgs e) => await model!.ShowHottestPath();
    private async void StackFunctionsClicked(object sender, RoutedEventArgs e) => await model!.StackView(CpuStackView.Functions);
    private async void StackTreeClicked(object sender, RoutedEventArgs e) => await model!.StackView(CpuStackView.CallTree);
    private async void StackCallersClicked(object sender, RoutedEventArgs e) => await model!.StackView(CpuStackView.Callers);
    private async void StackCalleesClicked(object sender, RoutedEventArgs e) => await model!.StackView(CpuStackView.Callees);
    private async void StackDescendClicked(object sender, RoutedEventArgs e) => await ActivateSelection(CpuStackGrid);
    private async void StackUpClicked(object sender, RoutedEventArgs e) => await model!.StackParent();
    private async void StackPreviousClicked(object sender, RoutedEventArgs e) => await model!.StackPage(false);
    private async void StackNextClicked(object sender, RoutedEventArgs e) => await model!.StackPage(true);
    private async void StackEvidenceClicked(object sender, RoutedEventArgs e)
    {
        if (model == null || model.Busy || model.SelectedStackRow == null) return;
        await model.StackEvidence();
        if (model.Error.Length == 0 && model.Status == "Captured event loaded.") Views.SelectedIndex = 2;
    }
    private async Task VerifyCpuStacks(string snapshot)
    {
        var vm = model!;
        void Check(bool value, string message) { if (!value || vm.Error.Length > 0) throw new InvalidOperationException(message + " " + vm.Error); }
        await vm.LoadCpuStacks(); Views.SelectedItem = CpuStacksTab;
        Check(vm.CpuStacks is { WithStacks.Samples: > 0 } && vm.CpuStackRows.Count > 0, "Fixture CPU stacks are unavailable.");
        var page = vm.CpuStacks!;
        Check(page.Selected.Samples == vm.Contributors.Sum(x => x.CpuSamples ?? 0), "Stack selection does not match resource CPU sample totals.");
        var original = vm.CpuStackRows.Select(x => x.Key).ToArray();
        if (page.More) {
            await vm.StackPage(true); Check(vm.CpuStacks!.Query.Offset > 0, "Stack next page failed.");
            await vm.StackPage(false); Check(original.SequenceEqual(vm.CpuStackRows.Select(x => x.Key)), "Stack previous page changed ordering.");
        }
        var stable = vm.CpuStacks;
        var cancelled = vm.StackView(CpuStackView.CallTree);
        await vm.LoadCpuStacks(); vm.Cancel(); await cancelled;
        Check(ReferenceEquals(stable, vm.CpuStacks) && vm.Status.StartsWith("Operation cancelled"), "Cancelled stack query changed the page or restarted deferred work.");
        await vm.StackView(CpuStackView.CallTree);
        vm.SelectedStackRow = vm.CpuStackRows.FirstOrDefault(x => x.HasChildren);
        Check(vm.SelectedStackRow != null, "Fixture needs a traversable stack path.");
        var rootKeys = vm.CpuStackRows.Select(x => x.Key).ToArray();
        await vm.DescendStack(); Check(vm.CpuStacks!.Query.Path?.Count == 1, "Stack descent failed.");
        await vm.StackParent(); Check(rootKeys.SequenceEqual(vm.CpuStackRows.Select(x => x.Key)), "Stack Up failed.");
        await vm.StackView(CpuStackView.Functions);
        vm.SelectedStackRow = vm.CpuStackRows.First(x => x.Key != "missing-stack" && x.Key != "truncated-stack");
        var focus = vm.SelectedStackRow;
        await vm.StackView(CpuStackView.Callers);
        Check(vm.CpuStacks!.Context.Samples == focus.Inclusive.Samples, "Caller focus duplicated or dropped recursive samples.");
        await vm.StackView(CpuStackView.Callees);
        Check(vm.CpuStacks!.Context.Samples == focus.Inclusive.Samples, "Callee focus duplicated or dropped recursive samples.");
        await vm.StackEvidence(); Check(vm.EvidenceText.Contains(vm.Summary!.Session.SourceHash), "Stack evidence lost source identity.");
        var inspectedIndex = vm.EvidenceEventIndex;
        Views.SelectedIndex = 2;
        // Invalid directory exercises failure invalidation without downloading symbols or requiring private PDBs.
        await vm.LoadEvidenceSymbols(Path.Combine(Path.GetDirectoryName(snapshot)!, "missing-symbol-directory"));
        if (vm.Error.Length == 0 || vm.CpuStacks != null || vm.EvidenceEventIndex != inspectedIndex)
            throw new InvalidOperationException("Symbol attempt retained obsolete stack keys or changed the inspected event.");
        await vm.LoadCpuStacks(); Views.SelectedItem = CpuStacksTab;
        vm.StackGrouping = CpuStackGrouping.Module; await vm.LoadCpuStacks();
        Check(vm.CpuStacks!.Query.Grouping == CpuStackGrouping.Module && vm.CpuStackRows.Count > 0, "Module grouping failed.");
        vm.StackGrouping = CpuStackGrouping.Function; await vm.LoadCpuStacks();
        // Scope by immutable thread lifetime, then navigate its hottest accounting path.
        Contributor? process = null;
        foreach (var candidate in vm.Contributors.Where(x => x.ProcessId != null && x.CpuSamples > 0).ToArray()) {
            await vm.SelectContributor(candidate);
            if (vm.CpuStacks!.Threads?.Any(x => x.ThreadId != null && x.Metric.Samples > 0) == true) { process = candidate; break; }
        }
        Check(process != null, "Fixture has no CPU contributor with resolved thread lifetimes.");
        Check(vm.StackThreads.Count > 0, "Process scope did not expose captured thread lifetimes.");
        Check(vm.CpuStacks!.Threads!.Sum(x => x.Metric.Samples) == process!.CpuSamples && vm.StackCoverage.Contains("unattributed to a thread"), "Thread attribution does not reconcile to process samples.");
        EntityLifetime? sampledThread = null;
        foreach (var thread in vm.StackThreads.ToArray()) {
            vm.SelectedStackThread = thread; await vm.LoadCpuStacks();
            if (vm.CpuStacks!.Selected.Samples > 0) { sampledThread = thread; break; }
        }
        Check(sampledThread != null && vm.CpuStacks!.Query.ThreadId == sampledThread.Id, "Thread lifetime scope has no samples.");
        var threadTotal = vm.CpuStacks!.Selected;
        await vm.ShowHottestPath();
        Check(vm.CpuStacks!.Selected == threadTotal && vm.CpuStacks.Query.ThreadId == sampledThread!.Id &&
            vm.SelectedStackRow?.Key == vm.CpuStacks.HotPath?.Keys.LastOrDefault(), "Hottest path changed thread scope or failed to select its leaf.");
        var hotEvidence = vm.CpuStacks.HotPath!.Evidence!;
        await vm.StackEvidence();
        Check(vm.EvidenceEventIndex == hotEvidence.EtlxEventIndex && vm.EvidenceText.Contains("TID " + sampledThread!.OsId), "Hottest path evidence belongs to another thread.");
        Views.SelectedItem = CpuStacksTab; await Task.Delay(150);
        Capture(Path.ChangeExtension(snapshot, "thread-hot-path.png"));
        await VerifyExportReview(vm.CpuStacks, snapshot, "cpu-stacks");
        await vm.AllStackThreads();
        Check(vm.CpuStacks!.Query.ThreadId == null && vm.SelectedStackThread == null, "All threads retained a lifetime filter.");
        vm.SelectedProcess = null; await vm.Refresh();
        Check(vm.StackThreads.Count == 0 && vm.CpuStacks!.Query.ThreadId == null, "Changing process scope retained stale thread choices.");
        await Task.Delay(200); Capture(Path.ChangeExtension(snapshot, "cpu-stacks.png"));
        var width = Width; var height = Height; Width = MinWidth; Height = MinHeight;
        await Task.Delay(200); UpdateLayout(); Capture(Path.ChangeExtension(snapshot, "cpu-stacks-small.png"));
        Check(CpuStackGrid.ActualHeight >= 70, "CPU stack controls hide event rows at minimum window size.");
        Width = width; Height = height;
        // Reimport while the tab is selected must replace trace-local keys and reload its initial scope.
        await vm.Open(vm.Summary!.Session.SourcePath);
        Check(vm.CpuStacks is { Query.Offset: 0, Query.FocusKey: null } && vm.CpuStackRows.Count > 0, "Reimport did not reset/reload CPU stack state.");
    }
}
