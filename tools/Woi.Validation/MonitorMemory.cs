using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Woi.Core;
using Woi.Monitor;

namespace Woi.Validation;

// Isolated allocation diagnosis. Synthetic snapshots and explicit collections here
// must never be presented as native observation cost or desktop budget acceptance.
internal static class MonitorMemory
{
    public static int Run(string output)
    {
        var clock = Stopwatch.StartNew();
        var calculator = new SnapshotCalculator(12);
        var ring = new SnapshotRing();
        var utc = DateTime.UtcNow;
        long tick = Stopwatch.GetTimestamp();
        var managedBefore = GC.GetTotalMemory(true);
        for (int second = 0; second < 1200; second++) {
            var processes = Enumerable.Range(1, 300).Select(pid => new RawProcess(pid, 1, 100000 + pid,
                new string(("synthetic-" + pid).ToCharArray()), (second + 1L) * 100000, (ulong)second * 1024,
                (ulong)second * 2048, 1048576, 2097152)).ToArray();
            ring.Add(calculator.Calculate(new(tick + second * Stopwatch.Frequency, utc.AddSeconds(second),
                8L * 1024 * 1024 * 1024, 12L * 1024 * 1024 * 1024, 300, 0, processes, [], 5)));
        }
        var view = ring.View(utc, []);
        var retainedManagedBytes = GC.GetTotalMemory(true) - managedBefore;
        var json = JsonSerializer.Serialize(new Response("status", Wire.Version, "result", view), Wire.Json);
        var decodeWithClone = Measure(() => {
            using var document = JsonDocument.Parse(json);
            var result = document.RootElement.GetProperty("result").Clone();
            var decoded = result.Deserialize<MonitorView>(Wire.Json)!;
            return decoded.History.Count;
        });
        var decodeInDocument = Measure(() => {
            using var document = JsonDocument.Parse(json);
            var decoded = document.RootElement.GetProperty("result").Deserialize<MonitorView>(Wire.Json)!;
            return decoded.History.Count;
        });
        var stringSerialization = Measure(() => JsonSerializer.Serialize(new Response("status", Wire.Version, "result", view), Wire.Json).Length);
        var utf8Serialization = Measure(() => JsonSerializer.SerializeToUtf8Bytes(new Response("status", Wire.Version, "result", view), Wire.Json).Length);
        var report = new {
            schema=1, utc=DateTime.UtcNow, synthetic=true, generatedSnapshots=1200, processesPerSnapshot=300,
            view.SnapshotCount, view.RetainedProcessRows, view.EstimatedRetainedBytes, retainedManagedBytes,
            statusUtf8Bytes=Encoding.UTF8.GetByteCount(json), decodeWithClone, decodeInDocument,
            stringSerialization, utf8Serialization, elapsedSeconds=clock.Elapsed.TotalSeconds,
            limitations=new[] { "Synthetic in-process allocation diagnosis, not real Windows API sampling or WPF memory acceptance.",
                "Explicit collections isolate retained managed data in this diagnostic process; production and native soaks do not force GC.",
                "Allocated bytes per iteration are managed thread allocations, not working set, native graphics memory or retained bytes." }
        };
        File.WriteAllText(Path.GetFullPath(output), JsonSerializer.Serialize(report, new JsonSerializerOptions(Wire.Json) { WriteIndented=true }));
        Console.WriteLine(JsonSerializer.Serialize(report, Wire.Json));
        return 0;
    }
    private static object Measure(Func<int> operation)
    {
        for (int i = 0; i < 5; i++) operation();
        const int rounds = 100;
        var before = GC.GetAllocatedBytesForCurrentThread();
        var clock = Stopwatch.StartNew();
        long consumed = 0;
        for (int i = 0; i < rounds; i++) consumed += operation();
        return new { rounds, allocatedBytesPerIteration=(GC.GetAllocatedBytesForCurrentThread() - before)/(double)rounds,
            millisecondsPerIteration=clock.Elapsed.TotalMilliseconds/rounds, consumed };
    }
}
