using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Woi.Core;
using Woi.Trace;
using Xunit;
using Xunit.Abstractions;

namespace Woi.Tests;
public sealed class TraceTests(ITestOutputHelper output)
{
    [FixtureTheory]
    [InlineData("net452/net.4.5.2.x64.etl")]
    [InlineData("allocator/SimpleAllocator-dotnet6.etl")]
    public void RealEtlMatchesIndependentRawCpuAndDiskTotals(string relative)
    {
        var root=Environment.GetEnvironmentVariable("WOI_FIXTURES");
        Assert.True(root!=null,"Set WOI_FIXTURES to the downloaded Microsoft fixture directory.");
        var path=Path.Combine(root!,relative.Replace('/',Path.DirectorySeparatorChar));
        var hash=Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var cache=Path.Combine(Path.GetTempPath(),"woi-integration-"+Guid.NewGuid().ToString("N"));
        try {
            var clock=Stopwatch.StartNew();
            var session=EtlImporter.Import(path,cache,null,default);var cold=clock.Elapsed.TotalSeconds;
            using(var reader=new EtlReader(session))
            using(var store=new CacheStore(session.CacheDirectory)) {
                var engine=new QueryEngine(reader,store);
                Assert.True(session.RecordedQpcFrequency>0);
                var interval=new IntervalQuery(0,session.DurationMs);
                var contributions=engine.Contributors(interval,default);
                long rawCpu=0,rawDiskOps=0,rawDiskBytes=0,rawFileBytes=0;
                using(var source=new ETWTraceEventSource(path)) {
                    source.Kernel.PerfInfoSample += x=>{
                        if(x.TimeStampRelativeMSec>=0 && x.TimeStampRelativeMSec<session.DurationMs && (x.ThreadID!=0 || x.NonProcess))rawCpu++;
                    };
                    source.Kernel.DiskIORead += Disk;source.Kernel.DiskIOWrite += Disk;
                    source.Kernel.FileIORead += x=>rawFileBytes+=x.IoSize;
                    source.Kernel.FileIOWrite += x=>rawFileBytes+=x.IoSize;
                    void Disk(DiskIOTraceData x) {
                        if(x.TimeStampRelativeMSec>=0 && x.TimeStampRelativeMSec<session.DurationMs){
                            rawDiskOps++;rawDiskBytes+=(long)x.TransferSize;
                        }
                    }
                    source.Process();
                }
                Assert.Equal(rawCpu,contributions.Sum(x=>x.CpuSamples));
                Assert.Equal(rawDiskBytes,contributions.Sum(x=>x.DiskBytes ?? 0));
                Assert.Equal(rawDiskOps,engine.Metrics(interval).Sum(x=>x.DiskOperations));
                Assert.Equal(rawFileBytes,contributions.Sum(x=>x.FileBytes ?? 0));
                var partial = new IntervalQuery(session.DurationMs * .213, session.DurationMs * .789);
                var expectedResourceEvents = reader.ReadObservations(partial, default)
                    .Where(x => x.Category is "CPU" or "Disk" or "Network" || x.Category == "File" && x.Bytes != null)
                    .Select(x => x with { Target = "" }).ToArray();
                var lightweightResourceEvents = reader.ReadContributorObservations(partial, default).ToArray();
                Assert.Equal(expectedResourceEvents, lightweightResourceEvents);
                var partialContributors = engine.Contributors(partial, default);
                Assert.Equal(expectedResourceEvents.Count(x => x.Category == "CPU"), partialContributors.Sum(x => x.CpuSamples));
                Assert.Equal(expectedResourceEvents.Where(x => x.Category == "CPU").Sum(x => x.CpuWeightMs ?? 0), partialContributors.Sum(x => x.CpuMs ?? 0), 7);
                Assert.Equal(expectedResourceEvents.Where(x => x.Category == "Disk").Sum(x => x.Bytes ?? 0), partialContributors.Sum(x => x.DiskBytes ?? 0));
                var first=reader.QueryEvents(new(0,session.DurationMs),default);
                Assert.Equal(500,first.Events.Count);Assert.True(first.More);
                var next=reader.QueryEvents(new(0,session.DurationMs,Cursor:first.NextCursor),default);
                Assert.Empty(first.Events.Select(x=>x.Index).Intersect(next.Events.Select(x=>x.Index)));
                var observed=first.Events.First(x=>x.Category!="Unsupported");
                var detail=reader.GetEvidence(observed.Index,null,default);
                Assert.Equal(observed,detail.Observation);
                var filtered=reader.QueryEvents(new(0,session.DurationMs,ProcessId:observed.ProcessId,Category:observed.Category),default);
                Assert.All(filtered.Events,x=>{Assert.Equal(observed.Category,x.Category);if(observed.ProcessId!=null)Assert.Equal(observed.ProcessId,x.ProcessId);});
                var timings=new List<double>();
                for(int i=0;i<20;i++){clock.Restart();engine.Contributors(interval,default);reader.QueryEvents(new(0,session.DurationMs,Category:"CPU"),default);timings.Add(clock.Elapsed.TotalMilliseconds);}
                timings.Sort();
                var top=contributions.FirstOrDefault();
                output.WriteLine(JsonSerializer.Serialize(new {relative,session.IndexedEvents,cold,rawCpu,rawDiskOps,rawDiskBytes,rawFileBytes,
                    top=top?.Name,queryCombinedP95Ms=timings[18],peak=Process.GetCurrentProcess().PeakWorkingSet64},Wire.Json));
            }
            clock.Restart();var reopen=EtlImporter.Import(path,cache,null,default);
            Assert.Equal(session.SourceHash,reopen.SourceHash);Assert.Equal(session.IndexedEvents,reopen.IndexedEvents);
            output.WriteLine("Cached reopen seconds: "+clock.Elapsed.TotalSeconds);
            Assert.Equal(hash,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        } finally {if(Directory.Exists(cache))Directory.Delete(cache,true);}
    }
    [Fact] public void CancellationAndDamagedInputNeverPublishOrChangeSource()
    {
        var root=Path.Combine(Path.GetTempPath(),"woi-failure-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            var path=Path.Combine(root,"broken.etl");File.WriteAllBytes(path,[1,2,3,4]);
            var cts=new CancellationTokenSource();cts.Cancel();
            Assert.Throws<OperationCanceledException>(()=>EtlImporter.Import(path,Path.Combine(root,"cache"),null,cts.Token));
            Assert.ThrowsAny<Exception>(()=>EtlImporter.Import(path,Path.Combine(root,"cache"),null,default));
            Assert.Equal(new byte[]{1,2,3,4},File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(root,"complete.json",SearchOption.AllDirectories));
            Assert.Empty(Directory.GetDirectories(root,"*.pending-*",SearchOption.AllDirectories));
        } finally {Directory.Delete(root,true);}
    }
    [Fact] public void UnsupportedExtensionAndOversizedSourceFailExplicitly()
    {
        var root=Path.Combine(Path.GetTempPath(),"woi-limits-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {
            var other=Path.Combine(root,"trace.txt");File.WriteAllText(other,"data");
            Assert.Equal("UnsupportedInput",Assert.Throws<WoiException>(()=>EtlImporter.Import(other,root,null,default)).Code);
            var path=Path.Combine(root,"large.etl");using(var file=File.Create(path))file.SetLength(EtlImporter.MaximumSourceBytes+1);
            Assert.Equal("SizeLimit",Assert.Throws<WoiException>(()=>EtlImporter.Import(path,root,null,default)).Code);
        } finally {Directory.Delete(root,true);}
    }
}
