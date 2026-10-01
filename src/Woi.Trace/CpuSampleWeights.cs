using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Woi.Core;

namespace Woi.Trace;

// A trace-local index keeps each sample's global predecessor context independent
// of query boundaries, paging order and evidence lookup. No TraceEvent object is
// retained. Analysis version 1.2 invalidates old aggregate caches.
internal sealed class CpuSampleWeights
{
    private readonly Entry[] entries;
    private readonly record struct Entry(long Index, double? Milliseconds);
    public CpuSampleWeights(TraceLog log, IReadOnlyList<SamplingInterval> intervals, CancellationToken token)
    {
        var calculator = new CpuSampleWeightCalculator(intervals);
        var values = new List<Entry>();
        foreach (var data in log.Events) {
            token.ThrowIfCancellationRequested();
            if (data is not SampledProfileTraceData sample) continue;
            var weight = calculator.Observe(sample.ProcessorNumber, sample.TimeStampRelativeMSec);
            // Idle samples are necessary predecessor context, but have no busy
            // contributor weight. Interrupt/DPC samples remain included.
            if (sample.ThreadID != 0 || sample.NonProcess)
                values.Add(new((long)sample.EventIndex, weight));
        }
        entries = values.OrderBy(x => x.Index).ToArray();
    }
    public double? Get(long index)
    {
        int low=0, high=entries.Length-1;
        while (low <= high) {
            int middle=low+(high-low)/2;
            if (entries[middle].Index == index) return entries[middle].Milliseconds;
            if (entries[middle].Index < index) low=middle+1; else high=middle-1;
        }
        return null;
    }
}
internal sealed class CpuSampleWeightCalculator(IReadOnlyList<SamplingInterval> intervals)
{
    private readonly Dictionary<int, double> previous = [];
    public double? Observe(int processor, double time)
    {
        if (!double.IsFinite(time) || processor < 0) return null;
        bool hadPrevious=previous.TryGetValue(processor, out var before);
        previous[processor]=time;
        var interval=EtlReader.SamplingWeight(intervals, time);
        if (interval == null) return null;
        if (!hadPrevious) return interval;
        var elapsed=time-before;
        if (elapsed <= 0 || !double.IsFinite(elapsed)) return null;
        // Microsoft documents predecessor spacing. The 110% outlier fallback
        // is empirically verified against TraceProcessing 1.12.10, per sample,
        // in three independent Microsoft fixtures; it is not an exact scheduler
        // accounting measurement or a claim of parity on all Windows versions.
        return elapsed > interval.Value * 1.1 ? interval : elapsed;
    }
}
