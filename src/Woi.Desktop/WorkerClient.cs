using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Woi.Core;

namespace Woi.Desktop;

public sealed class WorkerClient : IDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim inputGate = new(1);
    private readonly ConcurrentDictionary<string,TaskCompletionSource<JsonElement>> pending = new();
    private volatile bool disconnected;
    internal int ProcessId=>process.Id;
    public bool IsAlive => !disconnected && !process.HasExited;
    public event Action<ImportProgress>? Progress;
    public WorkerClient(bool monitor = false)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Woi.Worker.exe");
        if(!File.Exists(executable)) {
            executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../Woi.Worker/bin/Debug/net10.0-windows/Woi.Worker.exe"));
            if(!File.Exists(executable)) executable = executable.Replace(Path.DirectorySeparatorChar+"Debug"+Path.DirectorySeparatorChar,Path.DirectorySeparatorChar+"Release"+Path.DirectorySeparatorChar);
        }
        if(!File.Exists(executable)) throw new FileNotFoundException("The importer worker is missing. Build the solution or use the packaged application.",executable);
        process = new Process { StartInfo = new(executable) {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        }};
        if (monitor) {
            process.StartInfo.ArgumentList.Add("--monitor");
            process.StartInfo.ArgumentList.Add("--parent");
            process.StartInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            process.StartInfo.ArgumentList.Add("--parent-start");
            process.StartInfo.ArgumentList.Add(Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks.ToString());
        }
        process.Start(); _ = ReadResponses();
        _ = process.StandardError.ReadToEndAsync(); // Drain stderr so a library warning cannot deadlock the protocol.
    }
    private async Task ReadResponses()
    {
        try {
            while(await process.StandardOutput.ReadLineAsync() is { } line) {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var id = root.GetProperty("id").GetString()!;
                if(root.GetProperty("kind").GetString() == "progress") {
                    Progress?.Invoke(root.GetProperty("result").Deserialize<ImportProgress>(Wire.Json)!); continue;
                }
                if(!pending.TryRemove(id,out var completion)) continue;
                if(root.GetProperty("kind").GetString() == "error") {
                    completion.TrySetException(new WoiException(root.GetProperty("code").GetString() ?? "WorkerError",
                        root.GetProperty("error").GetString() ?? "Worker operation failed."));
                } else completion.TrySetResult(root.GetProperty("result").Clone());
            }
        } catch(Exception ex) { FailPending(ex); }
        finally { disconnected=true; FailPending(new IOException("The analysis worker exited. Open the recording again to restart it.")); }
    }
    private void FailPending(Exception error)
    {
        foreach(var item in pending) if(pending.TryRemove(item.Key,out var completion)) completion.TrySetException(error);
    }
    public async Task<T> Send<T>(string method,object parameters,CancellationToken token = default)
    {
        if(!IsAlive)throw new IOException("The analysis worker exited. Open the recording again to restart it.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        try {
            await Write(new Request(id,Wire.Version,method,JsonSerializer.SerializeToElement(parameters,Wire.Json)));
            using var registration = token.Register(()=>_ = Cancel(id));
            return (await completion.Task.WaitAsync(token)).Deserialize<T>(Wire.Json)!;
        } finally { pending.TryRemove(id,out _); }
    }
    private async Task Cancel(string id)
    {
        try { await Write(new Request(Guid.NewGuid().ToString("N"),Wire.Version,"cancel",
            JsonSerializer.SerializeToElement(new { requestId = id }, Wire.Json))); } catch(IOException) { }
    }
    private async Task Write(Request value)
    {
        await inputGate.WaitAsync();
        try { await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value,Wire.Json)); await process.StandardInput.FlushAsync(); }
        finally { inputGate.Release(); }
    }
    public void Dispose() {
        disconnected=true; FailPending(new IOException("The analysis worker was closed."));
        try { process.StandardInput.Close(); if(!process.WaitForExit(1000)) process.Kill(true); } catch(InvalidOperationException) { }
        process.Dispose();
    }
}
