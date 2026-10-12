using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Woi.Capture;
using Woi.Core;

if(args.Length!=4 || args[0]!="--pipe" || args[2]!="--parent" ||
    !Regex.IsMatch(args[1],@"^woi\.capture\.[0-9a-f]{32}$") || !int.TryParse(args[3],out int parentPid) || parentPid<=0)return 2;
using var parent=Process.GetProcessById(parentPid);
using var lifetime=new CancellationTokenSource();
using var pipe=new NamedPipeClientStream(".",args[1],PipeDirection.InOut,PipeOptions.Asynchronous);
try {
    await pipe.ConnectAsync(10000,lifetime.Token);
    PipeIdentity.RequirePeer(pipe,parentPid,false);
    using var reader=new StreamReader(pipe,new UTF8Encoding(false),false,4096,true);
    using var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true,NewLine="\n"};
    using var identity=WindowsIdentity.GetCurrent();
    if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) {
        await writer.WriteLineAsync(JsonSerializer.Serialize(new CaptureReply("ready",CaptureWire.Version,
            Error:"Administrator elevation is required for WPR capture.",Code:"ElevationRequired"),Wire.Json));return 1;
    }
    var runner=new WprRunner();
    using var storage=CaptureStorage.OpenDefault();
    int recovered=await CaptureRecovery.Recover(CaptureController.RecordingRoot,runner,lifetime.Token);
    await using var controller=new CaptureController(runner,CaptureController.RecordingRoot,parentPid,parent.StartTime.ToUniversalTime());
    await writer.WriteLineAsync(JsonSerializer.Serialize(new CaptureReply("ready",CaptureWire.Version,
        controller.Status with{Message=recovered>0?$"Recovered {recovered} interrupted recordings.":"Ready to record."}),Wire.Json));
    var parentWatch=Task.Run(async()=>{
        try{await parent.WaitForExitAsync(lifetime.Token);lifetime.Cancel();}catch(OperationCanceledException){}
    });
    var ticker=Task.Run(async()=>{
        try {
            while(!lifetime.IsCancellationRequested) {
                await Task.Delay(500,lifetime.Token);
                try{await controller.Tick(lifetime.Token);}catch(Exception ex) when(ex is not OperationCanceledException){} // Failure remains visible in controller state.
            }
        } catch(OperationCanceledException){}
    });
    using var idle=new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try {
        while(!lifetime.IsCancellationRequested) {
            using var readToken=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token,
                controller.Status.Phase==CapturePhase.Idle?idle.Token:CancellationToken.None);
            var line=await CaptureWire.ReadLine(reader,readToken.Token);if(line==null)break;
            CaptureReply reply;
            try {
                var request=JsonSerializer.Deserialize<CaptureRequest>(line,Wire.Json) ??
                    throw new WoiException("ProtocolError","Empty capture request.");
                reply=await CaptureWire.Dispatch(controller,request,lifetime.Token);
            } catch(JsonException){reply=new("",CaptureWire.Version,Error:"Malformed capture JSON.",Code:"ProtocolError");}
            await writer.WriteLineAsync(JsonSerializer.Serialize(reply,Wire.Json));
        }
    } finally {
        lifetime.Cancel();
        await ticker;await parentWatch;
    }
    return 0;
} catch(Exception) {return 1;}
