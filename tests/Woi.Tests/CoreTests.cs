using Woi.Core;
using Xunit;

namespace Woi.Tests;
public sealed class CoreTests
{
    private static EntityLifetime Life(string id, double start, double end) => new(id,"Process",7,"work",start,end,true,true,null,null,"","");
    internal static Observation Obs(long index, double ms, string category="CPU", string? process="p", double? weight=1,
        long? bytes=null, double? latency=null, double? memory=null) => new(index,ms,(long)(ms*10000),category,
        category=="Network"?"TcpIp/Send":category,process,null,7,8,"work","captured",bytes,latency,weight,memory,
        new("hash","parser",index,(long)(ms*10000),ms,"provider",category,7,8));
    [Theory][InlineData("Process")][InlineData("Thread")]
    public void IdReuseUsesHalfOpenLifetimesAndRejectsAmbiguity(string kind)
    {
        var old=Life("old",0,100) with {Kind=kind};var next=Life("next",100,200) with {Kind=kind};
        Assert.Equal("old",LifetimeResolver.Resolve([old,next],7,99,kind)?.Id);
        Assert.Equal("next",LifetimeResolver.Resolve([old,next],7,100,kind)?.Id);
        Assert.Null(LifetimeResolver.Resolve([old,next],7,200,kind));
        Assert.Null(LifetimeResolver.Resolve([old,Life("overlap",50,150) with {Kind=kind}],7,75,kind));
    }
    [Fact] public void MemorySumsLatestCapturedProcessSnapshotsWithinBucket()
    {
        var b=new AnalysisBuilder();b.Add(Obs(1,100,"Memory","p",memory:100));
        b.Add(Obs(2,200,"Memory","q",memory:200));b.Add(Obs(3,300,"Memory","p",memory:150));
        Assert.Equal(350,b.Finish().Single(x=>x.ProcessId=="").WorkingSetBytes);
        Assert.Null(b.Finish().FirstOrDefault(x=>x.Second==1)?.WorkingSetBytes);
    }
    [Fact] public void DiskP95UsesCompletedOperationsAndNearestRank()
    {
        var b=new AnalysisBuilder();
        for(int i=1;i<=20;i++)b.Add(Obs(i,i,"Disk",bytes:4096,latency:i));
        var row=b.Finish().Single(x=>x.ProcessId=="");
        Assert.Equal(19,row.DiskP95Ms);Assert.Equal(20,row.DiskOperations);Assert.Equal(81920,row.DiskBytes);
    }
    [Fact] public void IncidentHeuristicsRequireConsecutiveBucketsAndKnownSampleWeights()
    {
        AggregateRow Row(int sec, long weighted=1)=>new(sec,"",1,900,weighted,4096,0,0,25,10,25,null,[]);
        Assert.Equal(2,IncidentDetector.Detect([Row(0),Row(1),Row(2)],1,3000,new()).Count);
        Assert.Empty(IncidentDetector.Detect([Row(0),Row(2),Row(3)],1,4000,new()));
        Assert.Single(IncidentDetector.Detect([Row(0),Row(1),Row(2)],1,2100,new())); // Partial third second cannot establish 3s CPU.
        Assert.Single(IncidentDetector.Detect([Row(0,0),Row(1,0),Row(2,0)],1,3000,new()));
        Assert.Throws<WoiException>(()=>IncidentDetector.Validate(new(double.NaN)));
    }
    [Fact] public void DownsamplingRetainsEndpointsAndPeaksWithinBudget()
    {
        var data=Enumerable.Range(0,50000).Select(i=>new MetricPoint(i,1,i==24513?100:1,i==28742?999999:0,
            i==36901?300:1,null,null,null,1)).ToArray();
        var result=QueryEngine.Downsample(data,2000);
        Assert.InRange(result.Count,1,2000);Assert.Contains(data[0],result);Assert.Contains(data[^1],result);
        Assert.Contains(data[24513],result);Assert.Contains(data[28742],result);Assert.Contains(data[36901],result);
    }
    [Theory]
    [InlineData(0,3000,6)][InlineData(150,2500,4)][InlineData(100,400,1)]
    [InlineData(1000,2000,2)][InlineData(0,250,1)][InlineData(2100,2500,1)]
    public void ContributorIntervalsExcludeOutsideEvents(double start,double end,long count)
    {
        using var fixture=new FakeFixture();
        Assert.Equal(count,fixture.Engine.Contributors(new(start,end),default).Sum(x=>x.CpuSamples));
    }
    [Theory][InlineData(true,"q")][InlineData(false,"p")]
    public void CpuRankingUsesCapturedWeightsAndFallsBackToCounts(bool known,string first)
    {
        using var fixture=new FakeFixture();
        var values=new[]{Obs(10,1010,process:"p",weight:1),Obs(11,1020,process:"p",weight:1),Obs(12,1030,process:"q",weight:known?3:null)};
        var builder=new AnalysisBuilder();foreach(var value in values)builder.Add(value);
        var session=fixture.Reader.Session;
        fixture.Store.Save(session,[Life("p",0,3000),Life("q",0,3000) with {Name="second"}],builder.Finish());
        using var reader=new FakeReader(session,values);var engine=new QueryEngine(reader,fixture.Store);
        Assert.Equal(first,engine.Contributors(new(1000,2000),default)[0].ProcessId);
        Assert.Equal(first,engine.Contributors(new(1001,1999),default)[0].ProcessId);
        var report = engine.Report(new(1000,2000),default);
        Assert.Contains(known ? "estimated sampled CPU time" : "recorded CPU samples", report.Explanation);
        Assert.Contains(known ? "second accounted for 60.0%" : "work accounted for 66.7%", report.Explanation);
    }
    [Fact] public void MissingCoverageStaysNullAndUnavailableInExport()
    {
        using var fixture=new FakeFixture(false);
        var contributor=Assert.Single(fixture.Engine.Contributors(new(0,3000),default));
        Assert.Null(contributor.DiskBytes);Assert.Null(contributor.FileBytes);Assert.Null(contributor.NetworkBytes);
        Assert.Null(contributor.CpuPercent);Assert.Null(contributor.BaselineCpuPercent);
        Assert.All(fixture.Engine.Metrics(new(0,3000)),x=>{Assert.Null(x.CpuPercent);Assert.Null(x.DiskBytes);});
        var path=Path.Combine(Path.GetTempPath(),"woi-report-"+Guid.NewGuid().ToString("N")+".md");
        try {
            fixture.Engine.Export(path,"markdown",new(0,3000),default);
            Assert.Contains("unavailable",File.ReadAllText(path));
        } finally { if (File.Exists(path)) File.Delete(path); }
        var report=fixture.Engine.Report(new(0,3000),default);
        Assert.Equal(Provenance.ResourceAttribution,report.Provenance);
        Assert.Contains("Physical disk transfer coverage is unavailable", report.Explanation);
        Assert.Contains(report.Findings!,x=>x.Provenance==Provenance.Observed);
        Assert.Contains(report.Findings!,x=>x.Provenance==Provenance.TemporalCorrelation);
        Assert.DoesNotContain(report.Findings!,x=>x.Provenance==Provenance.Hypothesis);
        Assert.Throws<WoiException>(()=>fixture.Engine.Clamp(new(4000,5000)));
    }
    [Fact] public void AbsentCpuProviderNeverBecomesZeroSamples()
    {
        using var fixture=new FakeFixture(cpuCaptured:false);
        var contributor=Assert.Single(fixture.Engine.Contributors(new(0,3000),default));
        Assert.Null(contributor.CpuSamples);Assert.Null(contributor.CpuPercent);Assert.True(contributor.DiskBytes>0);
        Assert.All(fixture.Engine.Metrics(new(0,3000)),x=>Assert.Null(x.CpuSamples));
        var report=fixture.Engine.Report(new(0,3000),default);
        Assert.Contains(report.Findings!,x=>x.Provenance==Provenance.Observed && x.Text.Contains("coverage is unavailable"));
        Assert.Contains("CPU sampling is unavailable", report.Explanation);
        Assert.DoesNotContain("No CPU samples were recorded", report.Explanation);
    }
    [Fact]
    public void QueryProfileMeasuresEdgesWithoutChangingPartialTotals()
    {
        using var fixture = new FakeFixture();
        var result = fixture.Engine.Contributors(new(250, 2250), default);
        Assert.Equal(4, result.Sum(x => x.CpuSamples));
        var profile = fixture.Engine.LastContributorProfile!;
        Assert.Equal(2, profile.SelectedEdgeObservations);
        Assert.Equal(1, profile.BaselineEdgeObservations);
        Assert.Null(profile.VisitedRecords);
        Assert.True(profile.AllocatedBytes >= 0);
        Assert.True(profile.TotalMilliseconds >= profile.SelectedEdgeReplayMilliseconds);
    }

