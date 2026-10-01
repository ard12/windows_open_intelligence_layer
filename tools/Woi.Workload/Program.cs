using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

var fixedWork = args.Length == 2 && args[0] == "--fixed-work";
if(args.Length!=1 && !fixedWork){Console.Error.WriteLine("Usage: Woi.Workload [--fixed-work] output-directory");return 2;}
var directory=Path.GetFullPath(args[fixedWork ? 1 : 0]);Directory.CreateDirectory(directory);
var clock=Stopwatch.StartNew();List<object> phases=[];
var cpuWorkers=Math.Max(1,Environment.ProcessorCount-1);
var cpuResults=new double[cpuWorkers];
async Task Phase(string name,Func<Task> run)
{
    var start=clock.Elapsed.TotalMilliseconds;var utc=DateTime.UtcNow;
    await run();phases.Add(new{name,startMs=start,endMs=clock.Elapsed.TotalMilliseconds,startUtc=utc,endUtc=DateTime.UtcNow});
}
await Phase("idle before incident",()=>Task.Delay(fixedWork ? 0 : 4000));
await Phase("CPU burst",()=>Task.Run(()=>Parallel.For(0,cpuWorkers,worker=>{
    var timer=Stopwatch.StartNew();double result=worker+1;
    if(fixedWork) { for(int i=0;i<500_000_000;i++) result=Math.Sqrt(result+13.123); }
    else while(timer.Elapsed.TotalSeconds<6)for(int i=0;i<100000;i++)result=Math.Sqrt(result+13.123);
    cpuResults[worker]=result;
})));
await Phase("logical file writes and reads",async()=>{
    var path=Path.Combine(directory,"controlled-io.bin");byte[] buffer=new byte[1024*1024];Random.Shared.NextBytes(buffer);
    await using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,buffer.Length,FileOptions.WriteThrough|FileOptions.Asynchronous)){
        for(int i=0;i<64;i++)await file.WriteAsync(buffer);
        file.Flush(true);file.Position=0;while(await file.ReadAsync(buffer)>0){}
    }
});
await Phase("loopback TCP transfer",async()=>{
    var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
    try {
        var receive=Task.Run(async()=>{using var connection=await listener.AcceptTcpClientAsync();byte[] data=new byte[65536];long total=0;
            while(total<8*1024*1024){int count=await connection.GetStream().ReadAsync(data);if(count==0)throw new IOException("Incomplete loopback transfer.");total+=count;}});
        using var sender=new TcpClient();await sender.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        byte[] payload=new byte[65536];for(int i=0;i<128;i++)await sender.GetStream().WriteAsync(payload);
        sender.Close();await receive;
    }finally{listener.Stop();}
});
await Phase("memory allocation and resident hold",async()=>{
    var memory=new byte[128*1024*1024];for(int i=0;i<memory.Length;i+=4096)memory[i]=1;
    await Task.Delay(fixedWork ? 200 : 5000);GC.KeepAlive(memory);
});
await Phase("process churn",async()=>{
    for(int i=0;i<12;i++){
        using var process=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"cmd.exe"),"/d /c exit 0"){UseShellExecute=false,CreateNoWindow=true})!;
        await process.WaitForExitAsync();
    }
});
await Phase("idle after incident",()=>Task.Delay(fixedWork ? 0 : 4000));
File.WriteAllText(Path.Combine(directory,"workload.json"),JsonSerializer.Serialize(new{
    schema=1,fixedWork,cpuIterationsPerWorker=fixedWork ? 500_000_000 : (int?)null,cpuWorkers,cpuResultChecksum=cpuResults.Sum(),pid=Environment.ProcessId,logicalProcessors=Environment.ProcessorCount,completedUtc=DateTime.UtcNow,
    durationMs=clock.Elapsed.TotalMilliseconds,phases,
    expected=new{fileWriteBytes=64*1024*1024,fileReadBytes=64*1024*1024,loopbackPayloadBytes=8*1024*1024,memoryAllocationBytes=128*1024*1024,childProcesses=12}
},new JsonSerializerOptions{WriteIndented=true}));
return 0;
