using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Text.Json;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Woi.Core;

namespace Woi.Validation;

// Attaches only to the app this tool launches. Runtime allocation ticks are
// statistical diagnostics, not heap dumps or observation-budget acceptance.
internal static class UiAllocations
{
    private sealed record Totals(long Ticks, long SampleWeightBytes);
    public static async Task<int> Run(string directory, string executable, int seconds)
    {
        if (seconds is < 20 or > 120) throw new ArgumentOutOfRangeException(nameof(seconds));
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "ui-allocations.json");
        var soak = Path.Combine(directory, "instrumented-soak.json");
        var types = new Dictionary<(string Name, string Kind, int Thread), Totals>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds + 90));
        var clock = Stopwatch.StartNew();
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) {
            UseShellExecute=false, CreateNoWindow=true, WindowStyle=ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("--observation-soak"); start.ArgumentList.Add((seconds + 15).ToString());
        start.ArgumentList.Add(soak);
        using var app = Process.Start(start) ?? throw new IOException("The allocation probe could not launch its app.");
        EventPipeSession? recording = null;
        EventPipeEventSource? source = null;
        Task? processing = null;
        try {
            await Task.Delay(2000, timeout.Token);
            if (app.HasExited) throw new IOException("The app exited before the probe attached.");
            var client = new DiagnosticsClient(app.Id);
            var provider = new EventPipeProvider(ClrTraceEventParser.ProviderName, EventLevel.Verbose,
                (long)ClrTraceEventParser.Keywords.GC);
            recording = await client.StartEventPipeSessionAsync([provider], requestRundown:false,
                circularBufferMB:32, token:timeout.Token);
            source = new EventPipeEventSource(recording.EventStream);
            long ticks = 0, weight = 0;
            source.Clr.GCAllocationTick += data => {
                var key = (data.TypeName ?? "Unresolved type", data.AllocationKind.ToString(), data.ThreadID);
                var amount = data.AllocationAmount64;
                if (amount <= 0) return;
                types.TryGetValue(key, out var prior);
                types[key] = new((prior?.Ticks ?? 0) + 1, (prior?.SampleWeightBytes ?? 0) + amount);
                ticks++; weight += amount;
            };
            processing = Task.Run(source.Process);
            var measure = Stopwatch.StartNew();
            await Task.Delay(TimeSpan.FromSeconds(seconds), timeout.Token);
            await Task.Run(recording.Stop).WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
            await processing.WaitAsync(TimeSpan.FromSeconds(10), timeout.Token);
            var recordedSeconds = measure.Elapsed.TotalSeconds;
            await app.WaitForExitAsync(timeout.Token);
            if (app.ExitCode != 0 || !File.Exists(soak)) throw new IOException("The instrumented app soak did not finish successfully.");
            if (ticks == 0) throw new IOException("The runtime reported no usable allocation ticks.");
            var result = new {
                schema=1, success=true, requestedSeconds=seconds, recordedSeconds,
                elapsedSeconds=clock.Elapsed.TotalSeconds, allocationTicks=ticks, sampleWeightBytes=weight,
                reportedEventsLost=source.EventsLost, provider=ClrTraceEventParser.ProviderName,
                providerKeywords=(long)ClrTraceEventParser.Keywords.GC, circularBufferMiB=32,
                sampledTypes=types.OrderByDescending(x => x.Value.SampleWeightBytes).Take(30)
                    .Select(x => new { type=x.Key.Name, kind=x.Key.Kind, thread=x.Key.Thread,
                        x.Value.Ticks, x.Value.SampleWeightBytes }).ToArray(),
                limitations=new[] {
                    "Allocation ticks attribute the bytes between ticks to a sampled allocation type; these are approximate weights, not exact per-type totals.",
                    "This profiles only the owned WPF process, not the monitor worker, native allocations or retained heap.",
                    "No heap dump, allocation addresses, stacks, raw trace, machine names, paths or user activity payloads are retained.",
                    "The instrumented short soak changes runtime behavior and is not a filled-history, idle-overhead or memory-budget acceptance run.",
                    "Type weights identify candidates for investigation, not the source stack responsible for an allocation."
                }
            };
            Write(output, result);
            return 0;
        } catch (Exception error) {
            Write(output, new { schema=1, success=false, errorType=error.GetType().Name, error=error.Message,
                elapsedSeconds=clock.Elapsed.TotalSeconds });
            return 1;
        } finally {
            source?.StopProcessing();
            recording?.Dispose();
            if (processing != null) {
                try { await processing.WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            }
            source?.Dispose();
            if (!app.HasExited) {
                app.CloseMainWindow();
                if (!await Task.Run(() => app.WaitForExit(5000))) app.Kill(); // Only the owned process, not a PID-derived tree.
            }
        }
    }
    private static void Write(string output, object result)
    {
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions(Wire.Json) { WriteIndented=true }));
        Console.WriteLine(JsonSerializer.Serialize(result, Wire.Json));
    }
}
