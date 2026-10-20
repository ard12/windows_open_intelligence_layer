using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Woi.Core;
using Xunit;

namespace Woi.Tests;
public sealed class FixtureTheoryAttribute : TheoryAttribute
{
    public FixtureTheoryAttribute() { if(Environment.GetEnvironmentVariable("WOI_FIXTURES")==null)Skip="Set WOI_FIXTURES or run scripts/test.ps1 -DownloadFixtures for ETL integration tests."; }
}
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute() { if(Environment.GetEnvironmentVariable("WOI_FIXTURES")==null)Skip="Set WOI_FIXTURES or run scripts/test.ps1 -DownloadFixtures for worker integration tests."; }
}
public sealed class ProtocolTests
{
    private static string Root {
        get {var d=new DirectoryInfo(AppContext.BaseDirectory);while(d!=null && !File.Exists(Path.Combine(d.FullName,"WindowsOpenIntelligence.slnx")))d=d.Parent;
            return d?.FullName ?? throw new Exception("Solution root not found.");}
    }
    private static string Fixture=>Path.Combine(Environment.GetEnvironmentVariable("WOI_FIXTURES")!,"allocator","SimpleAllocator-dotnet6.etl");
    [Fact] public async Task MalformedRequestsReceiveErrorsAndWorkerRemainsUsable()
    {
        using var worker=new Harness();
        await worker.Process.StandardInput.WriteLineAsync("bad json");
        var bad=await worker.Read();Assert.Equal("ProtocolError",bad.GetProperty("code").GetString());
        await worker.Send("version","summary",new{},99);
        Assert.Equal("ProtocolError",(await worker.Result("version")).GetProperty("code").GetString());
        await worker.Send("params","cancel",null);
        Assert.Equal("ProtocolError",(await worker.Result("params")).GetProperty("code").GetString());
        await worker.Process.StandardInput.WriteLineAsync(new string('x',1024*1024+5000));
        Assert.Equal("ProtocolError",(await worker.Read()).GetProperty("code").GetString());
        await worker.Send("valid","summary",new{});
        Assert.Equal("NoTrace",(await worker.Result("valid")).GetProperty("code").GetString());
    }
    [FixtureFact] public async Task CancellationLeavesSourceIntactAndNoCompletedCache()
    {
        using var worker=new Harness();
        var hash=SHA256.HashData(File.ReadAllBytes(Fixture));
        await worker.Send("import","import",new{path=Fixture});
        while(true) {
            var response=await worker.Read();
            if(response.GetProperty("kind").GetString()=="progress" && response.GetProperty("result").GetProperty("stage").GetString()=="Decode")break;
        }
        await worker.Send("cancel","cancel",new{requestId="import"});
        Assert.Equal("Cancelled",(await worker.Result("import")).GetProperty("code").GetString());
        Assert.Equal(hash,SHA256.HashData(File.ReadAllBytes(Fixture)));
        Assert.Empty(Directory.GetFiles(worker.Cache,"complete.json",SearchOption.AllDirectories));
    }
    [FixtureFact] public async Task WorkerCrashDoesNotPublishAndRestartRebuilds()
    {
        using var worker=new Harness();
        await worker.Send("import","import",new{path=Fixture});
        while(true) {
            var response=await worker.Read();
            if(response.GetProperty("kind").GetString()=="progress" && response.GetProperty("result").GetProperty("stage").GetString()=="Decode")break;
        }
        worker.Process.Kill(true);await worker.Process.WaitForExitAsync();
        Assert.Empty(Directory.GetFiles(worker.Cache,"complete.json",SearchOption.AllDirectories));
        var session=Woi.Trace.EtlImporter.Import(Fixture,worker.Cache,null,default);
        Assert.True(session.IndexedEvents>0);
        Assert.Single(Directory.GetFiles(worker.Cache,"complete.json",SearchOption.AllDirectories));
    }
    [FixtureFact] public async Task ProtocolImportsExportsAndRejectsArbitrarySql()
    {
        using var worker=new Harness();
        await worker.Send("import","import",new{path=Fixture});
        var summary=(await worker.Result("import")).GetProperty("result");
        double end=summary.GetProperty("session").GetProperty("durationMs").GetDouble();
        await worker.Send("sql","sql",new{query="DROP TABLE entities"});
        Assert.Equal("UnknownMethod",(await worker.Result("sql")).GetProperty("code").GetString());
        await worker.Send("bookmark","bookmark",new Bookmark("one","window",0,end+100));
        Assert.Equal(end,(await worker.Result("bookmark")).GetProperty("result").GetProperty("endMs").GetDouble());
        var report=Path.Combine(worker.Cache,"report.md");
        await worker.Send("export","export",new{path=report,format="markdown",interval=new IntervalQuery(0,end)});
        Assert.Equal("result",(await worker.Result("export")).GetProperty("kind").GetString());
        Assert.Contains("not raw ETL offsets",File.ReadAllText(report));
        await worker.Send("cpu-events", "events", new EventQuery(0, end, Category: "CPU"));
        var events = (await worker.Result("cpu-events")).GetProperty("result").Deserialize<EventPage>(Wire.Json)!;
        var sample = events.Events.First(x => x.ProcessId != null && x.ThreadId != null);
        await worker.Send("threads", "entity", new { id = sample.ProcessId });
        var entity = (await worker.Result("threads")).GetProperty("result").Deserialize<EntityDetail>(Wire.Json)!;
        Assert.Contains(entity.Threads, x => x.Id == sample.ThreadId && x.OwnerId == sample.ProcessId);
        var query = new CpuStackQuery(0, end, sample.ProcessId, ThreadId: sample.ThreadId);
        await worker.Send("thread-stacks", "cpuStacks", query);
        var stacks = (await worker.Result("thread-stacks")).GetProperty("result").Deserialize<CpuStackPage>(Wire.Json)!;
        Assert.True(stacks.Selected.Samples > 0); Assert.Equal(sample.ThreadId, stacks.Query.ThreadId);
        await worker.Send("hot", "cpuStacks", query with { View = CpuStackView.CallTree, HottestPath = true });
        var hot = (await worker.Result("hot")).GetProperty("result").Deserialize<CpuStackPage>(Wire.Json)!;
        Assert.Equal(stacks.Selected, hot.Selected); Assert.NotEmpty(hot.HotPath!.Keys);
        await worker.Send("hot-evidence", "evidence", new { index = hot.HotPath.Evidence!.EtlxEventIndex });
        var evidence = (await worker.Result("hot-evidence")).GetProperty("result").Deserialize<EvidenceDetail>(Wire.Json)!;
        Assert.Equal(sample.ThreadId, evidence.Observation.ThreadId); Assert.Equal(sample.ProcessId, evidence.Observation.ProcessId);
        await worker.Send("bad-thread", "cpuStacks", query with { ThreadId = "missing" });
        Assert.Equal("EntityMissing", (await worker.Result("bad-thread")).GetProperty("code").GetString());
        await worker.Send("review", "exportReport", new IntervalQuery(0, end));
        var prepared = (await worker.Result("review")).GetProperty("result").Deserialize<Report>(Wire.Json)!;
        var reviewed = Path.Combine(worker.Cache, "reviewed.json");
        await worker.Send("safe-export", "export", new { path = reviewed, format = "json", interval = new IntervalQuery(0, end) });
        Assert.Equal("result", (await worker.Result("safe-export")).GetProperty("kind").GetString());
        Assert.Equal(ReportExport.Render(prepared, "json"), File.ReadAllText(reviewed));
        Assert.DoesNotContain(Fixture, File.ReadAllText(reviewed));
        var privacy = new ExportPrivacyOptions(true, true, true, true, true, true);
        await worker.Send("full-export", "export", new { path = reviewed, format = "json", interval = new IntervalQuery(0, end), privacy });
        Assert.Equal("result", (await worker.Result("full-export")).GetProperty("kind").GetString());
        Assert.Equal(ReportExport.Render(prepared, "json", privacy), File.ReadAllText(reviewed));
    }
    [Fact]
    public async Task MalformedMonitorMethodDoesNotCrashAndStopExitsCleanly()
    {
        using var worker = new Harness(monitor: true);
        await worker.Send("missing", "", new {});
        Assert.Equal("ProtocolError", (await worker.Result("missing")).GetProperty("code").GetString());
        await worker.Send("sql", "sql", new { query = "SELECT * FROM document" });
        Assert.Equal("UnknownMethod", (await worker.Result("sql")).GetProperty("code").GetString());
        await Task.Delay(1100);
        await worker.Send("status", "monitor.status", new {});
        var status = (await worker.Result("status")).GetProperty("result");
        Assert.True(status.GetProperty("snapshotCount").GetInt32() >= 1);
        Assert.Equal("WindowsApiSnapshot", status.GetProperty("descriptor").GetProperty("kind").GetString());
        await worker.Send("stop", "monitor.stop", new {});
        Assert.True((await worker.Result("stop")).GetProperty("result").GetProperty("stopped").GetBoolean());
        await worker.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, worker.Process.ExitCode);
    }
    private sealed class Harness:IDisposable
    {
        public Process Process{get;}
        public string Cache{get;}=Path.Combine(Path.GetTempPath(),"woi-rpc-"+Guid.NewGuid().ToString("N"));
        public Harness(bool monitor = false) {
            Directory.CreateDirectory(Cache);
            var sdk=new[]{Environment.GetEnvironmentVariable("DOTNET_HOST_PATH"),
                Path.Combine(Root,".tools","dotnet","dotnet.exe"),
                Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT")??"", "dotnet.exe")}
                .FirstOrDefault(path=>!string.IsNullOrEmpty(path) && File.Exists(path))??"dotnet";
            var configuration=AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar+"Release"+Path.DirectorySeparatorChar)?"Release":"Debug";
            var dll=Path.Combine(Root,"src","Woi.Worker","bin",configuration,"net10.0-windows","Woi.Worker.dll");
            var start=new ProcessStartInfo(File.Exists(sdk)?sdk:"dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add(dll);
            if (monitor) {
                start.ArgumentList.Add("--monitor"); start.ArgumentList.Add("--parent"); start.ArgumentList.Add(Environment.ProcessId.ToString());
                start.ArgumentList.Add("--parent-start"); start.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString());
            } else { start.ArgumentList.Add("--cache-root"); start.ArgumentList.Add(Cache); }
            Process=Process.Start(start)!;_=Process.StandardError.ReadToEndAsync();
        }
        public async Task Send(string id,string method,object? parameters,int version=1) {
            await Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new{id,version,method,parameters},Wire.Json));
            await Process.StandardInput.FlushAsync();
        }
        public async Task<JsonElement> Read() {
            var line=await Process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.NotNull(line);using var doc=JsonDocument.Parse(line!);return doc.RootElement.Clone();
        }
        public async Task<JsonElement> Result(string id) {
            while(true){var response=await Read();if(response.GetProperty("id").GetString()==id && response.GetProperty("kind").GetString()!="progress")return response;}
        }
        public void Dispose() {
            if(!Process.HasExited)Process.Kill(true);Process.WaitForExit();Process.Dispose();Directory.Delete(Cache,true);
        }
    }
}
