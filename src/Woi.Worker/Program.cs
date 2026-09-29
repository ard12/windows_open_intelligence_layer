using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Woi.Core;
using Woi.Trace;

if (args.Length > 0 && args[0] == "--monitor") return await MonitorHost.Run(args);
Console.InputEncoding = Encoding.UTF8; Console.OutputEncoding = new UTF8Encoding(false);
var cacheArg = Array.IndexOf(args, "--cache-root");
var cacheRoot = cacheArg >= 0 && args.Length > cacheArg+1 ? Path.GetFullPath(args[cacheArg+1]) :
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsOpenIntelligence", "cache");
if(args.Length >= 2 && args[0] == "--analyze") {
    try {
        var session = EtlImporter.Import(args[1], cacheRoot, null, CancellationToken.None);
        using var reader = new EtlReader(session); using var store = new CacheStore(session.CacheDirectory);
        var engine = new QueryEngine(reader, store);
        var interval = new IntervalQuery(0,session.DurationMs);
        var outputIndex = Array.IndexOf(args,"--output");
        if(outputIndex >= 0) engine.Export(Path.GetFullPath(args[outputIndex+1]), "json", interval, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(new { summary = engine.Summary(), contributors = engine.Contributors(interval,CancellationToken.None),
            metrics = engine.Metrics(interval) }, Wire.Json));
        return 0;
    } catch(Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
}

var running = new ConcurrentDictionary<string,CancellationTokenSource>();
var serial = new SemaphoreSlim(1);
var outputGate = new SemaphoreSlim(1);
ITraceReader? activeReader = null; CacheStore? activeStore = null; QueryEngine? engineState = null;
var tasks = new List<Task>();
async Task Reply(Response value) {
    await outputGate.WaitAsync();
    try { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value,Wire.Json)); await Console.Out.FlushAsync(); }
    finally { outputGate.Release(); }
}
T Parse<T>(JsonElement value) => value.Deserialize<T>(Wire.Json)!;
async Task Handle(Request request, CancellationTokenSource cancellation)
{
    bool entered = false;
    try {
        await serial.WaitAsync(cancellation.Token); entered = true;
        var token = cancellation.Token;
        object? result;
        if(request.Method == "import") {
            activeReader?.Dispose(); activeStore?.Dispose(); engineState = null;
            var path = request.Parameters.GetProperty("path").GetString()!;
            var progress = new InlineProgress(value => Reply(new(request.Id, Wire.Version,"progress",value)).GetAwaiter().GetResult());
            var session = EtlImporter.Import(path,cacheRoot,progress,token);
            activeReader = new EtlReader(session); activeStore = new CacheStore(session.CacheDirectory,readOnly:true);
            engineState = new(activeReader,activeStore); result = engineState.Summary();
        } else {
            var engine = engineState ?? throw new WoiException("NoTrace", "Open an ETL recording first.");
            result = request.Method switch {
                "summary" => engine.Summary(),
                "diagnostics" => new {pid=Environment.ProcessId,
                    peakWorkingSetBytes=System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64,
                    workingSetBytes=System.Diagnostics.Process.GetCurrentProcess().WorkingSet64},
                "capabilities" => engine.Session.Capabilities,
                "metrics" => engine.Metrics(Parse<IntervalQuery>(request.Parameters)),
                "contributors" => engine.Contributors(Parse<IntervalQuery>(request.Parameters),token),
                "events" => activeReader!.QueryEvents(Parse<EventQuery>(request.Parameters),token),
                "entity" => activeReader!.GetEntity(request.Parameters.GetProperty("id").GetString()!,token),
                "evidence" => activeReader!.GetEvidence(request.Parameters.GetProperty("index").GetInt64(),
                    request.Parameters.TryGetProperty("symbolDirectory",out var dir) && dir.ValueKind == JsonValueKind.String ? dir.GetString() : null,token),
                "settings" => Configure(engine,Parse<DetectionSettings>(request.Parameters)),
                "bookmarks" => activeStore!.Bookmarks(),
                "bookmark" => Bookmark(activeStore!,engine,Parse<Bookmark>(request.Parameters)),
                "export" => Export(engine, request.Parameters,token),
                _ => throw new WoiException("UnknownMethod","The requested operation is not supported.")
            };
        }
        await Reply(new(request.Id,Wire.Version,"result",result));
    } catch(OperationCanceledException) { await Reply(new(request.Id,Wire.Version,"error",Error:"Operation cancelled.",Code:"Cancelled")); }
    catch(WoiException ex) { await Reply(new(request.Id,Wire.Version,"error",Error:ex.Message,Code:ex.Code)); }
    catch(Exception ex) { await Reply(new(request.Id,Wire.Version,"error",Error:ex.Message,Code:"OperationFailed")); }
    finally {
        running.TryRemove(request.Id,out _); cancellation.Dispose();
        if(entered) serial.Release();
    }
}
try {
    var lines=new BoundedLines(Console.In,1024*1024);
    while(await lines.Read() is { } line) {
        if(line.Length > 1024*1024) { await Reply(new("",Wire.Version,"error",Error:"Request exceeds the 1 MiB limit.",Code:"ProtocolError")); continue; }
        Request? request;
        try { request = JsonSerializer.Deserialize<Request>(line,Wire.Json); }
        catch(JsonException) { await Reply(new("",Wire.Version,"error",Error:"Malformed JSON request.",Code:"ProtocolError")); continue; }
        if(request == null || request.Version != Wire.Version || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.Method) || request.Parameters.ValueKind != JsonValueKind.Object) {
            await Reply(new(request?.Id ?? "",Wire.Version,"error",Error:"Unsupported protocol version or missing request ID.",Code:"ProtocolError")); continue;
        }
        if(request.Method == "cancel") {
            if(request.Parameters.TryGetProperty("requestId",out var target) && target.ValueKind == JsonValueKind.String && running.TryGetValue(target.GetString() ?? "",out var active)) {
                try { active.Cancel(); } catch(ObjectDisposedException) { }
            }
            await Reply(new(request.Id,Wire.Version,"result",new { cancelled = true })); continue;
        }
        if(running.Count>=32){await Reply(new(request.Id,Wire.Version,"error",Error:"Too many pending requests.",Code:"WorkerBusy"));continue;}
        var cancellation = new CancellationTokenSource();
        if(!running.TryAdd(request.Id,cancellation)) { cancellation.Dispose(); await Reply(new(request.Id,Wire.Version,"error",Error:"Duplicate request ID.",Code:"ProtocolError")); continue; }
        tasks.RemoveAll(x=>x.IsCompleted);
        tasks.Add(Task.Run(()=>Handle(request,cancellation)));
    }
} finally {
    foreach(var pending in running.Values) { try { pending.Cancel(); } catch(ObjectDisposedException) { } }
    await Task.WhenAll(tasks);
    activeReader?.Dispose(); activeStore?.Dispose();
}
return 0;

