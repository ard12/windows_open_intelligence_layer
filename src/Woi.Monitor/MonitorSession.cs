using System.Diagnostics;
using Woi.Core;

namespace Woi.Monitor;

public sealed class MonitorSession(IRawSampler sampler, int logicalProcessors)
{
    private readonly object gate = new();
    private readonly SnapshotCalculator calculator = new(logicalProcessors);
    private readonly SnapshotRing ring = new();
    private readonly List<MonitorMark> marks = [];
    private readonly DateTime startedUtc = DateTime.UtcNow;

    public void SampleOnce()
    {
        RawSnapshot raw;
        try { raw = sampler.Sample(); }
        catch (Exception ex) {
            raw = new(Stopwatch.GetTimestamp(), DateTime.UtcNow, null, null, 0, 0, [],
                [new(DateTime.UtcNow, "SamplingFailure", ex.GetType().Name + ": " + ex.Message)]);
        }
        lock (gate) ring.Add(calculator.Calculate(raw));
    }
    public MonitorView View()
    {
        lock (gate) return ring.View(startedUtc, marks);
    }
    public IReadOnlyList<MonitorLifetime> Lifetimes()
    {
        lock (gate) return ring.Lifetimes();
    }
    public MonitorMark Mark(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 80)
            throw new WoiException("InvalidMark", "Provide a mark label of 1–80 characters.");
        lock (gate) {
            var mark = new MonitorMark(Guid.NewGuid().ToString("N"), Stopwatch.GetTimestamp(),
                DateTime.UtcNow, label.Trim());
            marks.Add(mark);
            if (marks.Count > 1000) marks.RemoveAt(0);
            return mark;
        }
    }
    public async Task Run(CancellationToken token)
    {
        SampleOnce();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(token)) SampleOnce();
    }
}
