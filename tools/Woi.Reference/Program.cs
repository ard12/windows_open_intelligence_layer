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
    // Diagnostic alternatives only: compare observed sample spacing with native
    // weights without changing the shipping recorded-interval estimator.
    var spacing = new List<(double Native, double? Previous, double? Next, double Interval)>();
    foreach (var processorSamples in cpu.Result.Samples.GroupBy(x => x.Processor)) {
        var ordered = processorSamples.OrderBy(x => x.Timestamp).ToArray();
        for (int i = 0; i < ordered.Length; i++) {
            var sample = ordered[i];
            double time = (double)sample.Timestamp.TotalMilliseconds;
            if (!Inside(time) || knownStart == null || time < knownStart ||
                sample.Thread?.Id == 0 && sample.IsExecutingDeferredProcedureCall != true && sample.IsExecutingInterruptServicingRoutine != true)
                continue;
            var interval = intervals.Intervals.LastOrDefault(x => x.StartMs <= time)?.Milliseconds;
            if (interval == null) continue;
            spacing.Add(((double)sample.Weight.TotalMilliseconds,
                i > 0 ? time - (double)ordered[i-1].Timestamp.TotalMilliseconds : null,
                i+1 < ordered.Length ? (double)ordered[i+1].Timestamp.TotalMilliseconds - time : null,
                interval.Value));
        }
    }
    object SpacingComparison(Func<(double Native, double? Previous, double? Next, double Interval), double?> estimate) {
        var available = spacing.Select(x => (x.Native, Value:estimate(x))).Where(x => x.Value != null).ToArray();
        var total = available.Sum(x => x.Value!.Value);
        var reference = available.Sum(x => x.Native);
        return new { samples=available.Length, estimatedMs=total, referenceMs=reference,
            totalDifferencePercent=reference>0 ? Math.Abs(total-reference)/reference*100 : (double?)null,
            meanAbsolutePerSampleErrorMs=available.Length>0 ? available.Average(x => Math.Abs(x.Value!.Value-x.Native)) : (double?)null,
            matchingSamples=available.Count(x => Math.Abs(x.Value!.Value-x.Native)<.00001) };
    }
    var ownWeightsByTime = weighted.GroupBy(x => Math.Round(x.TimeMs,4))
        .ToDictionary(x => x.Key, x => x.Select(v => v.CpuWeightMs!.Value).Order().ToArray());
    var referenceWeightsByTime = refWeighted.GroupBy(x => Math.Round((double)x.Timestamp.TotalMilliseconds,4))
        .ToDictionary(x => x.Key, x => x.Select(v => (double)v.Weight.TotalMilliseconds).Order().ToArray());
    int mismatchedSampleGroups = ownWeightsByTime.Keys.Union(referenceWeightsByTime.Keys).Count(time =>
        !ownWeightsByTime.TryGetValue(time,out var own) || !referenceWeightsByTime.TryGetValue(time,out var reference) ||
        own.Length != reference.Length || own.Zip(reference).Any(x => Math.Abs(x.First-x.Second)>.0001));
    var partialIntervals = new[] {(Start:0d, End:session.DurationMs/3), (Start:session.DurationMs*.213, End:session.DurationMs*.789),
        (Start:Math.Max(0,session.DurationMs-1000), End:session.DurationMs)};
    var partialWeightChecks = partialIntervals.Select(window => {
        var own = weighted.Where(x => x.TimeMs>=window.Start && x.TimeMs<window.End).ToArray();
        var reference = refWeighted.Where(x => (double)x.Timestamp.TotalMilliseconds>=window.Start && (double)x.Timestamp.TotalMilliseconds<window.End).ToArray();
        double ownMs=own.Sum(x => x.CpuWeightMs!.Value), refMs=reference.Sum(x => (double)x.Weight.TotalMilliseconds);
        return new { startMs=window.Start,endMs=window.End,analyzerSamples=own.Length,referenceSamples=reference.Length,
            analyzerWeightMs=ownMs,referenceWeightMs=refMs,differencePercent=refMs>0 ? Math.Abs(ownMs-refMs)/refMs*100 : (double?)null };
    }).ToArray();
    var checks=new {
        cpuCountsMatch=ownCpu.Length==samples.Length,diskOperationsMatch=ownDisk.Length==disks.Length,
        cpuSampleWeightGroupsMatch=mismatchedSampleGroups==0,partialWeightsMatch=partialWeightChecks.All(x=>x.analyzerSamples==x.referenceSamples && (x.differencePercent==null || x.differencePercent<=1)),
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
            mismatchedSampleGroups,partialWeightChecks,
            analyzerPidRanking=ownRanks.Take(10),referencePidRanking=referenceRanks.Take(10)},
        disk=new {analyzerOperations=ownDisk.Length,referenceOperations=disks.Length,analyzerBytes=ownBytes,
            referenceBytes,analyzerResponsePayloadMs=ownLatency,referencePairedDurationMs=referenceLatency,
            durationDifferencePercent=referenceLatency>0?Math.Abs(ownLatency-referenceLatency)/referenceLatency*100:(double?)null},
        diagnostics=new {
            diskTypes=disks.GroupBy(x=>x.IOType.ToString()).Select(g=>new{type=g.Key,count=g.Count(),bytes=g.Sum(x=>x.Size.Bytes)}),
            sampleSpacing=new { collectedSamples=cpu.Result.Samples.Count, weightedBusySamples=spacing.Count,
                previous=SpacingComparison(x => x.Previous), next=SpacingComparison(x => x.Next),
                previousOutlierFallback=new[] {1.1,1.25,1.5,2.0,4.0}.Select(threshold => new {
                    threshold, comparison=SpacingComparison(x => x.Previous is {} value ? value>threshold*x.Interval ? x.Interval : value : null) }),
                previousMismatchExamples=spacing.Where(x => x.Previous != null && Math.Abs(x.Previous.Value-x.Native)>.00001).Take(30)
                    .Select(x => new {native=x.Native,previous=x.Previous,next=x.Next,interval=x.Interval}),
                previousCapped=SpacingComparison(x => x.Previous is {} value ? Math.Min(value,x.Interval) : null),
                nextCapped=SpacingComparison(x => x.Next is {} value ? Math.Min(value,x.Interval) : null) },
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
    return checks.cpuCountsMatch && checks.cpuSampleWeightGroupsMatch && checks.partialWeightsMatch && checks.diskOperationsMatch && checks.diskBytesMatch &&
        checks.cpuWeightWithinOnePercent==true?0:1;
} catch(Exception ex){Console.Error.WriteLine(ex);return 2;}
finally{if(Directory.Exists(cache))Directory.Delete(cache,true);}

static ImportMetadata EtlImporterPublicMetadata(string directory)=>JsonSerializer.Deserialize<ImportMetadata>(
    File.ReadAllText(Path.Combine(directory,"import.json")),Wire.Json)!;