static object Configure(QueryEngine engine, DetectionSettings settings) { engine.Configure(settings); return engine.Summary(); }
static object Bookmark(CacheStore store,QueryEngine engine,Bookmark bookmark) {
    var interval=engine.Clamp(new(bookmark.StartMs,bookmark.EndMs));
    bookmark=bookmark with {StartMs=interval.StartMs,EndMs=interval.EndMs};
    if(string.IsNullOrWhiteSpace(bookmark.Id) || bookmark.Label.Length > 200) throw new WoiException("InvalidBookmark","Provide a bookmark ID and a label of at most 200 characters.");
    store.AddBookmark(bookmark); return bookmark;
}
static object Export(QueryEngine engine,JsonElement parameters,CancellationToken token) {
    engine.Export(LocalPaths.RequireLocal(parameters.GetProperty("path").GetString()!),
        parameters.GetProperty("format").GetString()!, parameters.GetProperty("interval").Deserialize<IntervalQuery>(Wire.Json)!,token);
    return new { exported = true };
}
sealed class InlineProgress(Action<ImportProgress> action) : IProgress<ImportProgress>
{
    public void Report(ImportProgress value) => action(value);
}

sealed class BoundedLines(TextReader reader,int maximum)
{
    private readonly char[] buffer=new char[4096];
    private int offset,count;
    public async Task<string?> Read(CancellationToken token = default)
    {
        var line=new StringBuilder();bool oversized=false,seen=false;
        while(true) {
            if(offset==count){count=await reader.ReadAsync(buffer.AsMemory(), token);offset=0;if(count==0)return seen?Finish():null;}
            char next=buffer[offset++];seen=true;
            if(next=='\n')return Finish();
            if(line.Length<maximum)line.Append(next);else oversized=true;
        }
        string Finish()=>oversized?new string(' ',maximum+1):line.ToString().TrimEnd('\r');
    }
}
