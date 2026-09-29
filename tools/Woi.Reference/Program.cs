using System.Text.Json;
using Microsoft.Windows.EventTracing;
using Woi.Core;
using Woi.Trace;

// Development-only: Microsoft TraceProcessing uses a separate native parser.
// It is deliberately excluded from the self-contained desktop distribution.
if(args.Length<2){Console.Error.WriteLine("Usage: Woi.Reference recording.etl report.json");return 2;}
var path=LocalPaths.RequireLocal(args[0]);
var output=LocalPaths.RequireLocal(args[1]);
var cache=Path.Combine(Path.GetTempPath(),"woi-reference-"+Guid.NewGuid().ToString("N"));
try {
    var session=EtlImporter.Import(path,cache,null,default);
    using var reader=new EtlReader(session);
    var observations=reader.ReadObservations(new(0,session.DurationMs),default).ToArray();
    using var processor=TraceProcessor.Create(path);
    var cpu=processor.UseCpuSamplingData();var disk=processor.UseDiskIOData();
    processor.Process();
    bool Inside(double time)=>time>=0 && time<session.DurationMs;
    var samples=cpu.Result.Samples.Where(x=>Inside((double)x.Timestamp.TotalMilliseconds) &&
        (x.Thread?.Id!=0 || x.IsExecutingDeferredProcedureCall==true || x.IsExecutingInterruptServicingRoutine==true)).ToArray();
    var disks=disk.Result.Activity.Where(x=>Inside((double)x.CompleteTime.TotalMilliseconds) && x.IOType.ToString() is "Read" or "Write").ToArray();
    var ownCpu=observations.Where(x=>x.Category=="CPU").ToArray();
    var ownDisk=observations.Where(x=>x.Category=="Disk").ToArray();
    var intervals=EtlImporterPublicMetadata(session.CacheDirectory);
    double? knownStart=intervals.Intervals.FirstOrDefault()?.StartMs;
    var weighted=knownStart is {} start?ownCpu.Where(x=>x.TimeMs>=start && x.CpuWeightMs!=null).ToArray():[];
    var refWeighted=knownStart is {} initial?samples.Where(x=>(double)x.Timestamp.TotalMilliseconds>=initial).ToArray():[];
    double ownWeight=weighted.Sum(x=>x.CpuWeightMs??0);
    double referenceWeight=refWeighted.Sum(x=>(double)x.Weight.TotalMilliseconds);
    double? difference=knownStart!=null && referenceWeight>0?Math.Abs(ownWeight-referenceWeight)/referenceWeight*100:null;
    long ownBytes=ownDisk.Sum(x=>x.Bytes??0),referenceBytes=disks.Sum(x=>x.Size.Bytes);
    double ownLatency=ownDisk.Sum(x=>x.DurationMs??0),referenceLatency=disks.Sum(x=>(double)x.IODuration.TotalMilliseconds);
    var ownRanks=ownCpu.GroupBy(x=>x.Pid).Select(x=>new {pid=x.Key,samples=x.Count()}).OrderByDescending(x=>x.samples).ToArray();
    var referenceRanks=samples.GroupBy(x=>x.Process?.Id??-1).Select(x=>new {pid=x.Key,samples=x.Count()}).OrderByDescending(x=>x.samples).ToArray();
    var checks=new {
        cpuCountsMatch=ownCpu.Length==samples.Length,diskOperationsMatch=ownDisk.Length==disks.Length,
        diskBytesMatch=ownBytes==referenceBytes,
        cpuWeightWithinOnePercent=difference is {} delta?(bool?)(delta<=1):null
    };
    var report=new {
        schema=1,sourceSha256=session.SourceHash,sourceFile=Path.GetFileName(path),
        analysisVersion=Wire.AnalysisVersion,traceEventVersion=EtlImporter.ParserVersion,
        referenceParser="Microsoft.Windows.EventTracing.Processing.All 1.12.10",
        interval=new {startMs=0,endMs=session.DurationMs},checks,
        cpu=new {analyzerSamples=ownCpu.Length,referenceSamples=samples.Length,knownWeightStartMs=knownStart,
            analyzerWeightMs=ownWeight,referenceWeightMs=referenceWeight,weightDifferencePercent=difference,
            analyzerPidRanking=ownRanks.Take(10),referencePidRanking=referenceRanks.Take(10)},
        disk=new {analyzerOperations=ownDisk.Length,referenceOperations=disks.Length,analyzerBytes=ownBytes,
            referenceBytes,analyzerResponsePayloadMs=ownLatency,referencePairedDurationMs=referenceLatency,
            durationDifferencePercent=referenceLatency>0?Math.Abs(ownLatency-referenceLatency)/referenceLatency*100:(double?)null},
        diagnostics=new {
            diskTypes=disks.GroupBy(x=>x.IOType.ToString()).Select(g=>new{type=g.Key,count=g.Count(),bytes=g.Sum(x=>x.Size.Bytes)}),
            unusualWeights=samples.Where(x=>Math.Abs((double)x.Weight.TotalMilliseconds-1)>.00001).Take(20).Select(x=>new{time=(double)x.Timestamp.TotalMilliseconds,cpu=x.Processor,weight=(double)x.Weight.TotalMilliseconds}),
            latencyDifferences=ownDisk.Select(x=>new {own=x,reference=disks.OrderBy(d=>Math.Abs((double)d.CompleteTime.TotalMilliseconds-x.TimeMs)).First()}).Where(x=>Math.Abs((x.own.DurationMs??0)-(double)x.reference.IODuration.TotalMilliseconds)>.00001).Take(20).Select(x=>new{time=x.own.TimeMs,own=x.own.DurationMs,reference=(double)x.reference.IODuration.TotalMilliseconds,referenceStart=(double)x.reference.InitializeTime.TotalMilliseconds,referenceEnd=(double)x.reference.CompleteTime.TotalMilliseconds})
        },
        limitations=new [] {
            "Same processing technology as WPA tables, but this run does not constitute interactive WPA validation.",
            "Busy CPU excludes pure idle thread 0; interrupt and DPC samples remain included.",
            "CPU weights compare only after the first recorded sampling interval.",
            "Disk counts use read/write completion timestamps and [start,end) inclusion; flushes are excluded.",
            "Disk response payload and paired issue/completion duration are distinct measurements; their difference is reported, not asserted as an exact match.",
            "PID rankings are a diagnostic comparison; imported lifetime identities remain canonical."
        }
    };
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions(Wire.Json){WriteIndented=true}));
    Console.WriteLine(JsonSerializer.Serialize(checks,Wire.Json));
    return checks.cpuCountsMatch && checks.diskOperationsMatch && checks.diskBytesMatch &&
        checks.cpuWeightWithinOnePercent==true?0:1;
} catch(Exception ex){Console.Error.WriteLine(ex);return 2;}
finally{if(Directory.Exists(cache))Directory.Delete(cache,true);}

static ImportMetadata EtlImporterPublicMetadata(string directory)=>JsonSerializer.Deserialize<ImportMetadata>(
    File.ReadAllText(Path.Combine(directory,"import.json")),Wire.Json)!;
