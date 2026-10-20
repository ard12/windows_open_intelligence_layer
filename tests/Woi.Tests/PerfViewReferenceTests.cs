using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using Woi.Core;
using Woi.Trace;
using Xunit;
namespace Woi.Tests;
public sealed class PerfViewReferenceTests
{
    [Fact]
    public void RecursiveFunctionSummaryMatchesInstalledPerfViewCallTree()
    {
        var source = new InternStackSource();
        CpuStackSample[] samples = [Sample(1, 2, ["A", "B", "A", "C"]), Sample(2, 3, ["A", "D"])];
        foreach (var sample in samples) {
            var stack = StackSourceCallStackIndex.Invalid;
            foreach (var frame in sample.Frames.Reverse())
                stack = source.Interner.CallStackIntern(source.Interner.FrameIntern(frame.Key), stack);
            source.AddSample(new StackSourceSample(source) { StackIndex = stack, Count = 1,
                Metric = (float)sample.Event.CpuWeightMs!.Value, TimeRelativeMSec = sample.Event.TimeMs });
        }
        source.Interner.DoneInterning();
        var reference = new CallTree(ScalingPolicyKind.ScaleToData) { DisableParallelism = true, StackSource = source };
        var result = CpuStackAggregation.Build("hash", new(0, 100), samples, default);
        Assert.Equal(2, result.Selected.Samples);
        Assert.Equal(5d, result.Selected.KnownCpuMs);
        Assert.Equal(5f, reference.Root.InclusiveMetric);
        foreach (var row in result.Rows) {
            var expected = Assert.Single(reference.ByID, x => x.Name == row.Key);
            Assert.Equal((long)expected.InclusiveCount, row.Inclusive.Samples);
            Assert.Equal((long)expected.ExclusiveCount, row.Exclusive.Samples);
            Assert.Equal((double)expected.InclusiveMetric, row.Inclusive.KnownCpuMs);
            Assert.Equal((double)expected.ExclusiveMetric, row.Exclusive.KnownCpuMs);
        }
        Assert.Equal(2, result.Rows.Single(x => x.Key == "A").Inclusive.Samples);
        Assert.Equal(5d, result.Rows.Sum(x => x.Exclusive.KnownCpuMs));
        Assert.Equal(12d, result.Rows.Sum(x => x.Inclusive.KnownCpuMs));
    }
    private static CpuStackSample Sample(long index, double weight, string[] rootToLeaf) =>
        new(CoreTests.Obs(index, index, weight: weight), rootToLeaf.Reverse().Select(x => new CpuStackFrame(x, x, "module", "module", true)).ToArray());

    // Invoke from an existing fixture test after import; creates no new cache or recording.
    // Shared selection; independent TraceEvent stack extraction and aggregation.
    internal static void AssertNativeCapturedCountParity(EtlReader reader, TraceSession session, IntervalQuery interval)
    {
        var samples = reader.ReadCpuStacks(interval, default).Where(x => x.Frames.Count > 0 && !x.Truncated).ToArray();
        Assert.NotEmpty(samples);
        var indices = samples.Select(x => x.Event.Index).ToHashSet();
        using var log = new TraceLog(Path.Combine(session.CacheDirectory, "events.etlx"));
        var events = log.Events.Filter(e => e is SampledProfileTraceData &&
            indices.Contains((long)e.EventIndex) && e.CallStackIndex() != CallStackIndex.Invalid);
        var source = new TraceEventStackSource(events) { ShowUnknownAddresses = true };
        var reference = new CallTree(ScalingPolicyKind.ScaleToData) { DisableParallelism = true, StackSource = source };
        var result = CpuStackAggregation.Build(session.SourceHash,
            new CpuStackQuery(interval.StartMs, interval.EndMs, interval.ProcessId, Limit: 500), samples, default);
        Assert.Equal(samples.LongLength, (long)reference.Root.InclusiveCount);
        Assert.Equal(samples.LongLength, result.Selected.Samples);
        Assert.Equal(samples.LongLength, result.WithStacks.Samples);
        Assert.Equal(0, result.WithoutStacks.Samples);
        var capturedLeaves = reference.ByID.Where(x => source.GetFrameCodeAddress(x.ID) != CodeAddressIndex.Invalid).Sum(x => x.ExclusiveCount);
        Assert.Equal((double)samples.LongLength, (double)capturedLeaves);
        long exclusiveSamples = 0;
        for (int offset = 0; offset < result.TotalRows; offset += 500) {
            var page = CpuStackAggregation.Build(session.SourceHash, result.Query with { Offset = offset, Limit = 500 }, samples, default);
            exclusiveSamples += page.Rows.Sum(x => x.Exclusive.Samples);
        }
        Assert.Equal(samples.LongLength, exclusiveSamples);
        // Not millisecond parity: reference uses one log interval; WOI uses per-event weights.
        // Not per-frame parity: reference canonicalizes by displayed name.
    }
}
