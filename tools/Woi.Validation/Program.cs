using System.Diagnostics;
using System.Text.Json;
using Woi.Core;
using Woi.Trace;
using Woi.Capture;

if(args.Length==2 && args[0]=="--aggregate-stress")return Woi.Validation.AggregateStress.Run(args[1]);
if(args.Length==2 && args[0]=="--profile"){
    File.WriteAllText(Path.GetFullPath(args[1]),CaptureProfiles.Xml());return 0;
}
if(args.Length<4 || args[0]!="--capture"){
    Console.Error.WriteLine("Usage: Woi.Validation --capture output-directory helper.exe workload.exe");return 2;
}
var output=Path.GetFullPath(args[1]);Directory.CreateDirectory(output);
try {
    using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(4));
    await using var client=await CaptureClient.Connect(Path.GetFullPath(args[2]),deadline.Token);
    var recording=await client.Send("start",new CaptureOptions("FileActivity",45),deadline.Token);
    var workloadDirectory=Path.Combine(output,"workload");Directory.CreateDirectory(workloadDirectory);
    var start=new ProcessStartInfo(Path.GetFullPath(args[3])){UseShellExecute=false,CreateNoWindow=true,
        RedirectStandardOutput=true,RedirectStandardError=true};
    start.ArgumentList.Add(workloadDirectory);
    using var workload=Process.Start(start)!;
    var stdout=workload.StandardOutput.ReadToEndAsync();var stderr=workload.StandardError.ReadToEndAsync();
    while(!workload.HasExited) {
        await Task.Delay(1000,deadline.Token);
        recording=await client.Send("status",new{},deadline.Token);
        Console.WriteLine($"{recording.Phase}: {recording.ElapsedSeconds:F0} seconds");
        if(recording.Phase is CapturePhase.Failed or CapturePhase.Cancelled)throw new Exception(recording.Message);
    }
    await workload.WaitForExitAsync(deadline.Token);
    if(workload.ExitCode!=0)throw new Exception("Controlled workload failed: "+await stderr);
    recording=await client.Send("stop",new{},deadline.Token);
    if(recording.Phase!=CapturePhase.Saved || recording.TracePath==null)throw new Exception("No saved recording.");
    var source=Path.Combine(output,"controlled.etl");File.Copy(recording.TracePath,source,false);
    File.Copy(Path.Combine(Path.GetDirectoryName(recording.TracePath)!,"capture.json"),Path.Combine(output,"capture.json"));
    var session=EtlImporter.Import(source,Path.Combine(output,"cache"),null,deadline.Token);
    using var reader=new EtlReader(session);using var store=new CacheStore(session.CacheDirectory);
    var engine=new QueryEngine(reader,store);
    using var metadata=JsonDocument.Parse(File.ReadAllText(Path.Combine(workloadDirectory,"workload.json")));
    int pid=metadata.RootElement.GetProperty("pid").GetInt32();
    var cpuPhase=metadata.RootElement.GetProperty("phases").EnumerateArray().First(x=>x.GetProperty("name").GetString()=="CPU burst");
    double cpuStart=(cpuPhase.GetProperty("startUtc").GetDateTime()-session.StartUtc).TotalMilliseconds;
    double cpuEnd=(cpuPhase.GetProperty("endUtc").GetDateTime()-session.StartUtc).TotalMilliseconds;
    var contributors=engine.Contributors(new(Math.Max(0,cpuStart),Math.Min(cpuEnd,session.DurationMs)),deadline.Token);
    var lifetime=engine.Summary().Processes.FirstOrDefault(x=>x.OsId==pid && x.StartMs<=cpuEnd && x.EndMs>cpuStart);
    var workloadEvents=reader.ReadObservations(new(0,session.DurationMs,lifetime?.Id),deadline.Token).ToArray();
    var quality=new {
        sourceSha256=session.SourceHash,indexedEvents=session.IndexedEvents,reportedLoss=session.ReportedLostEvents,
        controlledPid=pid,lifetimeResolved=lifetime!=null,cpuContributorIsWorkload=contributors.FirstOrDefault()?.Pid==pid,
        logicalFileBytes=workloadEvents.Where(x=>x.Category=="File").Sum(x=>x.Bytes??0),
        networkTransferBytes=workloadEvents.Where(x=>x.Category=="Network" && (x.Operation.Contains("Send")||x.Operation.Contains("Recv"))).Sum(x=>x.Bytes??0),
        maximumCapturedWorkingSet=workloadEvents.Select(x=>x.WorkingSetBytes??0).DefaultIfEmpty().Max(),
        capabilities=session.Capabilities,
        recording,contributorRanking=contributors.Take(10)
    };
    File.WriteAllText(Path.Combine(output,"end-to-end.json"),JsonSerializer.Serialize(quality,new JsonSerializerOptions(Wire.Json){WriteIndented=true}));
    engine.Export(Path.Combine(output,"incident.md"),"markdown",new(0,session.DurationMs),deadline.Token);
    engine.Export(Path.Combine(output,"incident.json"),"json",new(0,session.DurationMs),deadline.Token);
    var page=reader.QueryEvents(new(0,session.DurationMs,ProcessId:lifetime?.Id,Category:"CPU"),deadline.Token);
    if(page.Events.Count==0)throw new Exception("Controlled workload CPU evidence is missing.");
    var evidence=reader.GetEvidence(page.Events[0].Index,null,deadline.Token);
    if(evidence.Observation.ProcessId!=lifetime?.Id)throw new Exception("Evidence lifetime attribution did not match.");
    if(!quality.lifetimeResolved || !quality.cpuContributorIsWorkload || quality.logicalFileBytes<128L*1024*1024 ||
        quality.networkTransferBytes<16L*1024*1024 || quality.maximumCapturedWorkingSet<128L*1024*1024)
        throw new Exception("Controlled recording did not meet expected attribution or coverage. See end-to-end.json.");
    Console.WriteLine("Capture, import, contributor ranking, evidence, and report export passed.");
    return 0;
}catch(Exception ex){
    File.WriteAllText(Path.Combine(output,"failure.json"),JsonSerializer.Serialize(new{error=ex.ToString()},new JsonSerializerOptions(Wire.Json){WriteIndented=true}));
    Console.Error.WriteLine(ex.Message);return 1;
}
