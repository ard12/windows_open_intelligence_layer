using System.Diagnostics;
using System.Text.Json;
using Woi.Core;
namespace Woi.Validation;
public static class AggregateStress
{
    private const int Count=5_000_000;
    private const double Duration=Count*2d;
    public static int Run(string reportPath)
    {
        var output=Path.GetFullPath(reportPath);Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        string cache=Path.Combine(Path.GetDirectoryName(output)!,"aggregate-cache-"+Guid.NewGuid().ToString("N"));
        var timer=Stopwatch.StartNew();var builder=new AnalysisBuilder();
        long cpu=0,diskBytes=0,fileBytes=0;
        for(int index=0;index<Count;index++){
            var value=Value(index);builder.Add(value);
            if(value.Category=="CPU")cpu++;if(value.Category=="Disk")diskBytes+=value.Bytes??0;
            if(value.Category=="File")fileBytes+=value.Bytes??0;
        }
        var rows=builder.Finish();double aggregateMs=timer.Elapsed.TotalMilliseconds;
        var session=new TraceSession("synthetic","Generated aggregation stress","synthetic","generated",Wire.AnalysisVersion,cache,
            DateTime.UtcNow,Duration,12,Count,0,[
                new("CPU",Availability.Available,cpu,"Generated known samples"),
                new("Disk",Availability.Available,Count/10,"Generated completed disk operations"),
                new("FileBytes",Availability.Available,Count/10,"Generated logical operations")],["Synthetic aggregation dataset, not an ETL recording."],0,0);
        using var store=new CacheStore(cache);
        var entities=Enumerable.Range(0,20).Select(index=>new EntityLifetime("p"+index,"Process",index+10,"generated-"+index,0,Duration,true,true,null,null,"","")).ToArray();
        store.Save(session,entities,rows);
        using var reader=new GeneratedReader(session);
        var engine=new QueryEngine(reader,store);
        var queries=new List<double>();IReadOnlyList<Contributor> full=[];
        for(int repeat=0;repeat<20;repeat++){
            timer.Restart();full=engine.Contributors(new(0,Duration),default);queries.Add(timer.Elapsed.TotalMilliseconds);
        }
        if(full.Sum(x=>x.CpuSamples??0)!=cpu || full.Sum(x=>x.DiskBytes??0)!=diskBytes || full.Sum(x=>x.FileBytes??0)!=fileBytes)
            throw new Exception("Five-million-observation totals did not match the independent running counters.");
        var partial=new IntervalQuery(30250.25,66625.75);
        var selected=engine.Contributors(partial,default);
        // Independent arithmetic: CPU slots are 0..7 in each ten-record group.
        int first=(int)Math.Ceiling(partial.StartMs/2),last=(int)Math.Ceiling(partial.EndMs/2);
        long expectedCpu=0;for(int index=first;index<last;index++)if(index%10<8)expectedCpu++;
        if(selected.Sum(x=>x.CpuSamples??0)!=expectedCpu)throw new Exception("Partial selection did not match arithmetic sample inclusion.");
        timer.Restart();var metrics=engine.Metrics(new(0,Duration));double metricsMs=timer.Elapsed.TotalMilliseconds;
        if(metrics.Count>2000 || metrics[0].CpuPercent!=null)throw new Exception("Chart budget or unavailable-weight semantics failed.");
        double coldContributorMs=queries[0];queries.Sort();using var process=Process.GetCurrentProcess();
        var report=new{schema=1,success=true,kind="synthetic aggregation and query stress",observations=Count,buckets=rows.Count,
            retainedSeconds=Duration/1000,aggregateMs,contributorsP95Ms=queries[18],coldContributorMs,queryCacheEntries=16,metricsMs,peakBytes=process.PeakWorkingSet64,
            cpuSamples=cpu,diskBytes,fileBytes,visiblePoints=metrics.Count,partialIntervalMatched=true,
            limitations="This validates aggregation/query scale; it does not certify ETL decoding at five million events."};
        File.WriteAllText(output,JsonSerializer.Serialize(report,new JsonSerializerOptions(Wire.Json){WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(report,Wire.Json));return 0;
    }
    private static Observation Value(int index)
    {
        double ms=index*2d;string category=index%10<8?"CPU":index%10==8?"Disk":"File";int process=index%20;
        var reference=new EvidenceReference("synthetic","generated",index,index*20000L,ms,"generated",category,process+10,0);
        return new(index,ms,index*20000L,category,category,"p"+process,null,process+10,0,"generated-"+process,"synthetic",
            category=="CPU"?null:category=="Disk"?4096:1024,category=="Disk"?index%50:null,
            category=="CPU" && index>=1000?1:null,null,reference);
    }
    private sealed class GeneratedReader(TraceSession session):ITraceReader
    {
        public TraceSession Session=>session;
        public IEnumerable<Observation> ReadObservations(IntervalQuery query,CancellationToken token)
        {
            for(int index=Math.Max(0,(int)Math.Ceiling(query.StartMs/2));index<Math.Min(Count,(int)Math.Ceiling(query.EndMs/2));index++){
                token.ThrowIfCancellationRequested();var value=Value(index);
                if(query.ProcessId==null || value.ProcessId==query.ProcessId)yield return value;
            }
        }
        public EventPage QueryEvents(EventQuery query,CancellationToken token)=>throw new NotSupportedException();
        public EvidenceDetail GetEvidence(long index,string? symbols,CancellationToken token)=>throw new NotSupportedException();
        public EntityDetail GetEntity(string id,CancellationToken token)=>throw new NotSupportedException();
        public void Dispose(){}
    }
}
