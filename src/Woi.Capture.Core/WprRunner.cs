using System.Diagnostics;
using System.Text;
using Woi.Core;

namespace Woi.Capture;
public sealed record WprResult(int ExitCode,string Output);
public interface IWprRunner
{
    Task<WprResult> Run(IReadOnlyList<string> arguments,CancellationToken token);
}
public sealed class WprRunner : IWprRunner
{
    public static string Executable=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"wpr.exe");
    public async Task<WprResult> Run(IReadOnlyList<string> arguments,CancellationToken token)
    {
        if(!File.Exists(Executable))throw new WoiException("WprMissing","Windows Performance Recorder is unavailable on this computer.");
        using var storage=CaptureStorage.OpenDefault();
        var start=new ProcessStartInfo(Executable){UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true};
        start.Environment["TEMP"]=storage.Root;
        start.Environment["TMP"]=storage.Root;
        start.Environment["SystemTemp"]=storage.Root;
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        return await RunProcess(start,arguments[0],TimeSpan.FromSeconds(60),token);
    }
    internal static async Task<WprResult> RunProcess(ProcessStartInfo start,string operation,TimeSpan timeout,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var process=Process.Start(start)??throw new IOException("Windows Performance Recorder could not start.");
        using var drain=new CancellationTokenSource();
        var stdout=ReadBounded(process.StandardOutput,drain.Token);
        var stderr=ReadBounded(process.StandardError,drain.Token);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(timeout);
        var interrupted=false;
        string? cleanupError=null;
        try {await process.WaitForExitAsync(deadline.Token);}
        catch(OperationCanceledException) {
            interrupted=true;
            try {
                if(!process.HasExited)process.Kill(true);
                using var reap=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(reap.Token);
            } catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException) {
                cleanupError=ex.Message;
            }
        }
        // A descendant may inherit a redirected handle after the owned process exits.
        drain.CancelAfter(TimeSpan.FromSeconds(2));
        var output="stdout:\n"+await stdout+"\nstderr:\n"+await stderr;
        if(cleanupError!=null)throw new WoiException("CaptureCleanupFailed",$"Windows Performance Recorder {operation} could not be reaped: {cleanupError}\n{output}");
        if(interrupted) {
            var message=$"Windows Performance Recorder {operation} "+(token.IsCancellationRequested?"was cancelled.":"exceeded its command timeout.")+"\n"+output;
            if(token.IsCancellationRequested)throw new OperationCanceledException(message,token);
            throw new WoiException("CaptureTimeout",message);
        }
        return new(process.ExitCode,output);
    }
    private static async Task<string> ReadBounded(TextReader reader,CancellationToken token)
    {
        StringBuilder result=new();char[] buffer=new char[4096];var truncated=false;var incomplete=false;
        try {
            while(true){int count=await reader.ReadAsync(buffer.AsMemory(),token);if(count==0)break;
                // Both streams must fit the 64 KiB reply even when JSON escapes every character.
                int retained=Math.Min(count,4096-result.Length);result.Append(buffer,0,retained);truncated|=retained<count;}
        } catch(OperationCanceledException) when(token.IsCancellationRequested) {incomplete=true;}
        return result+(truncated?"\n[output truncated]":"")+(incomplete?"\n[output drain incomplete]":"");
    }
}
