using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Woi.Capture;
using Woi.Core;
using Xunit;

namespace Woi.Tests;
[CollectionDefinition("Native command tests",DisableParallelization=true)]
public sealed class NativeCommandCollection { }
[Collection("Native command tests")]
public sealed class WprRunnerTests
{
    private static ProcessStartInfo Child(string script)
    {
        var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell","v1.0","powershell.exe")) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
        };
        foreach(var argument in new[]{"-NoProfile","-NonInteractive","-EncodedCommand",Convert.ToBase64String(Encoding.Unicode.GetBytes(script))})start.ArgumentList.Add(argument);
        return start;
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task InterruptedCommandRetainsBoundedStreamsAndReapsItsOwnedProcess(bool cancelled)
    {
        using var cancellation=new CancellationTokenSource();
        var ready=Path.Combine(Path.GetTempPath(),"woi-wpr-ready-"+Guid.NewGuid().ToString("N"));
        var start=Child("[Console]::Out.WriteLine($PID); [Console]::Out.Write('OUT-'+(([string][char]1)*40000)); [Console]::Out.Flush(); [Console]::Error.Write('ERR-'+(([string][char]2)*40000)); [Console]::Error.Flush(); "+(cancelled?"[IO.File]::WriteAllText('"+ready.Replace("'","''")+"','ready'); ":"")+"Start-Sleep -Seconds 30");
        var task=WprRunner.RunProcess(start,"-stop",TimeSpan.FromSeconds(cancelled?30:8),cancellation.Token);
        if(cancelled) {
            try {
                using var readiness=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while(!File.Exists(ready))await Task.Delay(20,readiness.Token);
            } finally {
                cancellation.Cancel();
                try {await task.WaitAsync(TimeSpan.FromSeconds(8));}catch(OperationCanceledException) { }
                File.Delete(ready);
            }
        }
        Exception error=cancelled?
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task.WaitAsync(TimeSpan.FromSeconds(15))):
            await Assert.ThrowsAsync<WoiException>(()=>task.WaitAsync(TimeSpan.FromSeconds(15)));
        if(error is WoiException typed)Assert.Equal("CaptureTimeout",typed.Code);
        else Assert.Equal(cancellation.Token,((OperationCanceledException)error).CancellationToken);
        Assert.Contains("-stop",error.Message);Assert.Contains("OUT-",error.Message);Assert.Contains("ERR-",error.Message);
        var output=error.Message.Split("stdout:\n",2)[1];
        var streams=output.Split("\nstderr:\n",2);
        Assert.Equal(4096+"\n[output truncated]".Length,streams[0].Length);Assert.Equal(4096+"\n[output truncated]".Length,streams[1].Length);
        Assert.True(JsonSerializer.Serialize(new CaptureReply("bounded",CaptureWire.Version,Error:error.Message,Code:"CaptureTimeout"),Wire.Json).Length<CaptureWire.MaximumCharacters);
        var pid=int.Parse(streams[0].Split('\n')[0].Trim());
        Assert.False(IsRunning(pid));
    }
    [Fact]
    public async Task CompletedCommandReturnsExitCodeAndBothStreams()
    {
        var result=await WprRunner.RunProcess(Child("[Console]::Out.Write('native stop'); [Console]::Error.Write('native merge'); exit 7"),"-stop",TimeSpan.FromSeconds(5),default).WaitAsync(TimeSpan.FromSeconds(12));
        Assert.Equal(7,result.ExitCode);Assert.Equal("stdout:\nnative stop\nstderr:\nnative merge",result.Output);
    }
    [Fact]
    public async Task InheritedPipeHandlesCannotHoldTheRunnerAfterItsOwnedProcessExits()
    {
        var identity=Path.Combine(Path.GetTempPath(),"woi-wpr-grandchild-"+Guid.NewGuid().ToString("N"));
        var quoted=identity.Replace("'","''");
        var script="[IO.File]::WriteAllText('"+quoted+"',($PID.ToString()+'|'+[Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks)); [Console]::Out.Write('grandchild-out'); [Console]::Out.Flush(); [Console]::Error.Write('grandchild-err'); [Console]::Error.Flush(); Start-Sleep -Seconds 30";
        var encoded=Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var parent=Child("$child=Start-Process -FilePath '"+Child("").FileName.Replace("'","''")+"' -ArgumentList '-NoProfile','-NonInteractive','-EncodedCommand','"+encoded+"' -NoNewWindow -PassThru; $until=[DateTime]::UtcNow.AddSeconds(10); while(!(Test-Path -LiteralPath '"+quoted+"')){if([DateTime]::UtcNow -gt $until){exit 8}; Start-Sleep -Milliseconds 20}; [Console]::Out.Write('parent-out'); exit 0");
        try {
            var elapsed=Stopwatch.StartNew();
            var result=await WprRunner.RunProcess(parent,"-stop",TimeSpan.FromSeconds(15),default).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(0,result.ExitCode);
            Assert.Contains("parent-out",result.Output);Assert.Contains("grandchild-out",result.Output);Assert.Contains("grandchild-err",result.Output);
            Assert.Contains("[output drain incomplete]",result.Output);
            Assert.True(elapsed.Elapsed<TimeSpan.FromSeconds(15));
        } finally {
            if(File.Exists(identity)) {
                var parts=File.ReadAllText(identity).Split('|');
                try {
                    using var child=Process.GetProcessById(int.Parse(parts[0]));
                    if(child.StartTime.ToUniversalTime().Ticks==long.Parse(parts[1]) && !child.HasExited) {
                        child.Kill();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    }
                }catch(ArgumentException) { }
                File.Delete(identity);
            }
        }
    }
    private static bool IsRunning(int pid)
    {
        try {using var process=Process.GetProcessById(pid);return !process.HasExited;}
        catch(ArgumentException){return false;}
    }
}
