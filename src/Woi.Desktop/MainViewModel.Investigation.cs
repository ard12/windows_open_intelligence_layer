using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainViewModel
{
    private readonly Dictionary<string, WorkerClient> investigationOwners = new(StringComparer.Ordinal);
    private ComparisonRequest? lastComparisonRequest;
    private WorkerClient? comparisonOwner;
    public ObservableCollection<InvestigationView> Investigations { get; } = [];
    [ObservableProperty] private InvestigationView? selectedInvestigation;
    [ObservableProperty] private bool investigationEditorOpen = true;
    [ObservableProperty] private InvestigationView? firstAttempt;
    [ObservableProperty] private InvestigationView? secondAttempt;
    [ObservableProperty] private Comparison? attemptComparison;
    [ObservableProperty] private MonitorLifetime? investigationProcess;
    [ObservableProperty] private MonitorMark? investigationMark;
    [ObservableProperty] private Finding? selectedFinding;
    [ObservableProperty] private string investigationStart = "0";
    [ObservableProperty] private string investigationEnd = "10";
    [ObservableProperty] private string investigationSymptom = "A slowdown was noticeable";
    [ObservableProperty] private string investigationNotes = "";
    [ObservableProperty] private string investigationCpuThreshold = "80";
    [ObservableProperty] private string investigationSustainedSeconds = "3";
    [ObservableProperty] private string investigationStatus = "Observe before the symptom, select an interval, then create an investigation. You can also reopen a saved investigation.";
    [ObservableProperty] private string investigationEvidenceText = "Select a finding or contributor to inspect its recorded snapshot.";
    [ObservableProperty] private string comparisonTask = "";
    [ObservableProperty] private string comparisonChange = "";
    [ObservableProperty] private string applicationContext = "";
    [ObservableProperty] private bool sameTaskConfirmed;
    [ObservableProperty] private bool applicationContextConfirmed;
    public void UseRecentObservation(bool marked = false)
    {
        if (Monitor?.Descriptor is not { } source || Monitor.History.Count == 0) {
            InvestigationStatus = "Start observing and wait for readable history first."; return;
        }
        double Seconds(long tick) => (tick - source.StartTick) / (double)source.MonotonicFrequency;
        var latest = Monitor.History[^1].MonotonicTick;
        var end = marked && InvestigationMark != null ? Math.Min(latest, InvestigationMark.MonotonicTick) : latest;
        InvestigationEditorOpen = true;
        InvestigationEnd = Seconds(end).ToString("0.######", CultureInfo.InvariantCulture);
        InvestigationStart = Math.Max(Seconds(Monitor.History[0].MonotonicTick), Seconds(end) - 30).ToString("0.######", CultureInfo.InvariantCulture);
        InvestigationStatus = "Selected retained history. Adjust the interval and affected application before creating the investigation.";
    }
    public async Task CreateInvestigation()
    {
        try {
            var owner = monitorClient ?? throw new WoiException("NoObservation", "Start observing first.");
            double Number(string value) => double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
            var request = new InvestigationRequest(new(Number(InvestigationStart), Number(InvestigationEnd)), InvestigationSymptom,
                InvestigationProcess?.LifetimeId, InvestigationNotes, new(Number(InvestigationCpuThreshold), Number(InvestigationSustainedSeconds)));
            var view = await owner.Send<InvestigationView>("monitor.investigate", request);
            if (!ReferenceEquals(owner, monitorClient)) return;
            AddInvestigation(view, owner); InvestigationStatus = "Investigation created from retained snapshot evidence.";
        } catch (Exception ex) { InvestigationStatus = ex.Message; }
    }
    private void AddInvestigation(InvestigationView view, WorkerClient owner)
    {
        var existing = Investigations.FirstOrDefault(x => x.Investigation.Id == view.Investigation.Id);
        if (existing != null) Investigations.Remove(existing);
        Investigations.Add(view); investigationOwners[view.Investigation.Id] = owner;
        SelectedInvestigation = view; InvestigationEditorOpen = false;
        if (FirstAttempt?.Investigation.Id == view.Investigation.Id) FirstAttempt = view;
        if (SecondAttempt?.Investigation.Id == view.Investigation.Id) SecondAttempt = view;
        FirstAttempt ??= view;
        if (FirstAttempt.Investigation.Id != view.Investigation.Id) SecondAttempt = view;
        SelectedFinding = view.Investigation.Findings.FirstOrDefault();
        InvestigationEvidenceText = "Select a finding or contributor to inspect its snapshot; snapshot coordinates are separate from ETLX coordinates.";
    }
    partial void OnSelectedInvestigationChanged(InvestigationView? value)
    {
        SelectedFinding = value?.Investigation.Findings.FirstOrDefault();
        InvestigationEvidenceText = "Select a finding or contributor to inspect its recorded snapshot.";
    }
    partial void OnFirstAttemptChanged(InvestigationView? value) { AttemptComparison = null; lastComparisonRequest = null; comparisonOwner = null; }
    partial void OnSecondAttemptChanged(InvestigationView? value) { AttemptComparison = null; lastComparisonRequest = null; comparisonOwner = null; }
    private WorkerClient Owner(InvestigationView? view) => view != null && investigationOwners.TryGetValue(view.Investigation.Id, out var owner) && owner.IsAlive
        ? owner : throw new WoiException("MissingInvestigation", "Open or create the investigation in a running worker first.");
    public async Task SaveInvestigation(string path)
    {
        try { var view = SelectedInvestigation!; await Owner(view).Send<object>("investigation.save", new { id = view.Investigation.Id, path }); InvestigationStatus = "Saved snapshot evidence and investigation locally. The original ETL is excluded."; }
        catch (Exception ex) { InvestigationStatus = ex.Message; }
    }
    public async Task OpenInvestigation(string path)
    {
        try {
            if (!client.IsAlive) { var previous = client; client.Dispose(); client = new(); AttachProgress(); DropInvestigations(previous); }
            var view = await client.Send<InvestigationView>("investigation.open", new { path });
            AddInvestigation(view, client); InvestigationStatus = "Reopened saved evidence; findings were recalculated with the recorded source metadata.";
        } catch (Exception ex) { InvestigationStatus = ex.Message; }
    }
    public async Task CloseInvestigation()
    {
        var view = SelectedInvestigation; if (view == null) return;
        try { await Owner(view).Send<object>("investigation.close", new { id = view.Investigation.Id }); RemoveInvestigation(view); InvestigationStatus = "Closed the pinned investigation. Saved files remain available."; }
        catch (Exception ex) { InvestigationStatus = ex.Message; }
    }
    public async Task AttachTraceToInvestigation()
    {
        try {
            var view = SelectedInvestigation!; var owner = Owner(view);
            if (Summary == null) throw new WoiException("NoTrace", "Open and select an ETL interval first.");
            InvestigationView updated;
            if (ReferenceEquals(owner, client)) updated = await owner.Send<InvestigationView>("investigation.attach", new { id = view.Investigation.Id, interval = Interval() });
            else {
                var report = await client.Send<Report>("report", Interval());
                report = report with { Contributors = report.Contributors.Take(20).ToArray(), Incidents = report.Incidents.Take(20).ToArray() };
                updated = await owner.Send<InvestigationView>("investigation.attach.report", new { id = view.Investigation.Id, report });
            }
            AddInvestigation(updated, owner); InvestigationStatus = "Attached the selected ETL evidence as a separate source. Clock alignment and causality are not inferred.";
        } catch (Exception ex) { InvestigationStatus = ex.Message; }
    }
    public async Task ShowSnapshotEvidence(SnapshotReference? reference = null)
    {
        try {
            var view = SelectedInvestigation!;
            reference ??= SelectedFinding?.Snapshots.FirstOrDefault();
            if (reference == null) throw new WoiException("MissingEvidence", "Select a finding with a snapshot reference.");
            var detail = await Owner(view).Send<SnapshotDetail>("investigation.evidence", new { id = view.Investigation.Id, reference });
            InvestigationEvidenceText = JsonSerializer.Serialize(detail, new JsonSerializerOptions(Wire.Json) { WriteIndented = true });
        } catch (Exception ex) { InvestigationStatus = ex.Message; }
    }
    public async Task CompareAttempts()
    {
        try {
            var a = FirstAttempt ?? throw new WoiException("MissingAttempt", "Choose a first attempt.");
            var b = SecondAttempt ?? throw new WoiException("MissingAttempt", "Choose a second attempt.");
            var owner = Owner(a);
            if (!ReferenceEquals(owner, Owner(b))) throw new WoiException("SeparateWorkspaces", "Save and reopen both investigations to compare evidence from separate worker sessions.");
            var request = new ComparisonRequest(a.Investigation.Id, b.Investigation.Id, ComparisonTask, ComparisonChange, SameTaskConfirmed, ApplicationContext, ApplicationContextConfirmed);
            AttemptComparison = await owner.Send<Comparison>("investigation.compare", request);
            lastComparisonRequest = request; comparisonOwner = owner;
            InvestigationStatus = "Compared user-confirmed attempts; incompatible resource measurements remain unavailable.";
        } catch (Exception ex) { AttemptComparison = null; lastComparisonRequest = null; comparisonOwner = null; InvestigationStatus = ex.Message; }
    }
    public async Task ExportInvestigation(string path, string format)
    {
        try { var view = SelectedInvestigation!; await Owner(view).Send<object>("investigation.export", new { id = view.Investigation.Id, path, format, includeLocalPaths = false }); InvestigationStatus = "Report exported. It includes process names, machine metadata, notes, and evidence references; review before sharing."; }
        catch (Exception ex) { InvestigationStatus = ex.Message; }
    }
    public async Task ExportComparison(string path, string format)
    {
        try {
            if (comparisonOwner?.IsAlive != true || lastComparisonRequest == null) throw new WoiException("MissingComparison", "Compare two confirmed attempts before exporting.");
            await comparisonOwner.Send<object>("investigation.compare.export", new { comparison = lastComparisonRequest, path, format });
            InvestigationStatus = "Comparison report exported with source references; review before sharing.";
        } catch (Exception ex) { InvestigationStatus = ex.Message; }
    }
    private void RemoveInvestigation(InvestigationView view)
    {
        Investigations.Remove(view); investigationOwners.Remove(view.Investigation.Id);
        if (SelectedInvestigation?.Investigation.Id == view.Investigation.Id) SelectedInvestigation = Investigations.LastOrDefault();
        if (FirstAttempt?.Investigation.Id == view.Investigation.Id) FirstAttempt = null;
        if (SecondAttempt?.Investigation.Id == view.Investigation.Id) SecondAttempt = null;
        AttemptComparison = null; lastComparisonRequest = null; comparisonOwner = null;
    }
    private void DropInvestigations(WorkerClient? owner)
    {
        if (owner == null) return;
        foreach (var view in Investigations.Where(x => investigationOwners.GetValueOrDefault(x.Investigation.Id) == owner).ToArray()) RemoveInvestigation(view);
    }
}
