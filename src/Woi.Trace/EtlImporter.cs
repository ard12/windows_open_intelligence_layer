using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Woi.Core;

namespace Woi.Trace;

public static class EtlImporter
{
    public const long MaximumSourceBytes=1024L*1024*1024;
    public const long MaximumDerivedBytes=2L*1024*1024*1024;
    public const int MaximumEvents=5_000_000;
    public static string ParserVersion=>typeof(TraceLog).Assembly.GetName().Version!.ToString();
    public static TraceSession Import(string sourcePath,string cacheRoot,IProgress<ImportProgress>? progress,CancellationToken token) =>
        Import(sourcePath,cacheRoot,progress,token,new(),new());

    internal static TraceSession Import(string sourcePath,string cacheRoot,IProgress<ImportProgress>? progress,
        CancellationToken token,ImportPolicy policy,ImportEnvironment environment)
    {
        policy.Validate();var timer=Stopwatch.StartNew();token.ThrowIfCancellationRequested();
        sourcePath=LocalPaths.RequireLocal(sourcePath);cacheRoot=LocalPaths.RequireLocal(cacheRoot);
        if(!File.Exists(sourcePath))throw new WoiException("SourceMissing","The selected recording does not exist.");
        if(!Path.GetExtension(sourcePath).Equals(".etl",StringComparison.OrdinalIgnoreCase))
            throw new WoiException("UnsupportedInput","Select an ETL recording.");
        using var input=new FileStream(sourcePath,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>policy.SourceBytes)throw new WoiException("SizeLimit","The recording exceeds the source-file limit (1 GiB in the desktop app).");
        progress?.Report(new("Fingerprint","Fingerprinting the original recording.",0));
        string hash=Fingerprints.Hash(input,token,bytes=>progress?.Report(new("Fingerprint","Fingerprinting the original recording.",
            input.Length==0?100:bytes*100d/input.Length)));
        Directory.CreateDirectory(cacheRoot);
        var final=Path.Combine(cacheRoot,hash+"-"+ParserVersion+"-"+Wire.AnalysisVersion);
        using var cacheLock=AcquireLock(final+".lock",token);
        if(File.Exists(Path.Combine(final,"complete.json"))) {
            try {
                progress?.Report(new("Verify","Verifying derived cache integrity."));
                var manifest=JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(Path.Combine(final,"complete.json")),Wire.Json)
                    ?? throw new WoiException("InvalidCache","Missing cache manifest.");
                if(manifest.SourceHash!=hash || manifest.ParserVersion!=ParserVersion || manifest.AnalysisVersion!=Wire.AnalysisVersion)
                    throw new WoiException("InvalidCache","Incompatible cache manifest.");
                if(manifest.Files==null || manifest.Files.Count!=3 ||
                    !manifest.Files.Select(x=>x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(["events.etlx","import.json","analysis.db"]))
                    throw new WoiException("InvalidCache","Unexpected derived-file manifest.");
                foreach(var expected in manifest.Files) {
                    var actual=Fingerprints.File(Path.Combine(final,expected.Name),token);
                    if(actual!=expected)throw new WoiException("InvalidCache","A derived file failed its fingerprint check.");
                }
                using var store=new CacheStore(final,readOnly:true);
                store.VerifyIntegrity();
                var saved=store.GetSession();
                var metadata=LoadMetadata(final);
                using var indexed=new TraceLog(Path.Combine(final,"events.etlx"));
                if(saved.SourceHash!=hash || saved.ParserVersion!=ParserVersion || saved.AnalysisVersion!=Wire.AnalysisVersion ||
                    indexed.EventCount!=saved.IndexedEvents || manifest.IndexedEvents!=saved.IndexedEvents ||
                    store.Statistics()!=manifest.Statistics || saved.RecordedQpcFrequency!=metadata.RecordedQpcFrequency)
                    throw new WoiException("InvalidCache","Cache metadata or aggregates do not match the completed import.");
                Guard(final,policy,environment,token,writing:false);
                progress?.Report(new("Ready","Opened the verified indexed recording.",100));
                return saved with {SourcePath=sourcePath,ImportSeconds=timer.Elapsed.TotalSeconds};
            } catch(Exception ex) when(ex is not OperationCanceledException && ex is not OutOfMemoryException) {
                progress?.Report(new("Cache","The existing derived cache is invalid; rebuilding from the original recording."));
            }
        }
        RequireSpace(cacheRoot,policy,environment);
        var bookmarks=RecoverBookmarks(cacheRoot,hash);
        var staging=final+".pending-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(staging);
        var output=Path.Combine(staging,"events.etlx");
        var metadataNew=new ImportMetadata();List<string> warnings=[];
        try {
            progress?.Report(new("Decode","Indexing ETL events and captured entity lifetimes."));
            token.ThrowIfCancellationRequested();
            using(var source=new ETWTraceEventSource(sourcePath)) {
                source.Kernel.ProcessStart+=x=>metadataNew.ProcessStarts.Add((x.ProcessID,x.TimeStampRelativeMSec));
                source.Kernel.ProcessStop+=x=>metadataNew.ProcessStops.Add((x.ProcessID,x.TimeStampRelativeMSec));
                source.Kernel.ThreadStart+=x=>metadataNew.ThreadStarts.Add((x.ThreadID,x.TimeStampRelativeMSec));
                source.Kernel.ThreadStop+=x=>metadataNew.ThreadStops.Add((x.ThreadID,x.TimeStampRelativeMSec));
                source.Kernel.PerfInfoCollectionStart+=x=>{
                    if(x.SampleSource==0 && x.NewInterval>0)metadataNew.Intervals.Add(new(x.TimeStampRelativeMSec,x.NewInterval/10000d));
                };
                source.Kernel.PerfInfoSetInterval+=x=>{
                    if(x.SampleSource!=0)return;
                    if(metadataNew.Intervals.Count==0 && x.OldInterval>0)metadataNew.Intervals.Add(new(0,x.OldInterval/10000d));
                    if(x.NewInterval>0)metadataNew.Intervals.Add(new(x.TimeStampRelativeMSec,x.NewInterval/10000d));
                };
                long count=0;
                source.AllEvents+=x=>{
                    if(x is EventTraceHeaderTraceData header && header.PerfFreq>0)metadataNew.RecordedQpcFrequency=header.PerfFreq;
                    if((++count & 8191)==0)Guard(staging,policy,environment,token);
                };
                var logWriter=new GuardedWriter(()=>Guard(staging,policy,environment,token));
                TraceLog.CreateFromEventTraceLogFile(source,output,new TraceLogOptions {
                    KeepAllEvents=true,MaxEventCount=policy.IndexedEvents+1,ContinueOnError=false,
                    ShouldResolveSymbols=_=>false,LocalSymbolsOnly=true,ConversionLog=logWriter,
                    OnLostEvents=(truncated,lost,_)=>{
                        if(truncated)throw new WoiException("SizeLimit","The indexed event limit was exceeded; the import was not published.");
                        if(lost>0)warnings.Add($"Windows reports {lost:n0} lost events. Exact loss intervals may be unknown.");
                    }
                });
                if(logWriter.OutOfOrder)throw new WoiException("OutOfOrder","Time inversions invalidate indexed queries. Re-merge this recording with Windows Performance Toolkit.");
            }
            Guard(staging,policy,environment,token);
            metadataNew.Intervals.Sort((a,b)=>a.StartMs.CompareTo(b.StartMs));
            File.WriteAllText(Path.Combine(staging,"import.json"),JsonSerializer.Serialize(metadataNew,Wire.Json));
            CacheStatistics statistics;TraceSession session;
            using(var log=new TraceLog(output)) {
                if(log.EventCount>policy.IndexedEvents)throw new WoiException("SizeLimit","The recording exceeds the indexed event limit.");
                var duration=log.SessionDuration.TotalMilliseconds;
                if(!double.IsFinite(duration)||duration<=0)throw new WoiException("InvalidTrace","The recording has no usable positive duration.");
                var blank=new TraceSession(hash,sourcePath,hash,ParserVersion,Wire.AnalysisVersion,final,
                    log.SessionStartTime.ToUniversalTime(),duration,log.NumberOfProcessors,log.EventCount,log.EventsLost,[],[],0,0,metadataNew.RecordedQpcFrequency);
                using var reader=new EtlReader(log,blank,metadataNew,token);
                var builder=new AnalysisBuilder();long inspected=0,unknown=0,fileBytesRecords=0,networkBytesRecords=0,unweighted=0,unresolved=0;
                foreach(var data in log.Events) {
                    token.ThrowIfCancellationRequested();var value=reader.Normalize(data);
                    if(value==null){if(EtlReader.Category(data)=="Unsupported")unknown++;continue;}
                    if(value.Category=="File" && value.Bytes!=null)fileBytesRecords++;
                    if(value.Category=="Network" && value.Bytes!=null && IsTransfer(value.Operation))networkBytesRecords++;
                    if(value.Category=="CPU" && value.CpuWeightMs==null)unweighted++;
                    if(value.Category is "File" or "Disk" && string.IsNullOrEmpty(value.Target))unresolved++;
                    builder.Add(value);
                    if((++inspected & 16383)==0) {
                        Guard(staging,policy,environment,token);
                        progress?.Report(new("Aggregate",$"Analyzed {inspected:n0} observations.",inspected*100d/Math.Max(1,log.EventCount)));
                    }
                }
                if(log.EventsLost>0)warnings.Add($"Windows reports {log.EventsLost:n0} lost events; the recording may be incomplete.");
                if(unknown>0)warnings.Add($"{unknown:n0} indexed records have unsupported resource schemas; their headers remain inspectable.");
                if(unweighted>0)warnings.Add($"{unweighted:n0} CPU samples have no usable recorded sampling interval or sample spacing. Estimates for affected selections are unavailable.");
                if(unresolved>0)warnings.Add($"{unresolved:n0} file/disk records have unresolved captured paths.");
                warnings.Add("Reported event loss does not prove complete coverage. Providers may have been disabled or filtered.");
                warnings.Add("Resource contributors do not establish why an application became unresponsive.");
                warnings.Add("CPU estimates use captured per-processor sample spacing, with the recorded interval for long gaps. Sampling remains statistical.");
                warnings.Add("Disk latency is the completion record's response-time payload, separate from reconstructed issue-to-completion duration.");
                var categories=new[]{"Process","Thread","Module","CPU","Disk","File","Network","Memory","HardFault"};
                var capabilities=categories.Select(category=>new Capability(category,
                    builder.Counts.GetValueOrDefault(category)>0?Availability.Available:Availability.NotCaptured,
                    builder.Counts.GetValueOrDefault(category),Detail(category,builder.Counts.GetValueOrDefault(category)))).Concat(new[]{
                    new Capability("FileBytes",fileBytesRecords>0?Availability.Available:Availability.NotCaptured,fileBytesRecords,"Requested logical read/write bytes, separate from physical transfers."),
                    new Capability("NetworkBytes",networkBytesRecords>0?Availability.Available:Availability.NotCaptured,networkBytesRecords,"Supported send/receive byte records, not unique application payload."),
                    new Capability("ReportedLoss",Availability.Available,log.EventsLost,"Windows-reported loss; zero does not establish completeness."),
                    new Capability("Paths",unresolved>0?Availability.Unresolved:builder.Counts.GetValueOrDefault("File")+builder.Counts.GetValueOrDefault("Disk")>0?Availability.Available:Availability.NotCaptured,unresolved,"Captured paths only; unresolved identifiers stay in payloads."),
                    new Capability("UnknownSchemas",unknown>0?Availability.Unsupported:Availability.NotCaptured,unknown,"Inspectable headers without resource interpretation.")
                }).ToArray();
                session=blank with{Capabilities=capabilities,Warnings=warnings.Distinct().ToArray()};
                var rows=builder.Finish();Guard(staging,policy,environment,token);
                using var store=new CacheStore(staging);
                store.Save(session,reader.Entities,rows,token,()=>Guard(staging,policy,environment,token));
                foreach(var bookmark in bookmarks.Where(x=>x.StartMs>=0 && x.EndMs<=duration && x.EndMs>x.StartMs))store.AddBookmark(bookmark);
                Guard(staging,policy,environment,token);
                session=session with{ImportSeconds=timer.Elapsed.TotalSeconds,PeakWorkingSetBytes=Process.GetCurrentProcess().PeakWorkingSet64};
                store.UpdateSession(session);statistics=store.Statistics();store.VerifyIntegrity();
            }
            var files=new[]{"events.etlx","import.json","analysis.db"}.Select(name=>Fingerprints.File(Path.Combine(staging,name),token)).ToArray();
            Guard(staging,policy,environment,token);
            File.WriteAllText(Path.Combine(staging,"complete.json"),JsonSerializer.Serialize(
                new CacheManifest(hash,ParserVersion,Wire.AnalysisVersion,session.IndexedEvents,statistics,files),Wire.Json));
            Guard(staging,policy,environment,token);
            if(Directory.Exists(final))Directory.Move(final,final+".incompatible-"+Guid.NewGuid().ToString("N"));
            Directory.Move(staging,final);
            progress?.Report(new("Ready",$"Indexed {session.IndexedEvents:n0} events in {timer.Elapsed.TotalSeconds:n1}s.",100));
            return session;
        } catch(Exception ex) {
            if(Directory.Exists(staging) && Path.GetFullPath(staging).StartsWith(cacheRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                try{Directory.Delete(staging,true);}catch(IOException){}
            if(IsDiskFull(ex))throw new WoiException("InsufficientDiskSpace","The derived cache could not be written because the disk is full. The original recording was preserved.");
            if(ex is OutOfMemoryException)throw new WoiException("MemoryLimit","The worker ran out of memory. The import was not published.");
            throw;
        }
    }
    internal static ImportMetadata LoadMetadata(string directory) {
        var data=JsonSerializer.Deserialize<ImportMetadata>(File.ReadAllText(Path.Combine(directory,"import.json")),Wire.Json)
            ??throw new WoiException("InvalidCache","Sampling/lifetime metadata is missing.");
        if(data.Intervals==null || data.Intervals.Any(x=>!double.IsFinite(x.StartMs)||!double.IsFinite(x.Milliseconds)||x.Milliseconds<=0) ||
            data.ProcessStarts==null || data.ThreadStarts==null || data.ThreadStops==null || data.ProcessStops==null)
            throw new WoiException("InvalidCache","Sampling/lifetime metadata is invalid.");
        return data;
    }
    private static IReadOnlyList<Bookmark> RecoverBookmarks(string root,string hash) {
        foreach(var candidate in Directory.EnumerateDirectories(root,hash+"-*").Where(x=>!x.Contains(".pending-"))
            .OrderByDescending(Directory.GetLastWriteTimeUtc)) {
            try{return CacheStore.ReadBookmarks(candidate);}catch(Exception){}
        }
        return [];
    }
    private static FileStream AcquireLock(string path,CancellationToken token) {
        var timer=Stopwatch.StartNew();
        while(true) {
            token.ThrowIfCancellationRequested();
            try{return new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException ex) when((ex.HResult & 0xffff)==32) {
                if(timer.Elapsed>TimeSpan.FromSeconds(30))throw new WoiException("CacheBusy","Another importer is using this cache. Try again after it finishes.");
                token.WaitHandle.WaitOne(50);
            }
        }
    }
    private static void RequireSpace(string path,ImportPolicy policy,ImportEnvironment env) {
        if(env.FreeBytes(path)<policy.MinimumFreeBytes)
            throw new WoiException("InsufficientDiskSpace","Insufficient free space for derived cache files. The original recording was preserved.");
    }
    private static void Guard(string directory,ImportPolicy policy,ImportEnvironment env,CancellationToken token,bool writing=true) {
        token.ThrowIfCancellationRequested();if(writing)RequireSpace(directory,policy,env);
        if(Directory.EnumerateFiles(directory).Sum(x=>new FileInfo(x).Length)>policy.DerivedBytes)
            throw new WoiException("SizeLimit","Combined derived files exceed the supported limit (2 GiB in the desktop app).");
        if(Process.GetCurrentProcess().WorkingSet64>policy.WorkerBytes)
            throw new WoiException("MemoryLimit","The import exceeded the worker memory budget.");
    }
    internal static bool IsDiskFull(Exception ex)=>ex is SqliteException{SqliteErrorCode:13} ||
        ex is IOException && (ex.HResult & 0xffff) is 39 or 112;
    private static bool IsTransfer(string op)=>op.Contains("Send",StringComparison.OrdinalIgnoreCase)||op.Contains("Recv",StringComparison.OrdinalIgnoreCase)||op.Contains("Receive",StringComparison.OrdinalIgnoreCase);
    private static string Detail(string category,long count)=>count==0?"No supported observations; unavailable is not zero.":category switch{
        "CPU"=>"Busy CPU samples; estimates require captured sampling interval metadata.",
        "Disk"=>"Completed physical reads/writes and recorded latency.",
        "File"=>"Logical file operations; requested byte coverage is reported separately.",
        "Network"=>"Recorded TCP/IP and UDP activity, including captured endpoints.",
        "Memory"=>"Captured process working sets; shared pages may overlap.",
        "Process"=>"OS-reported lifetimes and parent relationships.",
        _=>"Supported observations present in the recording."
    };
    private sealed class GuardedWriter(Action guard):TextWriter {
        public override Encoding Encoding=>Encoding.UTF8;
        public bool OutOfOrder{get;private set;}
        public override void WriteLine(string? value){if(value?.Contains("events out of order",StringComparison.OrdinalIgnoreCase)==true)OutOfOrder=true;guard();}
        public override void WriteLine(string format,params object?[] arg)=>WriteLine(string.Format(format,arg));
    }
}
public record SamplingInterval(double StartMs,double Milliseconds);
public sealed class ImportMetadata
{
    public long RecordedQpcFrequency{get;set;}
    public List<SamplingInterval> Intervals{get;set;}=[];
    public HashSet<(int Pid,double Time)> ProcessStarts{get;set;}=[];
    public HashSet<(int Pid,double Time)> ProcessStops{get;set;}=[];
    public HashSet<(int Tid,double Time)> ThreadStarts{get;set;}=[];
    public HashSet<(int Tid,double Time)> ThreadStops{get;set;}=[];
}