    internal sealed class FakeFixture : IDisposable
    {
        public string Directory {get;}=Path.Combine(Path.GetTempPath(),"woi-tests-"+Guid.NewGuid().ToString("N"));
        public CacheStore Store {get;}
        public FakeReader Reader {get;}
        public QueryEngine Engine {get;}
        public FakeFixture(bool weights=true, bool cpuCaptured=true)
        {
            System.IO.Directory.CreateDirectory(Directory);
            var caps=new[]{new Capability(cpuCaptured ? "CPU" : "Disk",Availability.Available,6,"observations")};
            var session=new TraceSession("hash","source","hash","parser",Wire.AnalysisVersion,Directory,DateTime.UtcNow,3000,1,6,0,caps,[],0,0);
            Reader=new(session,new[]{100d,500d,1100d,1500d,2100d,2900d}.Select((t,i)=>cpuCaptured ? Obs(i,t,weight:weights?1:null) : Obs(i,t,"Disk",bytes:4096,latency:1)).ToArray());
            Store=new(Directory);var b=new AnalysisBuilder();foreach(var x in Reader.Values)b.Add(x);
            Store.Save(session,[Life("p",0,3000)],b.Finish());Engine=new(Reader,Store);
        }
        public void Dispose(){Store.Dispose();System.IO.Directory.Delete(Directory,true);}
    }
    internal sealed class FakeReader(TraceSession session,IReadOnlyList<Observation> values):ITraceReader
    {
        public TraceSession Session=>session;
        public IReadOnlyList<Observation> Values=>values;
        public IEnumerable<Observation> ReadObservations(IntervalQuery q,CancellationToken token)=>values.Where(x=>x.TimeMs>=q.StartMs && x.TimeMs<q.EndMs && (q.ProcessId==null||q.ProcessId==x.ProcessId));
        public EventPage QueryEvents(EventQuery q,CancellationToken token)=>throw new NotImplementedException();
        public EvidenceDetail GetEvidence(long i,string? s,CancellationToken t)=>throw new NotImplementedException();
        public EntityDetail GetEntity(string i,CancellationToken t)=>throw new NotImplementedException();
        public void Dispose(){}
    }
}
