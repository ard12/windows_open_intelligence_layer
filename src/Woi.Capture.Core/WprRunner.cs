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
        var start=new ProcessStartInfo(Executable){UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        using var process=Process.Start(start)??throw new IOException("Windows Performance Recorder could not start.");
        var stdout=ReadBounded(process.StandardOutput);var stderr=ReadBounded(process.StandardError);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);deadline.CancelAfter(TimeSpan.FromSeconds(60));
        try {await process.WaitForExitAsync(deadline.Token);}
        catch(OperationCanceledException) {
            if(!process.HasExited)process.Kill(true);
            await process.WaitForExitAsync();
            if(!token.IsCancellationRequested)throw new WoiException("CaptureTimeout","Windows Performance Recorder exceeded its command timeout.");
            throw;
        }
        return new(process.ExitCode,(await stdout)+"\n"+(await stderr));
    }
    private static async Task<string> ReadBounded(TextReader reader)
    {
        StringBuilder result=new();char[] buffer=new char[4096];
        while(true){int count=await reader.ReadAsync(buffer);if(count==0)break;
            if(result.Length<32768)result.Append(buffer,0,Math.Min(count,32768-result.Length));}
        return result.ToString();
    }
}
