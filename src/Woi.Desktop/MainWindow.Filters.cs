using System.IO;
using System.Windows;
using System.Windows.Controls;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainWindow
{
    private void EventFiltersExpanded(object sender, RoutedEventArgs e) { if (LifetimePanel != null) LifetimePanel.IsExpanded = false; }
    private void EventRightClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(EventGrid, source) is DataGridRow row)
            EventGrid.SelectedItem = row.Item;
    }
    private async void SaveFilterClicked(object sender, RoutedEventArgs e) => await model!.SaveFilterPreset();
    private async void LoadFilterClicked(object sender, RoutedEventArgs e) => await model!.LoadFilterPreset();
    private async void DeleteFilterClicked(object sender, RoutedEventArgs e) => await model!.DeleteFilterPreset();
    private async void AddFilterClicked(object sender, RoutedEventArgs e) => await model!.AddFilterRule();
    private void RemoveFilterClicked(object sender, RoutedEventArgs e) => model!.RemoveFilterRule();
    private async void ClearFiltersClicked(object sender, RoutedEventArgs e) => await model!.ClearEventFilters();
    private async void PreviousEventClicked(object sender, RoutedEventArgs e) => await model!.PreviousPage();
    private async void EventFilterClicked(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((MenuItem)sender).Tag).Split(':');
        await model!.FilterFromEvent(EventGrid.SelectedItem as Observation, Enum.Parse<EventFilterField>(parts[0]), Enum.Parse<EventFilterAction>(parts[1]));
    }
    private async void EventFamilyClicked(object sender, RoutedEventArgs e)
    {
        if (model == null || model.Busy || EventGrid.SelectedItem is not Observation { ProcessId: { } id }) return;
        await model.OpenProcessFamily(id); Views.SelectedItem = ProcessHistoryTab;
    }
    private async Task VerifyEventFilters(string snapshot)
    {
        var vm = model!;
        Views.SelectedIndex = 1;
        await vm.FirstPage();
        void Check(bool condition, string message) { if (!condition || vm.Error.Length > 0) throw new InvalidOperationException(message + " " + vm.Error); }
        var first = vm.Events.ToArray();
        Check(first.Length == 500 && vm.HasNextEventPage, "Fixture needs multiple event pages.");
        vm.OperationFilter = "draft-does-not-match-any-event";
        await vm.NextPage();
        var second = vm.Events.Select(x => x.Index).ToArray();
        Check(second.Length > 0 && !first.Select(x => x.Index).Intersect(second).Any(), "Next page reused draft filters or repeated events.");
        await vm.PreviousPage();
        Check(first.SequenceEqual(vm.Events), "Previous page did not restore the original coordinates.");
        await vm.FirstPage();
        Check(vm.Events.Count == 0 && !vm.HasNextEventPage && !vm.HasPreviousEventPage, "Applying a changed filter did not reset paging.");
        vm.OperationFilter = ""; await vm.FirstPage();
        var stable = vm.Events.ToArray();
        var cancelled = vm.NextPage();
        Check(vm.Busy && !ProcessPicker.IsEnabled, "Scope controls remained editable during a query.");
        var selectedScope = vm.SelectedProcess;
        await vm.SelectContributor(vm.Contributors.FirstOrDefault(x => x.ProcessId != null));
        Check(vm.SelectedProcess == selectedScope, "A busy scope-changing action changed the query scope.");
        vm.Cancel(); await cancelled;
        Check(vm.Status.StartsWith("Operation cancelled") && stable.SequenceEqual(vm.Events) && !vm.HasPreviousEventPage, "Cancelled navigation changed the displayed page.");
        await vm.NextPage();
        Check(second.SequenceEqual(vm.Events.Select(x => x.Index)), "Retry after cancellation skipped events.");
        await vm.PreviousPage();
        var row = vm.Events.First();
        vm.FilterField = EventFilterField.Operation; vm.FilterOperator = EventFilterOperator.Equals;
        vm.FilterValue = row.Operation; vm.FilterAction = EventFilterAction.Include;
        await vm.AddFilterRule(); await vm.FirstPage();
        Check(vm.Events.Count > 0 && vm.Events.All(x => x.Operation == row.Operation), "Include rule failed.");
        vm.FilterPresetName = "CPU operation review"; await vm.SaveFilterPreset();
        Check(vm.FilterPresets.Count > 0, "Preset save failed.");
        using (var reopened = new MainViewModel(Path.ChangeExtension(snapshot, "presets.json"))) {
            Check(reopened.FilterPresets.Any(x => x.Name == vm.FilterPresetName && x.Rules.Count == 1) && reopened.AnalysisWorkerProcessId == null,
                "Preset did not survive a new view model without starting analysis.");
        }
        await vm.FilterFromEvent(row, EventFilterField.Operation, EventFilterAction.Exclude);
        Check(vm.Events.Count == 0, "Exclusion did not override inclusion.");
        await vm.LoadFilterPreset();
        Check(vm.Events.Count > 0 && vm.FilterRules.Count == 1 && !vm.HasPreviousEventPage, "Saved filter load failed.");
        vm.FilterValue = ""; await vm.AddFilterRule();
        if (vm.Error.Length == 0 || vm.Events.Count == 0) throw new InvalidOperationException("Invalid rule was not rejected without losing displayed events.");
        await vm.FirstPage();
        EventFiltersEditor.IsExpanded = true;
        EventGrid.SelectedItem = vm.Events.First();
        await Task.Delay(200); Capture(Path.ChangeExtension(snapshot, "filters.png"));
        var originalWidth = Width; var originalHeight = Height;
        Width = MinWidth; Height = MinHeight;
        await Task.Delay(200); UpdateLayout();
        Capture(Path.ChangeExtension(snapshot, "filters-small.png"));
        Check(EventGrid.ActualHeight >= 70, "Expanded filter controls leave no usable event rows at minimum window size.");
        Width = originalWidth; Height = originalHeight;
        await vm.ShowEvidence(vm.Events.First());
        Check(vm.EvidenceText.Contains(vm.Summary!.Session.SourceHash), "Filtered event lost its evidence coordinates.");
        await vm.DeleteFilterPreset();
        using (var reopened = new MainViewModel(Path.ChangeExtension(snapshot, "presets.json")))
            Check(!reopened.FilterPresets.Any(x => x.Name == "CPU operation review"), "Preset deletion did not persist.");
        await vm.ClearEventFilters(); EventFiltersEditor.IsExpanded = false;
        Check(vm.FilterRules.Count == 0 && vm.Events.Count > 0, "Clearing filters failed.");
    }
}
