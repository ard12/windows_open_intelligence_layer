using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ScottPlot.WPF;
using System.IO;
using System.Text.Json;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainWindow
{
    private void ConfigureTimelinePlot(WpfPlot plot)
    {
        // Explicit zoom/pan commands keep graph and analysis scope synchronized.
        plot.UserInputProcessor.Disable();
        plot.ToolTip = "Shift-drag to select a range. Zoom / apply changes both charts and analysis tables; Back restores the previous interval.";
        double At(MouseEventArgs e) {
            var point = e.GetPosition(plot);
            var figure = plot.Plot.RenderManager.LastRender.FigureRect;
            return plot.Plot.GetCoordinates(new ScottPlot.Pixel(point.X * figure.Width / Math.Max(1, plot.ActualWidth),
                point.Y * figure.Height / Math.Max(1, plot.ActualHeight))).X;
        }
        plot.PreviewMouseLeftButtonDown += (_, e) => {
            if (model?.Busy != false || model.Summary == null || (Keyboard.Modifiers & ModifierKeys.Shift) == 0) return;
            dragStart = At(e); plot.CaptureMouse(); e.Handled = true;
        };
        plot.PreviewMouseLeftButtonUp += (_, e) => {
            if (dragStart is not { } start) return;
            var end = At(e); dragStart = null; plot.ReleaseMouseCapture(); e.Handled = true;
            model?.SelectTimelineRange(start, end);
        };
        plot.LostMouseCapture += (_, _) => dragStart = null;
    }
    private async void TimelineBackClicked(object sender, RoutedEventArgs e) => await model!.TimelineBack();
    private async void TimelineFullClicked(object sender, RoutedEventArgs e) => await model!.TimelineFull();
    private async void TimelineLeftClicked(object sender, RoutedEventArgs e) => await model!.TimelinePan(-1);
    private async void TimelineRightClicked(object sender, RoutedEventArgs e) => await model!.TimelinePan(1);
    private async void PinBaselineClicked(object sender, RoutedEventArgs e) => await model!.PinInterval(true);
    private async void PinComparisonClicked(object sender, RoutedEventArgs e) { await model!.PinInterval(false); Views.SelectedItem = IntervalComparisonTab; }
    private async void CompareIntervalsClicked(object sender, RoutedEventArgs e) => await model!.CompareTraceIntervals();
    private async void SwapIntervalsClicked(object sender, RoutedEventArgs e) => await model!.SwapIntervals();
    private async void IntervalPreviousClicked(object sender, RoutedEventArgs e) => await model!.IntervalPage(false);
    private async void IntervalNextClicked(object sender, RoutedEventArgs e) => await model!.IntervalPage(true);
    private async void OpenBaselineClicked(object sender, RoutedEventArgs e) { await model!.OpenComparedInterval(true); Views.SelectedIndex = 0; }
    private async void OpenComparisonClicked(object sender, RoutedEventArgs e) { await model!.OpenComparedInterval(false); Views.SelectedIndex = 0; }
    private async void IntervalBaselineEvidenceClicked(object sender, RoutedEventArgs e) => await InspectInterval(true);
    private async void IntervalComparisonEvidenceClicked(object sender, RoutedEventArgs e) => await InspectInterval(false);
    private async Task InspectInterval(bool baseline)
    {
        if (model == null || model.Busy) return;
        await model.IntervalEvidence(baseline);
        if (model.Error.Length == 0 && model.Status == "Captured event loaded.") Views.SelectedIndex = 2;
    }
    private void ExportIntervalComparisonClicked(object sender, RoutedEventArgs e)
    {
        if (model?.Busy == false && model.IntervalComparison is { } report) ReviewAndExport(report, "interval-comparison", jsonOnly: true);
    }
    private async Task VerifyTimelineComparison(string snapshot)
    {
        var vm = model!;
        void Check(bool condition, string message) {
            if (!condition || vm.Error.Length > 0) throw new InvalidOperationException(message + " " + vm.Error);
        }
        await vm.TimelineFull(); Views.SelectedIndex = 0;
        var duration = vm.Summary!.Session.DurationMs / 1000;
        var full = vm.AppliedInterval!;
        var oldEvents = vm.Events.Select(x => x.Index).ToArray();
        vm.SelectTimelineRange(duration * .33, duration * .11);
        Check(vm.AppliedInterval == full && vm.Interval().StartMs < vm.Interval().EndMs &&
            oldEvents.SequenceEqual(vm.Events.Select(x => x.Index)), "Draft selection changed the applied interval or event page.");
        await vm.LoadCpuStacks(true); await vm.LoadProcessHistory(true);
        Check(vm.CpuStacks!.Query.StartMs == full.StartMs && vm.ProcessHistory!.StartMs == full.StartMs,
            "Automatic tab loading used the unapplied selection.");
        RenderCharts();
        foreach (var plot in new[] { CpuPlot, DiskPlot }) {
            var span = plot.Plot.GetPlottables<ScottPlot.Plottables.HorizontalSpan>().Single().GetAxisLimits();
            Check(Math.Abs(span.Left - vm.Interval().StartMs / 1000) < 1e-8 &&
                Math.Abs(span.Right - vm.Interval().EndMs / 1000) < 1e-8, "Selection highlight is not on the time axis.");
        }
        await Task.Delay(100); Capture(Path.ChangeExtension(snapshot, "timeline-selection.png"));
        await vm.PinInterval(true);
        vm.SelectTimelineRange(duration * .44, duration * .88); await vm.PinInterval(false);
        vm.ComparisonMetric = IntervalMetric.CpuSamples; await vm.CompareTraceIntervals();
        var original = vm.IntervalComparison;
        Check(original is { TotalRows: > 0 } && original.Totals[0].Baseline > 0, "Interval comparison is empty.");
        var cancelled = vm.CompareTraceIntervals(); vm.Cancel(); await cancelled;
        Check(ReferenceEquals(original, vm.IntervalComparison) && vm.Status.StartsWith("Operation cancelled"), "Cancellation replaced the comparison.");
        vm.ComparisonMetric = (IntervalMetric)999; await vm.CompareTraceIntervals();
        if (vm.Error.Length == 0 || !ReferenceEquals(original, vm.IntervalComparison))
            throw new InvalidOperationException("Invalid comparison replaced its previous result.");
        vm.ComparisonMetric = IntervalMetric.CpuSamples; await vm.CompareTraceIntervals();
        await vm.OpenComparedInterval(true);
        var appliedA = vm.AppliedInterval!;
        Check(vm.Contributors.Sum(x => x.CpuSamples ?? 0) == original!.Totals[0].Baseline, "Opened A disagrees with comparison samples.");
        var beforeMetrics = vm.Metrics.ToArray(); var beforeStacks = vm.CpuStacks; var beforeHistory = vm.ProcessHistory;
        vm.SelectTimelineRange(duration * .44, duration * .88);
        vm.StackGrouping = (CpuStackGrouping)999; await vm.Refresh(); // Fails after metrics/events/history queries.
        if (vm.Error.Length == 0 || vm.AppliedInterval != appliedA || !beforeMetrics.SequenceEqual(vm.Metrics) ||
            !ReferenceEquals(beforeStacks, vm.CpuStacks) || !ReferenceEquals(beforeHistory, vm.ProcessHistory))
            throw new InvalidOperationException("Late refresh failure published mixed interval state.");
        vm.StackGrouping = CpuStackGrouping.Function; await vm.Refresh();
        Check(vm.Contributors.Sum(x => x.CpuSamples ?? 0) == original.Totals[0].Comparison, "Applied B disagrees with comparison samples.");
        await vm.TimelineBack();
        Check(vm.AppliedInterval == appliedA, "Timeline Back did not restore A.");
        await vm.TimelinePan(1);
        Check(vm.AppliedInterval!.StartMs > appliedA.StartMs, "Timeline pan failed.");
        RenderCharts();
        var cpu = CpuPlot.Plot.Axes.GetLimits(); var disk = DiskPlot.Plot.Axes.GetLimits();
        Check(cpu.Left == disk.Left && cpu.Right == disk.Right &&
            Math.Abs(cpu.Left - vm.AppliedInterval.StartMs / 1000) < 1e-8, "Timeline chart axes are not synchronized.");
        await vm.OpenComparedInterval(false);
        var evidenceRow = vm.IntervalRows.First(x => x.ComparisonEvidence.Count > 0); vm.SelectedIntervalRow = evidenceRow;
        await vm.IntervalEvidence(false);
        Check(vm.EvidenceEventIndex == evidenceRow.ComparisonEvidence[0].EtlxEventIndex &&
            vm.EvidenceText.Contains(original.SourceHash), "Comparison evidence used the wrong source event.");
        await vm.SwapIntervals(); await vm.CompareTraceIntervals();
        Check(vm.IntervalComparison!.Totals[0].Difference == -original.Totals[0].Difference, "Swapping A/B did not reverse the delta.");
        await vm.SwapIntervals(); await vm.CompareTraceIntervals();
        var export = Path.ChangeExtension(snapshot, "interval-comparison.json");
        await vm.ExportIntervalComparison(export);
        var saved = JsonSerializer.Deserialize<IntervalComparisonPage>(File.ReadAllText(export), Wire.Json)!;
        Check(saved.SourceHash == original.SourceHash && saved.Query == original.Query, "Comparison export lost source/range identity.");
        await VerifyExportReview(vm.IntervalComparison, snapshot, "interval-comparison");
        Views.SelectedItem = IntervalComparisonTab; await Task.Delay(150);
        Capture(Path.ChangeExtension(snapshot, "interval-comparison.png"));
        var width = Width; var height = Height; Width = MinWidth; Height = MinHeight;
        await Task.Delay(150); UpdateLayout(); Capture(Path.ChangeExtension(snapshot, "interval-comparison-small.png"));
        Check(IntervalComparisonGrid.ActualHeight >= 65, "Comparison controls hide contributor rows at minimum window size.");
        Width = width; Height = height;
        Views.SelectedIndex = 0;
        await vm.Open(vm.Summary.Session.SourcePath);
        Check(vm.IntervalComparison == null && vm.BaselineCaption == "A: not pinned" && vm.ComparisonCaption == "B: not pinned" &&
            vm.AppliedInterval!.StartMs == 0, "Reimport retained source-local A/B state.");
    }
}
