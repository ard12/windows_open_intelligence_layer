using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Woi.Core;

namespace Woi.Desktop;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private WorkerClient? client;
    private bool disposed;
    private WorkerClient AnalysisClient {
        get {
            ObjectDisposedException.ThrowIf(disposed,this);
            return client ??= CreateAnalysisClient();
        }
    }
    private WorkerClient CreateAnalysisClient()
    {
        var created=new WorkerClient(cacheRoot: analysisCacheRoot);
        created.Progress += value => System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Status=value.Message);
        return created;
    }
    private void RecoverAnalysisClient()
    {
        if(client is { IsAlive: false }) {
            var previous=client; client=null;
            previous.Dispose(); DropInvestigations(previous);
        }
    }
    private CancellationTokenSource? operation;
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
    internal int WorkerProcessId => client?.ProcessId ?? throw new InvalidOperationException("The analysis worker has not started.");
    internal int? AnalysisWorkerProcessId => client?.ProcessId;
    internal long? EvidenceEventIndex { get; private set; }
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
        bool succeeded = false;
        try { await action(operation.Token); succeeded = true; }
        catch(OperationCanceledException) { Status = "Operation cancelled. The original recording was preserved."; }
        catch(Exception ex) { Error = ex.Message; Status = "The operation could not be completed."; }
        finally {
            operation.Dispose(); operation = null; Busy = false;
            if (!succeeded) { historyLoadPending = false; stackLoadPending = false; }
            if (succeeded && ProcessHistoryVisible && historyLoadPending && Summary != null) {
                historyLoadPending = false;
                await LoadProcessHistory(true);
            }
            if (succeeded && CpuStacksVisible && stackLoadPending && Summary != null) {
                stackLoadPending = false; await LoadCpuStacks(true);
            }
        }
    }
    public Task Open(string path) => Run(async token => {
        RecoverAnalysisClient();
        ClearProcessHistory();
        ClearCpuStacks();
        ResetTimeline();
        EvidenceEventIndex = null;
        Summary = null; SelectedProcess = null; Category = "All categories"; OperationFilter = ""; TargetFilter = ""; FilterRules.Clear(); ResetEventNavigation(); EvidenceText = "Select an event to inspect the captured record."; Events.Clear(); Contributors.Clear(); Metrics.Clear(); Bookmarks.Clear();
        Summary = await AnalysisClient.Send<Summary>("import",new { path },token);
        Category = Summary.Session.Capabilities.Any(x=>x.Category=="CPU" && x.Status==Availability.Available) ? "CPU" : Summary.Session.Capabilities.Any(x=>x.Category=="Disk" && x.Status==Availability.Available) ? "Disk" : "All categories";
        StartSeconds = "0"; EndSeconds = (Summary.Session.DurationMs/1000).ToString("0.######",CultureInfo.InvariantCulture);
        await RefreshCore(token);
        var bookmarks = await AnalysisClient.Send<List<Bookmark>>("bookmarks",new {},token);
        foreach(var item in bookmarks) Bookmarks.Add(item);
        Status = $"Ready · {Summary.Session.IndexedEvents:n0} indexed events · {Summary.Session.ImportSeconds:n1}s import · {Summary.Session.ReportedLostEvents:n0} reported lost";
    });
    public Task Refresh() => Run(RefreshCore);
    private async Task RefreshCore(CancellationToken token)
    {
        if(Summary == null) return;
        var interval = BoundedInterval();
        var eventQuery = DraftEventQuery() with { StartMs = interval.StartMs, EndMs = interval.EndMs };
        var metrics = await AnalysisClient.Send<List<MetricPoint>>("metrics",interval,token);
        var contributors = await AnalysisClient.Send<List<Contributor>>("contributors",interval,token);
        var events = await AnalysisClient.Send<EventPage>("events", eventQuery, token);
        var history = ProcessHistory == null ? null : await AnalysisClient.Send<ProcessHistoryPage>("processHistory",
            new ProcessHistoryQuery(interval.StartMs, interval.EndMs, ProcessHistory.Focus?.Id), token);
        var stacks = CpuStacks == null ? null : await AnalysisClient.Send<CpuStackPage>("cpuStacks",
            new CpuStackQuery(interval.StartMs, interval.EndMs, interval.ProcessId, Grouping: StackGrouping), token);
        token.ThrowIfCancellationRequested();
        // Publish one coherent scope only after every requested analysis succeeds.
        Metrics.Clear(); foreach(var x in metrics) Metrics.Add(x);
        Contributors.Clear(); foreach(var x in contributors) Contributors.Add(x);
        Explanation = EvidenceLanguage.Explain(interval,contributors,Summary.Session.Capabilities,Summary.Session.Warnings);
        PublishFirstEventPage(eventQuery, events);
        if (history != null) PublishProcessHistory(history);
        if (stacks != null) PublishCpuStacks(stacks, []);
        CommitTimeline(interval);
        ChartsChanged?.Invoke();
        Status = $"{Events.Count:n0} events on this page · interval {interval.StartMs/1000:n3}–{interval.EndMs/1000:n3}s · {(SelectedProcess?.Name ?? "all processes")}";
    }
    public Task ShowEvidence(Observation? observation,string? symbolDirectory = null) => observation == null ? Task.CompletedTask : ShowEvidenceAt(observation.Index, symbolDirectory);
    public Task LoadEvidenceSymbols(string directory) => EvidenceEventIndex is { } index ? ShowEvidenceAt(index, directory) : Task.CompletedTask;
    private Task ShowEvidenceAt(long index, string? symbolDirectory = null) => Run(async token=>{
        // Symbol loading can change address keys even when cancelled after partially resolving modules.
        if (symbolDirectory != null) ClearCpuStacks();
        var detail = await AnalysisClient.Send<EvidenceDetail>("evidence",new { index, symbolDirectory },token);
        EvidenceEventIndex = detail.Observation.Index;
        EvidenceText = $"Provenance: {detail.Provenance}\n{detail.Observation.Operation}\n{detail.Observation.ProcessName} · PID {detail.Observation.Pid} · TID {detail.Observation.Tid}\n\n{detail.Coordinates}\n\n{detail.Interpretation}\n\n" +
            string.Join("\n",detail.Payload.Select(x=>$"{x.Key}: {x.Value}")) + "\n\nCaptured stack\n" +
            (detail.Stack.Count == 0 ? "No captured stack is attached to this record." : string.Join("\n",detail.Stack));
        Status = "Captured event loaded.";
    });
    public Task ShowEntity(EntityLifetime? entity) => Run(async token=>{
        if(entity == null) return;
        var detail = await AnalysisClient.Send<EntityDetail>("entity",new { id=entity.Id },token);
        EvidenceEventIndex = null;
        EvidenceText = $"Provenance: observed captured lifetime\n{entity.Name} · PID {entity.OsId}\nLifetime ID: {entity.Id}\n\nImage: {entity.ImagePath}\nCommand line: {entity.CommandLine}\n" +
            $"Start: {entity.StartMs/1000:n6}s ({(entity.StartKnown ? "observed start" : "start boundary unknown / rundown")})\n" +
            $"End: {entity.EndMs/1000:n6}s ({(entity.EndKnown ? "observed exit" : "end boundary unknown")})\n" +
            $"OS-reported parent lifetime: {entity.ParentId ?? "unresolved"}\n\nCaptured threads\n" +
            string.Join("\n",detail.Threads.Select(x=>$"{x.OsId} · {x.StartMs/1000:n3}–{x.EndMs/1000:n3}s · {x.Id}")) +
            "\n\nCaptured modules\n" + string.Join("\n",detail.Modules);
        Status = "Captured process loaded.";
    });
    public async Task SelectContributor(Contributor? contributor)
    {
        if(Busy || contributor?.ProcessId == null || Summary == null) return;
        SelectedProcess = Summary.Processes.FirstOrDefault(x=>x.Id==contributor.ProcessId);
        await Refresh();
    }
    public async Task SelectIncident(Incident? incident)
    {
        if(Busy || incident == null) return;
        SelectedProcess = null; StartSeconds=(incident.StartMs/1000).ToString("0.######",CultureInfo.InvariantCulture);
        EndSeconds=(incident.EndMs/1000).ToString("0.######",CultureInfo.InvariantCulture); await Refresh();
    }
    public Task SaveBookmark() => Run(async token=>{
        var interval=Interval();
        var bookmark=new Bookmark(Guid.NewGuid().ToString("N"),BookmarkLabel,interval.StartMs,interval.EndMs);
        var saved = await AnalysisClient.Send<Bookmark>("bookmark",bookmark,token); Bookmarks.Add(saved); Status="Bookmark saved locally.";
    });
    public async Task SelectBookmark(Bookmark? bookmark)
    {
        if(Busy || bookmark==null)return;
        StartSeconds=(bookmark.StartMs/1000).ToString("0.######",CultureInfo.InvariantCulture);
        EndSeconds=(bookmark.EndMs/1000).ToString("0.######",CultureInfo.InvariantCulture); await Refresh();
    }
    public Task Configure() => Run(async token=>{
        if(!double.TryParse(CpuThreshold,CultureInfo.InvariantCulture,out var cpu) ||
            !double.TryParse(LatencyThreshold,CultureInfo.InvariantCulture,out var disk) ||
            !int.TryParse(ConsecutiveSeconds,out var seconds) || !int.TryParse(MinimumDiskOperations,out var ops))
            throw new WoiException("InvalidSettings","Enter numeric heuristic thresholds.");
        Summary = await AnalysisClient.Send<Summary>("settings",new DetectionSettings(cpu,disk,seconds,ops),token);
        Status="Incident heuristics updated. These conditions do not prove a freeze.";
    });
    public Task Export(string path,string format) => Run(async token=>{
        await AnalysisClient.Send<object>("export",new { path,format,interval=Interval() },token); Status="Evidence report saved locally.";
    });
    public void Cancel() => operation?.Cancel();
    public void Dispose()
    {
        if(disposed)return;
        disposed=true;
        StopObservation(); CancelRecording();captureClient?.DisposeAsync().AsTask().GetAwaiter().GetResult();operation?.Cancel();
        client?.Dispose();client=null;
    }
}
