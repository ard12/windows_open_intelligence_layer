using System.Globalization;
using System.Text.Json;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Woi.Core;

namespace Woi.Trace;

public sealed class EtlReader : ITraceReader
{
    private readonly TraceLog log;
    private readonly ImportMetadata metadata;
    private readonly Dictionary<int, EntityLifetime> processByIndex = new();
    private readonly Dictionary<int, EntityLifetime[]> lifetimesByPid = new();
    private readonly Dictionary<int, EntityLifetime[]> lifetimesByTid = new();
    private IEnumerator<TraceEvent>? pageIterator;
    private Observation? pageLookahead;
    private EventQuery? activePageQuery;
    private long? expectedCursor;
    internal long PagingRecordsVisited { get; private set; }
    public TraceSession Session { get; }
    public IReadOnlyList<EntityLifetime> Entities { get; }
    public EtlReader(TraceSession session) : this(new TraceLog(Path.Combine(session.CacheDirectory, "events.etlx")),
        session, JsonSerializer.Deserialize<ImportMetadata>(File.ReadAllText(Path.Combine(session.CacheDirectory, "import.json")), Wire.Json)!) { }
    public EtlReader(TraceLog traceLog, TraceSession session, ImportMetadata importMetadata)
    {
        log = traceLog; Session = session; metadata = importMetadata;
        List<EntityLifetime> entities = [];
        var positions = new Dictionary<string,int>();
        var processStarts = metadata.ProcessStarts.GroupBy(x=>x.Pid).ToDictionary(x=>x.Key,x=>x.Select(v=>v.Time).ToArray());
        var threadStarts = metadata.ThreadStarts.GroupBy(x=>x.Tid).ToDictionary(x=>x.Key,x=>x.Select(v=>v.Time).ToArray());
        var threadStops = metadata.ThreadStops.GroupBy(x=>x.Tid).ToDictionary(x=>x.Key,x=>x.Select(v=>v.Time).ToArray());
        static bool Boundary(Dictionary<int,double[]> map,int id,double time) =>
            map.TryGetValue(id,out var values) && values.Any(x=>Math.Abs(x-time)<.01);
        foreach (var p in log.Processes) {
            var id = ProcessId(p);
            var value = new EntityLifetime(id, "Process", p.ProcessID, p.Name, p.StartTimeRelativeMsec,
                p.EndTimeRelativeMsec, Boundary(processStarts,p.ProcessID,p.StartTimeRelativeMsec),
                p.ExitStatus != null, null, p.Parent == null || p.Parent.StartTimeRelativeMsec > p.StartTimeRelativeMsec || p.Parent.EndTimeRelativeMsec <= p.StartTimeRelativeMsec ? null : ProcessId(p.Parent), p.ImageFileName, p.CommandLine);
            processByIndex[(int)p.ProcessIndex] = value; positions[value.Id]=entities.Count; entities.Add(value);
        }
        foreach (var group in entities.GroupBy(x => x.OsId)) lifetimesByPid[group.Key] = group.ToArray();
        foreach (var p in log.Processes) {
            var entity = processByIndex[(int)p.ProcessIndex];
            var parent = lifetimesByPid.TryGetValue(p.ParentID, out var candidates) && entity.StartKnown ? LifetimeResolver.Resolve(candidates, p.ParentID, entity.StartMs) : null;
            var updated = entity with { ParentId = parent?.Id };
            processByIndex[(int)p.ProcessIndex] = updated;
            entities[positions[entity.Id]] = updated;
        }
        foreach (var t in log.Threads) entities.Add(new(ThreadId(t), "Thread", t.ThreadID, $"Thread {t.ThreadID}",
            t.StartTimeRelativeMSec, t.EndTimeRelativeMSec,
            Boundary(threadStarts,t.ThreadID,t.StartTimeRelativeMSec),
            Boundary(threadStops,t.ThreadID,t.EndTimeRelativeMSec),
            ProcessId(t.Process), null, "", ""));
        foreach(var group in entities.Where(x=>x.Kind=="Thread").GroupBy(x=>x.OsId)) lifetimesByTid[group.Key]=group.ToArray();
        Entities = entities;
    }
    private string ProcessId(TraceProcess p) => Session.SourceHash[..16] + ":p:" + (int)p.ProcessIndex;
    private string ThreadId(TraceThread t) => Session.SourceHash[..16] + ":t:" + (int)t.ThreadIndex;
    private double? Weight(double ms)
    {
        return SamplingWeight(metadata.Intervals,ms);
    }
    internal static double? SamplingWeight(IReadOnlyList<SamplingInterval> intervals,double ms)
    {
        int low=0,high=intervals.Count-1,found=-1;
        while(low<=high) {
            int middle=low+(high-low)/2;
            if(intervals[middle].StartMs<=ms){found=middle;low=middle+1;}else high=middle-1;
        }
        return found<0?null:intervals[found].Milliseconds;
    }
    public Observation? Normalize(TraceEvent data, bool includeUnsupported = false)
    {
        var category = Category(data);
        if (category == "Unsupported" && !includeUnsupported) return null;
        string? processId = null, threadId = null;
        string name = "Unattributed activity";
        TraceProcess? process = data.ProcessID >= 0 ? data.Process() : null;
        if (process != null && processByIndex.TryGetValue((int)process.ProcessIndex, out var entity) &&
            (category == "Process" || (lifetimesByPid.TryGetValue(entity.OsId, out var candidates) &&
             LifetimeResolver.Resolve(candidates, entity.OsId, data.TimeStampRelativeMSec)?.Id == entity.Id))) {
            processId = entity.Id; name = entity.Name;
        }
        var thread = data.ThreadID >= 0 ? data.Thread() : null;
        if (thread != null && processId != null && ProcessId(thread.Process) == processId &&
            (category == "Thread" || (lifetimesByTid.TryGetValue(data.ThreadID,out var threadCandidates) &&
             LifetimeResolver.Resolve(threadCandidates,data.ThreadID,data.TimeStampRelativeMSec,"Thread")?.Id == ThreadId(thread)))) threadId = ThreadId(thread);
        string target = Text(data, "FileName", "ImageFileName", "ModuleILPath");
        long? bytes = null;
        double? duration = null, memory = null, cpuWeight = null;
        if (data is SampledProfileTraceData sample) {
            if (sample.NonProcess) { processId = null; threadId = null; name = "Unattributed interrupt / DPC activity"; }
            if (sample.ThreadID == 0 && !sample.NonProcess) return null; // Idle samples are not busy CPU.
            cpuWeight = Weight(data.TimeStampRelativeMSec);
            target = "Instruction 0x" + sample.InstructionPointer.ToString("x");
        }
        if (data is DiskIOTraceData disk) { bytes = Math.Max(0, (long)disk.TransferSize); duration = disk.ElapsedTimeMSec; target = disk.FileName; }
        if (data is MemoryHardFaultTraceData hardFault) { bytes = Math.Max(0, (long)hardFault.ByteCount); duration = hardFault.ElapsedTimeMSec; target = hardFault.FileName; }
        if (data is FileIOReadWriteTraceData file) { bytes = Math.Max(0, (long)file.IoSize); target = file.FileName; }
        if (category == "Network") {
            var source = Text(data, "saddr"); var dest = Text(data, "daddr");
            target = $"{source}:{Text(data,"sport")} → {dest}:{Text(data,"dport")}";
            bytes = Number(data, "size");
        }
        if (data is MemoryProcessMemInfoTraceData memoryData && memoryData.Count > 0) {
            var mem = memoryData.Values(0);
            memory = mem.WorkingSetPageCount * 4096d; // Windows x64 base pages.
            target = $"Working set snapshot: {memory/1048576:n2} MiB";
        }
        var reference = new EvidenceReference(Session.SourceHash, Session.ParserVersion, (long)data.EventIndex,
            ReadQpc(data), data.TimeStampRelativeMSec, data.ProviderGuid.ToString(), data.EventName, data.ProcessID, data.ThreadID);
        return new((long)data.EventIndex, data.TimeStampRelativeMSec, ReadQpc(data), category, data.EventName,
            processId, threadId, data.ProcessID, data.ThreadID, name, target, bytes, duration, cpuWeight, memory, reference);
    }
    // QPC is retained as an evidence coordinate; calculations use high-resolution trace-relative time.
#pragma warning disable CS0618
    private static long ReadQpc(TraceEvent data) => data.TimeStampQPC;
#pragma warning restore CS0618
    public static string Category(TraceEvent data) => data switch {
        SampledProfileTraceData => "CPU", DiskIOTraceData => "Disk", ProcessTraceData => "Process",
        ThreadTraceData => "Thread", ImageLoadTraceData => "Module", MemoryProcessMemInfoTraceData => "Memory",
        MemoryHardFaultTraceData => "HardFault",
        _ when data.GetType().Name.StartsWith("FileIO", StringComparison.Ordinal) => "File",
        _ when data.GetType().Name.StartsWith("TcpIp", StringComparison.Ordinal) || data.GetType().Name.StartsWith("UdpIp", StringComparison.Ordinal) ||
               data.GetType().Name.StartsWith("UpdIp", StringComparison.Ordinal) => "Network",
        _ => "Unsupported"
    };
    private static string Text(TraceEvent data, params string[] names)
    {
        foreach(var key in names) {
            int index = Array.FindIndex(data.PayloadNames, x => x.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) return Convert.ToString(data.PayloadValue(index), CultureInfo.InvariantCulture) ?? "";
        }
        return "";
    }
    private static long? Number(TraceEvent data, string name) => long.TryParse(Text(data,name), out var value) ? value : null;
    public IEnumerable<Observation> ReadObservations(IntervalQuery query, CancellationToken token)
    {
        foreach (var data in log.Events.FilterByTime(query.StartMs, query.EndMs)) {
            token.ThrowIfCancellationRequested();
            if (data.TimeStampRelativeMSec < query.StartMs || data.TimeStampRelativeMSec >= query.EndMs) continue;
            var value = Normalize(data);
            if(value != null && (query.ProcessId == null || value.ProcessId == query.ProcessId)) yield return value;
        }
    }
    public IEnumerable<Observation> ReadContributorObservations(IntervalQuery query,CancellationToken token)
    {
        foreach(var data in log.Events.FilterByTime(query.StartMs,query.EndMs)) {
            token.ThrowIfCancellationRequested();
            if(data.TimeStampRelativeMSec<query.StartMs || data.TimeStampRelativeMSec>=query.EndMs)continue;
            // Lifecycle and metadata records have no contributor weight. Keep them available in Explorer.
            if(data is not (SampledProfileTraceData or DiskIOTraceData or FileIOReadWriteTraceData) && Category(data)!="Network")continue;
            var value=Normalize(data);
            if(value!=null && (query.ProcessId==null || value.ProcessId==query.ProcessId))yield return value;
        }
    }
    public EventPage QueryEvents(EventQuery query, CancellationToken token)
    {
        if (query.Limit is < 1 or > 500 || !double.IsFinite(query.StartMs) || !double.IsFinite(query.EndMs) ||
            query.StartMs<0 || query.EndMs<=query.StartMs || query.StartMs>=Session.DurationMs ||
            query.Cursor is < 0 || query.Cursor>=log.EventCount)
            throw new WoiException("InvalidQuery", "Choose a valid interval, event cursor, and page size from 1 to 500.");
        if(query.ProcessId!=null && !Entities.Any(x=>x.Kind=="Process" && x.Id==query.ProcessId))
            throw new WoiException("EntityMissing","The requested process lifetime is unavailable.");
        query=query with {EndMs=Math.Min(query.EndMs,Session.DurationMs)};
        var key=query with {Cursor=null};
        try {
            token.ThrowIfCancellationRequested();
            if(pageIterator==null || activePageQuery!=key || query.Cursor!=expectedCursor) {
                ResetPaging();
                double start=query.StartMs;
                if(query.Cursor is {} cursor) start=Math.Max(start,log.GetEvent((EventIndex)cursor).TimeStampRelativeMSec);
                pageIterator=((IEnumerable<TraceEvent>)log.Events.FilterByTime(start,query.EndMs)).GetEnumerator();
                activePageQuery=key;
            }
            List<Observation> result=[];
            if(pageLookahead!=null){result.Add(pageLookahead);pageLookahead=null;}
            while(pageIterator.MoveNext()) {
                token.ThrowIfCancellationRequested();PagingRecordsVisited++;
                var data=pageIterator.Current;
                if((long)data.EventIndex<=(query.Cursor??-1) || data.TimeStampRelativeMSec<query.StartMs || data.TimeStampRelativeMSec>=query.EndMs)continue;
                if(!string.IsNullOrWhiteSpace(query.Category) && Category(data)!=query.Category)continue;
                var value=Normalize(data,true);
                if(value==null || (query.ProcessId!=null && value.ProcessId!=query.ProcessId) ||
                    (!string.IsNullOrWhiteSpace(query.Category) && value.Category!=query.Category) ||
                    (!string.IsNullOrWhiteSpace(query.Operation) && !value.Operation.Contains(query.Operation,StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(query.Target) && !value.Target.Contains(query.Target,StringComparison.OrdinalIgnoreCase)))continue;
                if(result.Count==query.Limit) {
                    pageLookahead=value;expectedCursor=result[^1].Index;
                    return new(result,expectedCursor,true);
                }
                result.Add(value);
            }
            ResetPaging();return new(result,null,false);
        } catch {ResetPaging();throw;}
    }
    private void ResetPaging()
    {
        pageIterator?.Dispose();pageIterator=null;pageLookahead=null;activePageQuery=null;expectedCursor=null;
    }
    public EvidenceDetail GetEvidence(long index, string? localSymbolDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(index < 0 || index >= log.EventCount) throw new WoiException("EvidenceMissing", "The event index does not exist in this recording.");
        var data = log.GetEvent((EventIndex)index);
        var value = Normalize(data, true) ?? throw new WoiException("EvidenceMissing","This is an idle CPU record.");
        var payload = new Dictionary<string,string>();
        for(int i=0;i<data.PayloadNames.Length;i++) payload[data.PayloadNames[i]] =
            Convert.ToString(data.PayloadValue(i), CultureInfo.InvariantCulture) ?? "";
        if(localSymbolDirectory != null) {
            localSymbolDirectory = LocalPaths.RequireLocal(localSymbolDirectory);
            if (!Directory.Exists(localSymbolDirectory) || localSymbolDirectory.StartsWith(@"\\"))
                throw new WoiException("InvalidSymbols", "Choose a local directory containing matching PDB files.");
            using var symbols = new SymbolReader(TextWriter.Null, localSymbolDirectory) { Options = SymbolReaderOptions.CacheOnly };
            foreach(var module in log.ModuleFiles) { token.ThrowIfCancellationRequested(); log.CodeAddresses.LookupSymbolsForModule(symbols, module); }
        }
        List<string> frames = [];
        for(var stack = data.CallStack(); stack != null && frames.Count < 256; stack = stack.Caller) {
            var address = stack.CodeAddress;
            var module = address.ModuleFile;
            frames.Add(!string.IsNullOrEmpty(address.FullMethodName) ? address.FullMethodName :
                module != null && address.Address >= module.ImageBase ?
                $"{module.Name}+0x{address.Address-module.ImageBase:x} (0x{address.Address:x})" : $"unresolved!0x{address.Address:x}");
        }
        return new(value, payload, frames,
            $"{Session.SourceHash} · ETLX {index} · {value.TimeMs/1000:F6}s · QPC {value.Qpc} · {value.Evidence.Provider}\nRecorded clock UTC: {Session.StartUtc.AddMilliseconds(value.TimeMs):O}",
            "Observed record interpreted by TraceEvent. ETLX indices are derived coordinates, not raw ETL offsets. Captured stacks can be incomplete; unresolved addresses are retained.");
    }
    public EntityDetail GetEntity(string id, CancellationToken token)
    {
        var entity = Entities.FirstOrDefault(x=>x.Id == id) ?? throw new WoiException("EntityMissing","The selected lifetime is unavailable.");
        var p = log.Processes.FirstOrDefault(x=>ProcessId(x) == id);
        return new(entity, Entities.Where(x=>x.OwnerId == id).ToArray(),
            p?.LoadedModules.Select(x=>x.ModuleFile.FilePath).Distinct().ToArray() ?? [], []);
    }
    public void Dispose() { ResetPaging(); log.Dispose(); }
}
