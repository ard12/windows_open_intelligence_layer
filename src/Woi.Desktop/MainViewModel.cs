using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private WorkerClient client = new();
    private CancellationTokenSource? operation;
    private long? nextCursor;
    private bool more;
    public ObservableCollection<Observation> Events { get; } = [];
    public ObservableCollection<Contributor> Contributors { get; } = [];
    public ObservableCollection<MetricPoint> Metrics { get; } = [];
    public ObservableCollection<Bookmark> Bookmarks { get; } = [];
    [ObservableProperty] private Summary? summary;
    [ObservableProperty] private string status = "Open a Windows Performance Recorder ETL to begin.";
    [ObservableProperty] private string error = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(CanRecord))] private bool busy;
    [ObservableProperty] private string startSeconds = "0";
    [ObservableProperty] private string endSeconds = "10";
    [ObservableProperty] private EntityLifetime? selectedProcess;
    [ObservableProperty] private string ranking = "CPU";
    [ObservableProperty] private string category = "";
    [ObservableProperty] private string operationFilter = "";
    [ObservableProperty] private string targetFilter = "";
    [ObservableProperty] private string explanation = "Choose a recorded interval to see which processes contributed and inspect their evidence.";
    [ObservableProperty] private string evidenceText = "Select an event to inspect the captured record.";
    [ObservableProperty] private string cpuThreshold = "80";
    [ObservableProperty] private string latencyThreshold = "20";
    [ObservableProperty] private string consecutiveSeconds = "3";
    [ObservableProperty] private string minimumDiskOperations = "10";
    [ObservableProperty] private string bookmarkLabel = "Selected interval";
    public event Action? ChartsChanged;
    public MainViewModel()
    {
        AttachProgress();
    }
    internal int WorkerProcessId=>client.ProcessId;
    private void AttachProgress() => client.Progress += value =>
        System.Windows.Application.Current.Dispatcher.Invoke(()=>Status=value.Message);
    public IntervalQuery Interval()
    {
        if(!double.TryParse(StartSeconds, NumberStyles.Float, CultureInfo.InvariantCulture,out var start) ||
            !double.TryParse(EndSeconds,NumberStyles.Float,CultureInfo.InvariantCulture,out var end) ||
            !double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
            throw new WoiException("InvalidInterval","Enter start and end seconds, with end after start.");
        return new(start*1000,end*1000,SelectedProcess?.Id,Ranking);
    }
    private async Task Run(Func<CancellationToken,Task> action)
    {
        if(Busy) return;
        Busy = true; Error = ""; operation = new();
        try { await action(operation.Token); }
        catch(OperationCanceledException) { Status = "Operation cancelled. The original recording was preserved."; }
        catch(Exception ex) { Error = ex.Message; Status = "The operation could not be completed."; }
        finally { operation.Dispose(); operation = null; Busy = false; }
    }
    public Task Open(string path) => Run(async token => {
        if(!client.IsAlive){client.Dispose();client=new();AttachProgress();}
        Summary = null; SelectedProcess = null; Category = "All categories"; OperationFilter = ""; TargetFilter = ""; EvidenceText = "Select an event to inspect the captured record."; Events.Clear(); Contributors.Clear(); Metrics.Clear(); Bookmarks.Clear(); nextCursor = null;
        Summary = await client.Send<Summary>("import",new { path },token);
        Category = Summary.Session.Capabilities.Any(x=>x.Category=="CPU" && x.Status==Availability.Available) ? "CPU" : Summary.Session.Capabilities.Any(x=>x.Category=="Disk" && x.Status==Availability.Available) ? "Disk" : "All categories";
        StartSeconds = "0"; EndSeconds = (Summary.Session.DurationMs/1000).ToString("0.######",CultureInfo.InvariantCulture);
        await RefreshCore(token);
        var bookmarks = await client.Send<List<Bookmark>>("bookmarks",new {},token);
        foreach(var item in bookmarks) Bookmarks.Add(item);
        Status = $"Ready · {Summary.Session.IndexedEvents:n0} indexed events · {Summary.Session.ImportSeconds:n1}s import · {Summary.Session.ReportedLostEvents:n0} reported lost";
    });
    public Task Refresh() => Run(RefreshCore);
    private async Task RefreshCore(CancellationToken token)
    {
        if(Summary == null) return;
        var interval = Interval();
        var metrics = await client.Send<List<MetricPoint>>("metrics",interval,token);
        var contributors = await client.Send<List<Contributor>>("contributors",interval,token);
        Metrics.Clear(); foreach(var x in metrics) Metrics.Add(x);
        Contributors.Clear(); foreach(var x in contributors) Contributors.Add(x);
        Explanation = EvidenceLanguage.Explain(interval,contributors,Summary.Session.Capabilities,Summary.Session.Warnings);
        nextCursor = null; Events.Clear(); await LoadPage(token);
        ChartsChanged?.Invoke();
        Status = $"{Events.Count:n0} events on this page · interval {interval.StartMs/1000:n3}–{interval.EndMs/1000:n3}s · {(SelectedProcess?.Name ?? "all processes")}";
    }
    private async Task LoadPage(CancellationToken token)
    {
        var interval = Interval();
        var page = await client.Send<EventPage>("events",new EventQuery(interval.StartMs,interval.EndMs,interval.ProcessId,
            string.IsNullOrWhiteSpace(Category) || Category=="All categories" ? null : Category, OperationFilter,TargetFilter,nextCursor),token);
        Events.Clear(); foreach(var x in page.Events) Events.Add(x);
        nextCursor = page.NextCursor; more = page.More;
        Status = more ? "Showing 500 events. Use Next page for more." : $"Showing {page.Events.Count:n0} events; end of selection.";
    }
    public Task NextPage() => Run(async token=> { if(more) await LoadPage(token); });
    public Task FirstPage() => Run(async token=> { nextCursor=null; await LoadPage(token); });
    public Task ShowEvidence(Observation? observation,string? symbolDirectory = null) => Run(async token=>{
        if(observation == null) return;
        var detail = await client.Send<EvidenceDetail>("evidence",new { index=observation.Index, symbolDirectory },token);
        EvidenceText = $"Provenance: {detail.Provenance}\n{detail.Observation.Operation}\n{detail.Observation.ProcessName} · PID {detail.Observation.Pid} · TID {detail.Observation.Tid}\n\n{detail.Coordinates}\n\n{detail.Interpretation}\n\n" +
            string.Join("\n",detail.Payload.Select(x=>$"{x.Key}: {x.Value}")) + "\n\nCaptured stack\n" +
            (detail.Stack.Count == 0 ? "No captured stack is attached to this record." : string.Join("\n",detail.Stack));
    });
    public Task ShowEntity(EntityLifetime? entity) => Run(async token=>{
        if(entity == null) return;
        var detail = await client.Send<EntityDetail>("entity",new { id=entity.Id },token);
        EvidenceText = $"Provenance: observed captured lifetime\n{entity.Name} · PID {entity.OsId}\nLifetime ID: {entity.Id}\n\nImage: {entity.ImagePath}\nCommand line: {entity.CommandLine}\n" +
            $"Start: {entity.StartMs/1000:n6}s ({(entity.StartKnown ? "observed start" : "start boundary unknown / rundown")})\n" +
            $"End: {entity.EndMs/1000:n6}s ({(entity.EndKnown ? "observed exit" : "end boundary unknown")})\n" +
            $"OS-reported parent lifetime: {entity.ParentId ?? "unresolved"}\n\nCaptured threads\n" +
            string.Join("\n",detail.Threads.Select(x=>$"{x.OsId} · {x.StartMs/1000:n3}–{x.EndMs/1000:n3}s · {x.Id}")) +
            "\n\nCaptured modules\n" + string.Join("\n",detail.Modules);
    });
    public async Task SelectContributor(Contributor? contributor)
    {
        if(contributor?.ProcessId == null || Summary == null) return;
        SelectedProcess = Summary.Processes.FirstOrDefault(x=>x.Id==contributor.ProcessId);
        await Refresh();
    }
    public async Task SelectIncident(Incident? incident)
    {
        if(incident == null) return;
        SelectedProcess = null; StartSeconds=(incident.StartMs/1000).ToString("0.######",CultureInfo.InvariantCulture);
        EndSeconds=(incident.EndMs/1000).ToString("0.######",CultureInfo.InvariantCulture); await Refresh();
    }
    public Task SaveBookmark() => Run(async token=>{
        var interval=Interval();
        var bookmark=new Bookmark(Guid.NewGuid().ToString("N"),BookmarkLabel,interval.StartMs,interval.EndMs);
        var saved = await client.Send<Bookmark>("bookmark",bookmark,token); Bookmarks.Add(saved); Status="Bookmark saved locally.";
    });
    public async Task SelectBookmark(Bookmark? bookmark)
    {
        if(bookmark==null)return;
        StartSeconds=(bookmark.StartMs/1000).ToString("0.######",CultureInfo.InvariantCulture);
        EndSeconds=(bookmark.EndMs/1000).ToString("0.######",CultureInfo.InvariantCulture); await Refresh();
    }
    public Task Configure() => Run(async token=>{
        if(!double.TryParse(CpuThreshold,CultureInfo.InvariantCulture,out var cpu) ||
            !double.TryParse(LatencyThreshold,CultureInfo.InvariantCulture,out var disk) ||
            !int.TryParse(ConsecutiveSeconds,out var seconds) || !int.TryParse(MinimumDiskOperations,out var ops))
            throw new WoiException("InvalidSettings","Enter numeric heuristic thresholds.");
        Summary = await client.Send<Summary>("settings",new DetectionSettings(cpu,disk,seconds,ops),token);
        Status="Incident heuristics updated. These conditions do not prove a freeze.";
    });
    public Task Export(string path,string format) => Run(async token=>{
        await client.Send<object>("export",new { path,format,interval=Interval() },token); Status="Evidence report saved locally.";
    });
    public void Cancel() => operation?.Cancel();
    public void Dispose() { StopObservation(); CancelRecording();captureClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();operation?.Cancel(); client.Dispose(); }
}
