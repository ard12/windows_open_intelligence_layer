using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainWindow
{
    private async void SelectionKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None || model == null || model.Busy) return;
        e.Handled = true;
        await ActivateSelection(sender);
    }

    private async Task ActivateSelection(object sender)
    {
        if (model == null || model.Busy) return;
        FrameworkElement? focus = null;
        if (sender == EventGrid && EventGrid.SelectedItem is Observation observation) {
            await model.ShowEvidence(observation);
            if (model.Error.Length == 0 && model.Status == "Captured event loaded.") { Views.SelectedIndex = 2; focus = EvidenceDetails; }
        } else if (sender == ContributorGrid && ContributorGrid.SelectedItem is Contributor contributor) {
            await model.SelectContributor(contributor);
            if (model.Error.Length == 0) { Views.SelectedIndex = 1; focus = EventGrid; }
        } else if (sender == IncidentList && IncidentList.SelectedItem is Incident incident) {
            await model.SelectIncident(incident);
        } else if (sender == HistoryGrid && model.SelectedHistoryRow != null) {
            await model.ExploreHistoryProcess();
            if (model.Error.Length == 0) { Views.SelectedIndex = 1; focus = EventGrid; }
        } else if (sender == CpuStackGrid && model.SelectedStackRow != null) {
            await model.DescendStack(); focus = CpuStackGrid;
        } else if (sender == InvestigationContributors && InvestigationContributors.SelectedItem is InvestigationContributor row) {
            if (await model.ShowSnapshotEvidence(row.Evidence)) focus = InvestigationEvidenceDetails;
        }
        if (focus != null) await Dispatcher.InvokeAsync(() => { FocusManager.SetFocusedElement(this, focus); focus.Focus(); }, DispatcherPriority.Input);
    }

    private async Task VerifyAccessibility(string snapshot)
    {
        bool itemPeerLifetimeMitigationConfigured = AppContext.TryGetSwitch("Switch.System.Windows.Automation.Peers.ItemAutomationPeerKeepsItsItemAlive", out var configured) && configured;
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>()) {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        var names = Descendants(this).OfType<FrameworkElement>()
            .Where(x => x is TextBox or ComboBox or DataGrid or ListBox)
            .Select(x => new { type = x.GetType().Name, name = UIElementAutomationPeer.CreatePeerForElement(x)?.GetName() }).ToArray();
        if (names.Length < 50 || names.Any(x => string.IsNullOrWhiteSpace(x.name)))
            throw new InvalidOperationException("A desktop input or evidence table has no accessible name: " + JsonSerializer.Serialize(names));
        var checkedActions = new List<string>();
        async Task Enter(FrameworkElement control, Func<bool> completed, string action)
        {
            await Dispatcher.InvokeAsync(() => { FocusManager.SetFocusedElement(this, control); control.Focus(); }, DispatcherPriority.Input);
            var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            control.RaiseEvent(key);
            var elapsed = Stopwatch.StartNew();
            while ((!completed() || model!.Busy) && elapsed.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(20);
            if (!key.Handled || !completed() || model!.Busy || model.Error.Length != 0)
                throw new InvalidOperationException("Keyboard activation failed: " + action + ". " + model!.Error);
            checkedActions.Add(action);
        }
        var vm = model!;
        vm.SelectedProcess = null; await vm.FirstPage();
        var observation = vm.Events.First(x => x.Category != "Unsupported");
        EventGrid.SelectedItem = observation; Views.SelectedIndex = 1;
        await Enter(EventGrid, () => Views.SelectedIndex == 2 && FocusManager.GetFocusedElement(this) == EvidenceDetails && vm.EvidenceEventIndex == observation.Index, "event to captured evidence and focus");
        ContributorGrid.SelectedItem = vm.Contributors.First(x => x.CpuSamples > 0); Views.SelectedIndex = 0;
        await Enter(ContributorGrid, () => Views.SelectedIndex == 1 && FocusManager.GetFocusedElement(this) == EventGrid && vm.SelectedProcess != null, "contributor to process events and focus");
        await vm.LoadProcessHistory(); Views.SelectedItem = ProcessHistoryTab;
        vm.SelectedHistoryRow = vm.HistoryRows.First();
        await Enter(HistoryGrid, () => Views.SelectedIndex == 1 && FocusManager.GetFocusedElement(this) == EventGrid, "historical process to events and focus");
        if (vm.SelectedInvestigation?.Investigation.Contributors.FirstOrDefault() is { } contributor) {
            var previousEvidence = vm.InvestigationEvidenceText;
            if (await vm.ShowSnapshotEvidence(contributor.Evidence with { MonotonicTick = long.MinValue }) || vm.InvestigationEvidenceText != previousEvidence)
                throw new InvalidOperationException("A failed snapshot lookup reported success or changed the displayed prior evidence.");
            checkedActions.Add("failed snapshot request preserves evidence and reports failure");
            Views.SelectedItem = InvestigationTab; InvestigationContributors.SelectedItem = contributor;
            await Enter(InvestigationContributors, () => FocusManager.GetFocusedElement(this) == InvestigationEvidenceDetails && vm.InvestigationStatus == "Recorded snapshot evidence loaded." && JsonSerializer.Deserialize<SnapshotDetail>(vm.InvestigationEvidenceText, Wire.Json)?.Reference == contributor.Evidence, "investigation contributor to snapshot evidence and focus");
        }
        File.WriteAllText(Path.ChangeExtension(snapshot, "accessibility.json"), JsonSerializer.Serialize(new {
            success = true, itemPeerLifetimeMitigationConfigured, names, checkedActions,
            limitation = "In-process WPF automation peers and routed Enter events; physical keyboard, mouse and independent assistive technology sessions remain unverified."
        }, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
    }
}
