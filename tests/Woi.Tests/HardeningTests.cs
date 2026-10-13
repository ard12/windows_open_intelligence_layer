using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Woi.Core;
using Woi.Trace;
using Xunit;

namespace Woi.Tests;

public sealed class HardeningTests
{
    private static string Fixture => Path.Combine(Environment.GetEnvironmentVariable("WOI_FIXTURES")!,
        "net452","net.4.5.2.x64.etl");
    private sealed class Progress(Action<ImportProgress> action):IProgress<ImportProgress>
    {public void Report(ImportProgress value)=>action(value);}
    private sealed class Temp:IDisposable
    {
        public string Root {get;}=Path.Combine(Path.GetTempPath(),"woi-hardening-"+Guid.NewGuid().ToString("N"));
        public Temp()=>Directory.CreateDirectory(Root);
        public void Dispose()=>Directory.Delete(Root,true);
    }

    [Fact] public void SamplingChangesApplyAtTheirExactHalfOpenBoundary()
    {
        SamplingInterval[] intervals=[new(100,1),new(2500,2),new(4000,.5)];
        Assert.Null(EtlReader.SamplingWeight(intervals,99.999));
        Assert.Equal(1,EtlReader.SamplingWeight(intervals,100));
        Assert.Equal(1,EtlReader.SamplingWeight(intervals,2499.999));
        Assert.Equal(2,EtlReader.SamplingWeight(intervals,2500));
        Assert.Equal(.5,EtlReader.SamplingWeight(intervals,99999));
    }
    [Fact] public void CpuWeightsUseEachProcessorIncludingIdlePredecessorContext()
    {
        var calculator=new CpuSampleWeightCalculator([new(0,1)]);
        Assert.Equal(1,calculator.Observe(0,0));
        Assert.Equal(1,calculator.Observe(1,.2));
        Assert.Equal(.9,calculator.Observe(0,.9)!.Value,8);
        Assert.Equal(.95,calculator.Observe(1,1.15)!.Value,8);
        Assert.Equal(1,calculator.Observe(0,3)); // Long gap: recorded nominal interval.
        Assert.Null(calculator.Observe(0,3)); // Nonpositive spacing is unavailable.
        Assert.Null(calculator.Observe(-1,4));
    }
    [Fact] public void CpuWeightsRequireCapturedIntervalsAndHonorChanges()
    {
        var calculator=new CpuSampleWeightCalculator([new(100,1),new(102,2),new(106,.5)]);
        Assert.Null(calculator.Observe(0,99.5));
        Assert.Equal(.5,calculator.Observe(0,100));
        Assert.Equal(2,calculator.Observe(0,102));
        Assert.Equal(.5,calculator.Observe(0,106));
        Assert.Equal(.4,calculator.Observe(0,106.4)!.Value,8);
        Assert.Null(calculator.Observe(0,double.NaN));
        Assert.Equal(.5,calculator.Observe(0,107));
    }
    [Fact] public void AllResourcePeaksSurviveChartDownsampling()
    {
        var values=Enumerable.Range(0,50000).Select(i=>new MetricPoint(i,1,1,0,0,
            i==12345?100000:0,i==22223?200000:0,i==43210?300000:0,0)).ToArray();
        var reduced=QueryEngine.Downsample(values,2000);
        Assert.InRange(reduced.Count,1,2000);
        foreach(int index in new[]{0,12345,22223,43210,49999})Assert.Contains(values[index],reduced);
        Assert.Throws<ArgumentOutOfRangeException>(()=>QueryEngine.Downsample(values,0));
    }
    [Fact] public void AbsentDiskOperationsRemainUnavailable()
    {
        using var fixture=new CoreTests.FakeFixture();
        Assert.All(fixture.Engine.Metrics(new(0,3000)),x=>Assert.Null(x.DiskOperations));
        Assert.Throws<WoiException>(()=>fixture.Engine.Contributors(new(0,3000,"nonexistent"),default));
        Assert.Throws<WoiException>(()=>fixture.Engine.Contributors(new(0,3000,Ranking:"unknown"),default));
    }
    [Fact] public void LocalOnlyPathsRejectNetworkSharesWithoutOpeningThem()
    {
        Assert.Equal("LocalInputRequired",Assert.Throws<WoiException>(()=>LocalPaths.RequireLocal(@"\\server\share\trace.etl")).Code);
        Assert.Equal("LocalInputRequired",Assert.Throws<WoiException>(()=>LocalPaths.RequireLocal(@"\\?\UNC\server\share\trace.etl")).Code);
    }
    [FixtureFact] public void FingerprintingCanBeCancelledBeforeAnyDerivedCacheIsCreated()
    {
        using var temp=new Temp();using var cts=new CancellationTokenSource();
        var digest=SHA256.HashData(File.ReadAllBytes(Fixture));
        var progress=new Progress(value=>{if(value.Stage=="Fingerprint" && value.Percent>0)cts.Cancel();});
        Assert.Throws<OperationCanceledException>(()=>EtlImporter.Import(Fixture,temp.Root,progress,cts.Token));
        Assert.Empty(Directory.GetFiles(temp.Root,"complete.json",SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(temp.Root));
        Assert.Equal(digest,SHA256.HashData(File.ReadAllBytes(Fixture)));
    }
    [FixtureFact] public void ImportAndBookmarksSupportLongCachePaths()
    {
        using var temp=new Temp();var root=temp.Root;
        while(root.Length<1100)root=Path.Combine(root,"nested-cache-directory");
        var session=EtlImporter.Import(Fixture,root,null,default);
        Assert.True(Path.Combine(session.CacheDirectory,"analysis.db").Length>1100);
        var bookmark=new Bookmark("long-path","saved evidence",0,100);
        using(var store=new CacheStore(session.CacheDirectory)) {
            Assert.Empty(store.Bookmarks());store.AddBookmark(bookmark);
        }
        var reopened=EtlImporter.Import(Fixture,root,null,default);
        Assert.Equal(session.CacheDirectory,reopened.CacheDirectory);
        using var readOnly=new CacheStore(reopened.CacheDirectory,true);
        Assert.Equal(bookmark,Assert.Single(readOnly.Bookmarks()));
    }
    [FixtureTheory]
    [InlineData("source","SizeLimit")]
    [InlineData("events","SizeLimit")]
    [InlineData("derived","SizeLimit")]
    [InlineData("memory","MemoryLimit")]
    [InlineData("space","InsufficientDiskSpace")]
    [InlineData("spaceDuringWrite","InsufficientDiskSpace")]
    public void ImportLimitsFailExplicitlyAndNeverPublish(string scenario,string code)
    {
        using var temp=new Temp();
        var digest=SHA256.HashData(File.ReadAllBytes(Fixture));
        var policy=scenario switch {
            "source"=>new ImportPolicy(SourceBytes:1),
            "events"=>new ImportPolicy(IndexedEvents:100),
            "derived"=>new ImportPolicy(DerivedBytes:1),
            "memory"=>new ImportPolicy(WorkerBytes:1),
            _=>new ImportPolicy()
        };
        int checks=0;
        var environment=new ImportEnvironment(_=>scenario=="space" || scenario=="spaceDuringWrite" && ++checks>2?0:long.MaxValue);
        Assert.Equal(code,Assert.Throws<WoiException>(()=>EtlImporter.Import(Fixture,temp.Root,null,default,policy,environment)).Code);
        Assert.Empty(Directory.GetFiles(temp.Root,"complete.json",SearchOption.AllDirectories));
        Assert.Empty(Directory.GetDirectories(temp.Root,"*.pending-*",SearchOption.AllDirectories));
        Assert.Equal(digest,SHA256.HashData(File.ReadAllBytes(Fixture)));
    }
    [FixtureTheory]
    [InlineData("events.etlx")]
    [InlineData("import.json")]
    [InlineData("analysis.db")]
    [InlineData("aggregates")]
    [InlineData("complete.json")]
    public void InvalidDerivedCachesRebuildFromOriginalAndPreserveRecoverableBookmarks(string damaged)
    {
        using var temp=new Temp();
        var session=EtlImporter.Import(Fixture,temp.Root,null,default);
        using(var store=new CacheStore(session.CacheDirectory))store.AddBookmark(new("keep","selected interval",100,1000));
        if(damaged=="aggregates") {
            using var connection=new SqliteConnection($"Data Source={Path.Combine(session.CacheDirectory,"analysis.db")};Pooling=False");
            connection.Open();using var command=connection.CreateCommand();command.CommandText="DELETE FROM aggregates WHERE second=0";command.ExecuteNonQuery();
        } else if(damaged=="analysis.db")File.WriteAllText(Path.Combine(session.CacheDirectory,damaged),"damaged database");
        else File.WriteAllText(Path.Combine(session.CacheDirectory,damaged),"damaged derived data");
        List<string> stages=[];
        var rebuilt=EtlImporter.Import(Fixture,temp.Root,new Progress(x=>stages.Add(x.Stage)),default);
        Assert.Contains("Decode",stages);
        using var reopened=new CacheStore(rebuilt.CacheDirectory,true);
        reopened.VerifyIntegrity();Assert.Equal(session.IndexedEvents,rebuilt.IndexedEvents);
        Assert.Equal("keep",Assert.Single(reopened.Bookmarks()).Id);
    }
    [FixtureFact] public void VerifiedCacheCanReopenWithoutAdditionalWritingSpace()
    {
        using var temp=new Temp();
        var first=EtlImporter.Import(Fixture,temp.Root,null,default);
        using(var state=new CacheStore(first.CacheDirectory))state.AddBookmark(new("still-here","bookmark",100,1000));
        var second=EtlImporter.Import(Fixture,temp.Root,null,default,new(),new(_=>0));
        Assert.Equal("still-here",Assert.Single(CacheStore.ReadBookmarks(second.CacheDirectory)).Id);
        Assert.Equal(first.SourceHash,second.SourceHash);
        Assert.Single(Directory.GetDirectories(temp.Root));
    }
    [FixtureFact] public void SequentialPagingVisitsRecordsOnceAndSupportsReplayAndFilterChanges()
    {
        using var temp=new Temp();
        var session=EtlImporter.Import(Fixture,temp.Root,null,default);
        using var reader=new EtlReader(session);
        var first=reader.QueryEvents(new(0,session.DurationMs),default);
        var profile=Assert.IsType<EventPageQueryProfile>(reader.LastEventPageProfile);
        Assert.True(profile.Completed);Assert.False(profile.ContinuedIterator);Assert.False(profile.ReusedLookahead);
        Assert.Equal(first.Events.Count,profile.ReturnedRecords);
        Assert.True(profile.NormalizedRecords>=first.Events.Count);
        Assert.True(profile.VisitedRecords>=profile.NormalizedRecords);
        Assert.True(profile.AllocatedBytes>0);
        Assert.InRange(profile.NormalizationMilliseconds,0,profile.TotalMilliseconds);
        long? cursor=first.NextCursor;var all=first.Events.Select(x=>x.Index).ToHashSet();
        for(int i=0;i<50;i++) {
            var page=reader.QueryEvents(new(0,session.DurationMs,Cursor:cursor),default);
            Assert.Equal(500,page.Events.Count);
            profile=Assert.IsType<EventPageQueryProfile>(reader.LastEventPageProfile);
            Assert.True(profile.Completed);Assert.True(profile.ContinuedIterator);Assert.True(profile.ReusedLookahead);
            Assert.Equal(page.Events.Count,profile.ReturnedRecords);
            Assert.True(profile.NormalizedRecords>=page.Events.Count-1); // One immutable lookahead is reused.
            Assert.All(page.Events,x=>Assert.True(all.Add(x.Index)));
            cursor=page.NextCursor;
        }
        Assert.True(reader.PagingRecordsVisited<session.IndexedEvents,"Consecutive pages must not rescan earlier records.");
        var replay=reader.QueryEvents(new(0,session.DurationMs),default);
        Assert.Equal(first.Events,replay.Events);
        var next=reader.QueryEvents(new(0,session.DurationMs,Cursor:replay.NextCursor,Category:"CPU"),default);
        Assert.All(next.Events,x=>{Assert.Equal("CPU",x.Category);Assert.True(x.Index>replay.NextCursor);});
        using var cts=new CancellationTokenSource();cts.Cancel();
        Assert.Throws<OperationCanceledException>(()=>reader.QueryEvents(new(0,session.DurationMs),cts.Token));
        profile=Assert.IsType<EventPageQueryProfile>(reader.LastEventPageProfile);
        Assert.False(profile.Completed);Assert.Equal(0,profile.ReturnedRecords);Assert.Equal(0,profile.VisitedRecords);
        Assert.Equal(first.Events,reader.QueryEvents(new(0,session.DurationMs),default).Events);
        Assert.False(reader.LastEventPageProfile!.ContinuedIterator);
        Assert.Throws<WoiException>(()=>reader.QueryEvents(new(0,session.DurationMs,Cursor:-1),default));
        Assert.Throws<WoiException>(()=>reader.QueryEvents(new(0,session.DurationMs,Cursor:session.IndexedEvents),default));
    }
    [FixtureFact] public async Task ConcurrentImportsShareOnePublishedCache()
    {
        using var temp=new Temp();
        var tasks=Enumerable.Range(0,2).Select(_=>Task.Run(()=>EtlImporter.Import(Fixture,temp.Root,null,default))).ToArray();
        var sessions=await Task.WhenAll(tasks);
        Assert.Equal(sessions[0].CacheDirectory,sessions[1].CacheDirectory);
        Assert.Single(Directory.GetDirectories(temp.Root));
        Assert.Single(Directory.GetFiles(temp.Root,"complete.json",SearchOption.AllDirectories));
    }
}
