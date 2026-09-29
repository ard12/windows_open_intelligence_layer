using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Woi.Core;
namespace Woi.Capture;
public sealed class CaptureClient : IAsyncDisposable
{
    private readonly NamedPipeServerStream pipe;
    private readonly Process helper;
    private readonly StreamReader reader;
    private readonly StreamWriter writer;
    private readonly SemaphoreSlim gate=new(1);
    private bool disposed;
    private CaptureClient(NamedPipeServerStream pipe,Process helper)
    {
        this.pipe=pipe;this.helper=helper;
        reader=new(pipe,new UTF8Encoding(false),false,4096,true);
        writer=new(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true,NewLine="\n"};
    }
    public static string ResolveHelper()
    {
        var path=Path.Combine(AppContext.BaseDirectory,"Woi.Capture.exe");
        if(!File.Exists(path)) {
            var configuration=AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar+"Debug"+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)?"Debug":"Release";
            path=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,$"../../../../Woi.Capture/bin/{configuration}/net10.0-windows/Woi.Capture.exe"));
        }
        if(!File.Exists(path))throw new WoiException("CaptureHelperMissing","The recording helper is missing. Use the complete release ZIP or build the solution.");
        return path;
    }
    public static async Task<CaptureClient> Connect(string executable,CancellationToken token)
    {
        var name="woi.capture."+Guid.NewGuid().ToString("N");
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
        var sid=WindowsIdentity.GetCurrent().User!;
        security.SetOwner(sid);
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid,null),PipeAccessRights.FullControl,AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(sid,PipeAccessRights.FullControl,AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
        var pipe=NamedPipeServerStreamAcl.Create(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,4096,4096,security);
        Process? helper=null;CaptureClient? client=null;
        try {
            var start=new ProcessStartInfo(Path.GetFullPath(executable)){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
            start.ArgumentList.Add("--pipe");start.ArgumentList.Add(name);
            start.ArgumentList.Add("--parent");start.ArgumentList.Add(Environment.ProcessId.ToString());
            var launch=Task.Run(()=>Process.Start(start));
            try {helper=await launch.WaitAsync(token);}
            catch { _=launch.ContinueWith(x=>{if(x.Status==TaskStatus.RanToCompletion)x.Result?.Dispose();},TaskScheduler.Default);throw;}
            if(helper==null)throw new WoiException("ElevationRequired","Windows could not launch the recording helper.");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var connection=pipe.WaitForConnectionAsync(deadline.Token);
            var exited=helper.WaitForExitAsync(deadline.Token);
            if(await Task.WhenAny(connection,exited)!=connection)
                throw new WoiException("CaptureHelperExited","The helper could not connect. Use administrator elevation for the same Windows account.");
            await connection;PipeIdentity.RequirePeer(pipe,helper.Id,true);
            client=new(pipe,helper);
            var greeting=await CaptureWire.ReadLine(client.reader,deadline.Token);
            var hello=JsonSerializer.Deserialize<CaptureReply>(greeting??"",Wire.Json);
            if(hello?.Version!=CaptureWire.Version || hello.Id!="ready" || hello.Error!=null)
                throw new WoiException(hello?.Code??"ProtocolError",hello?.Error??"Invalid recording helper response.");
            return client;
        } catch(Win32Exception ex) when(ex.NativeErrorCode==1223) {
            pipe.Dispose();helper?.Dispose();throw new WoiException("ElevationCancelled","Windows administrator elevation was cancelled. You can still import an existing ETL.");
        } catch { if(client!=null)await client.DisposeAsync();else{pipe.Dispose();helper?.Dispose();}throw;}
    }
    public async Task<CaptureStatus> Send(string method,object parameters,CancellationToken token=default)
    {
        await gate.WaitAsync(token);
        try {
            ObjectDisposedException.ThrowIf(disposed,this);
            var id=Guid.NewGuid().ToString("N");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(75));
            await writer.WriteLineAsync(JsonSerializer.Serialize(new CaptureRequest(id,CaptureWire.Version,method,
                JsonSerializer.SerializeToElement(parameters,Wire.Json)),Wire.Json).AsMemory(),deadline.Token);
            var line=await CaptureWire.ReadLine(reader,deadline.Token);
            if(line==null)throw new WoiException("CaptureHelperExited","The recording helper disconnected. Reopen recording controls to recover the owned session.");
            var reply=JsonSerializer.Deserialize<CaptureReply>(line,Wire.Json);
            if(reply?.Id!=id || reply.Version!=CaptureWire.Version)throw new WoiException("ProtocolError","Capture response identity did not match.");
            if(reply.Error!=null)throw new WoiException(reply.Code??"CaptureFailed",reply.Error);
            return reply.Status??throw new WoiException("ProtocolError","Capture response contained no state.");
        } finally {gate.Release();}
    }
    public ValueTask DisposeAsync()
    {
        if(disposed)return ValueTask.CompletedTask;disposed=true;
        // Closing IPC lets the elevated helper cancel only its own session in its finally block.
        pipe.Dispose();helper.Dispose();
        return ValueTask.CompletedTask;
    }
}
