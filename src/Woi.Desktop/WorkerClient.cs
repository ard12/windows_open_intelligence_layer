using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Woi.Core;

namespace Woi.Desktop;

internal record ReplyDecodeDiagnostics(long Replies, long AllocatedBytes, double LastMilliseconds, double MaximumMilliseconds);

public sealed class WorkerClient : IDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim inputGate = new(1);
    private readonly ConcurrentDictionary<string, IPending> pending = new();
    private volatile bool disconnected;
    private ReplyDecodeDiagnostics diagnostics = new(0, 0, 0, 0);
    internal int ProcessId => process.Id;
    internal ReplyDecodeDiagnostics Diagnostics => Volatile.Read(ref diagnostics);
    public bool IsAlive => !disconnected && !process.HasExited;
    public event Action<ImportProgress>? Progress;

    public WorkerClient(bool monitor = false) : this(CreateStartInfo(monitor)) { }
    internal WorkerClient(ProcessStartInfo start)
    {
        process = new Process { StartInfo = start };
        process.Start();
        _ = Task.Run(ReadResponses); // Decode away from the WPF synchronization context.
        _ = process.StandardError.ReadToEndAsync();
    }
    private static ProcessStartInfo CreateStartInfo(bool monitor)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "Woi.Worker.exe");
        if (!File.Exists(executable)) {
            executable = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Woi.Worker/bin/Debug/net10.0-windows/Woi.Worker.exe"));
            if (!File.Exists(executable)) executable = executable.Replace(Path.DirectorySeparatorChar + "Debug" + Path.DirectorySeparatorChar,
                Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar);
        }
        if (!File.Exists(executable)) throw new FileNotFoundException("The importer worker is missing. Build the solution or use the packaged application.", executable);
        var start = new ProcessStartInfo(executable) {
            UseShellExecute=false, RedirectStandardInput=true, RedirectStandardOutput=true,
            RedirectStandardError=true, CreateNoWindow=true
        };
        if (monitor) {
            start.ArgumentList.Add("--monitor");
            start.ArgumentList.Add("--parent");
            start.ArgumentList.Add(Environment.ProcessId.ToString());
            start.ArgumentList.Add("--parent-start");
            using var parent = Process.GetCurrentProcess();
            start.ArgumentList.Add(parent.StartTime.ToUniversalTime().Ticks.ToString());
        }
        return start;
    }
    private interface IPending
    {
        void Complete(JsonElement result);
        void Fail(Exception error);
    }
    private sealed class Pending<T> : IPending
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete(JsonElement result)
        {
            // Deserialize while the received document is alive. The typed result
            // survives its disposal, without cloning another large JSON buffer.
            try { Completion.TrySetResult(result.Deserialize<T>(Wire.Json)!); }
            catch (Exception ex) { Completion.TrySetException(ex); }
        }
        public void Fail(Exception error) => Completion.TrySetException(error);
    }
    private async Task ReadResponses()
    {
        try {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) {
                var before = GC.GetAllocatedBytesForCurrentThread();
                var clock = Stopwatch.StartNew();
                try {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    var id = root.GetProperty("id").GetString()!;
                    if (root.GetProperty("kind").GetString() == "progress") {
                        Progress?.Invoke(root.GetProperty("result").Deserialize<ImportProgress>(Wire.Json)!);
                        continue;
                    }
                    if (!pending.TryRemove(id, out var completion)) continue;
                    if (root.GetProperty("kind").GetString() == "error") {
                        completion.Fail(new WoiException(root.GetProperty("code").GetString() ?? "WorkerError",
                            root.GetProperty("error").GetString() ?? "Worker operation failed."));
                    } else completion.Complete(root.GetProperty("result"));
                } finally {
                    var previous = diagnostics;
                    Volatile.Write(ref diagnostics, new(previous.Replies + 1,
                        previous.AllocatedBytes + GC.GetAllocatedBytesForCurrentThread() - before,
                        clock.Elapsed.TotalMilliseconds, Math.Max(previous.MaximumMilliseconds, clock.Elapsed.TotalMilliseconds)));
                }
            }
        } catch (Exception ex) { FailPending(ex); }
        finally { disconnected=true; FailPending(new IOException("The analysis worker exited. Open the recording again to restart it.")); }
    }
    private void FailPending(Exception error)
    {
        foreach (var item in pending)
            if (pending.TryRemove(item.Key, out var completion)) completion.Fail(error);
    }
    public async Task<T> Send<T>(string method, object parameters, CancellationToken token = default)
    {
        if (!IsAlive) throw new IOException("The analysis worker exited. Open the recording again to restart it.");
        token.ThrowIfCancellationRequested();
        var id = Guid.NewGuid().ToString("N");
        var completion = new Pending<T>();
        pending[id] = completion;
        try {
            await Write(new Request(id, Wire.Version, method, JsonSerializer.SerializeToElement(parameters, Wire.Json)));
            using var registration = token.Register(() => _ = Cancel(id));
            return await completion.Completion.Task.WaitAsync(token);
        } finally { pending.TryRemove(id, out _); }
    }
    private async Task Cancel(string id)
    {
        try { await Write(new Request(Guid.NewGuid().ToString("N"), Wire.Version, "cancel",
            JsonSerializer.SerializeToElement(new { requestId = id }, Wire.Json))); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }
    private async Task Write(Request value)
    {
        await inputGate.WaitAsync().ConfigureAwait(false);
        try {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(value, Wire.Json)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        } finally { inputGate.Release(); }
    }
    public void Dispose()
    {
        disconnected=true;
        FailPending(new IOException("The analysis worker was closed."));
        try { process.StandardInput.Close(); if (!process.WaitForExit(1000)) process.Kill(true); }
        catch (InvalidOperationException) { }
        process.Dispose();
    }
}
