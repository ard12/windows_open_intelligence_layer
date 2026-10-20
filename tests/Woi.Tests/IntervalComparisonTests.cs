using System.Security.Cryptography;
using Woi.Core;
using Woi.Trace;
using Xunit;

namespace Woi.Tests;

public sealed class IntervalComparisonTests
{
    [Fact]
    public void UnequalDurationsKeepTotalsAndRatesSeparateAndSwapReversesDelta()
    {
        using var fixture = new CoreTests.FakeFixture();
        var query = new IntervalComparisonQuery(new(0, 1000), new(1000, 3000), IntervalMetric.CpuSamples);
        var page = fixture.Engine.CompareIntervals(query, default);
        var samples = page.Totals[0];
        Assert.Equal(2, samples.Baseline); Assert.Equal(4, samples.Comparison);
        Assert.Equal(2, samples.Difference); Assert.Equal(100, samples.PercentChange);
        Assert.Equal(2, samples.BaselineRate); Assert.Equal(2, samples.ComparisonRate); Assert.Equal(0, samples.RateDifference);
        Assert.Equal(samples, Assert.Single(page.Rows).Value);
        var reversed = fixture.Engine.CompareIntervals(query with { Baseline = query.Comparison, Comparison = query.Baseline }, default);
        Assert.Equal(-2, reversed.Totals[0].Difference); Assert.Equal(-50, reversed.Totals[0].PercentChange);
        var identical = fixture.Engine.CompareIntervals(query with { Comparison = query.Baseline }, default);
        Assert.Equal(0, identical.Totals[0].Difference);
    }
    [Fact]
    public void ExactEdgesClampAndKnownZeroDoNotBecomeMissingOrInfinitePercent()
    {
        using var fixture = new CoreTests.FakeFixture();
        var page = fixture.Engine.CompareIntervals(new(new(100, 500), new(2900, 9000), IntervalMetric.CpuSamples), default);
        Assert.Equal(1, page.Totals[0].Baseline); Assert.Equal(1, page.Totals[0].Comparison);
        Assert.Equal(3000, page.Query.Comparison.EndMs);
        Assert.Equal(10, page.Totals[0].ComparisonRate);
        var empty = fixture.Engine.CompareIntervals(new(new(0, 100), new(100, 500)), default);
        Assert.Equal(0, empty.Totals[0].Baseline); Assert.Equal(1, empty.Totals[0].Difference);
        Assert.Null(empty.Totals[0].PercentChange);
        Assert.Equal(0, empty.Totals[1].Baseline); // No observed samples in an available category.
        Assert.Null(empty.Totals[2].Baseline); Assert.Null(empty.Totals[2].Difference); // Unavailable disk category.
    }
    [Fact]
    public void MissingWeightsAndMissingCategoriesStayUnavailable()
    {
        using var fixture = new CoreTests.FakeFixture(weights: false);
        var page = fixture.Engine.CompareIntervals(new(new(0, 1000), new(1000, 3000)), default);
        Assert.Equal(2, page.BaselineUnknownWeights); Assert.Equal(4, page.ComparisonUnknownWeights);
        Assert.Null(page.Totals[1].Baseline); Assert.Null(page.Totals[1].ComparisonRate); Assert.Null(page.Totals[1].Difference);
        Assert.Equal(2, page.Totals[0].Difference);
        using var disk = new CoreTests.FakeFixture(cpuCaptured: false);
        var withoutCpu = disk.Engine.CompareIntervals(new(new(0, 1000), new(1000, 3000), IntervalMetric.DiskBytes), default);
        Assert.Null(withoutCpu.BaselineUnknownWeights); Assert.Null(withoutCpu.Totals[0].Difference);
        Assert.Equal(8192, withoutCpu.Totals[2].Difference); Assert.Equal(0, withoutCpu.Totals[2].RateDifference);
    }
    [Fact]
    public void UnionMatchesLifetimesNotNamesOrPidsAndPagesDeterministically()
    {
        using var fixture = new CoreTests.FakeFixture();
        var values = new[] { CoreTests.Obs(1, 100, process: "old"), CoreTests.Obs(2, 1100, process: "new"), CoreTests.Obs(3, 1200, process: null) };
        using var store = new CacheStore(Path.Combine(fixture.Directory, "lifetime-union"));
        EntityLifetime Life(string id, double start, double end) => new(id, "Process", 7, "same-name", start, end, true, true, null, null, "", "");
        var builder = new AnalysisBuilder(); foreach (var item in values) builder.Add(item);
        store.Save(fixture.Reader.Session, [Life("old", 0, 1000), Life("new", 1000, 3000)], builder.Finish());
        using var reader = new CoreTests.FakeReader(fixture.Reader.Session, values);
        var engine = new QueryEngine(reader, store);
        var query = new IntervalComparisonQuery(new(0, 1000), new(1000, 3000), IntervalMetric.CpuSamples, Limit: 1);
        var pages = Enumerable.Range(0, 3).Select(i => engine.CompareIntervals(query with { Offset = i }, default)).ToArray();
        Assert.Equal(3, pages[0].TotalRows); Assert.True(pages[0].More); Assert.False(pages[2].More);
        var rows = pages.SelectMany(x => x.Rows).ToArray();
        Assert.Equal(3, rows.Select(x => x.ProcessId).Distinct().Count());
        Assert.Equal(-1, rows.Single(x => x.ProcessId == "old").Value.Difference);
        Assert.Equal(1, rows.Single(x => x.ProcessId == "new").Value.Difference);
        Assert.Equal("Unattributed activity", rows.Single(x => x.ProcessId == "?").Name);
        Assert.Equal(pages[0].Totals[0].Baseline, rows.Sum(x => x.Value.Baseline));
        Assert.Equal(pages[0].Totals[0].Comparison, rows.Sum(x => x.Value.Comparison));
        Assert.Equal(pages[0].Rows[0].ProcessId, engine.CompareIntervals(query, default).Rows[0].ProcessId);
        Assert.Throws<WoiException>(() => engine.CompareIntervals(query with { Baseline = new(0, 1000, "old"), Comparison = new(1000, 3000, "new") }, default));
    }
    [Fact]
    public void InvalidQueriesAndCancellationFailWithoutPoisoningNextComparison()
    {
        using var fixture = new CoreTests.FakeFixture();
        var query = new IntervalComparisonQuery(new(0, 1000), new(1000, 3000));
        foreach (var invalid in new[] { query with { Limit = 501 }, query with { Offset = -1 }, query with { Metric = (IntervalMetric)99 },
            query with { Baseline = null! }, query with { Baseline = new(double.NaN, 1000) }, query with { Comparison = new(3000, 4000) },
            query with { Baseline = new(0, 1000, "missing") } })
            Assert.Throws<WoiException>(() => fixture.Engine.CompareIntervals(invalid, default));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => fixture.Engine.CompareIntervals(query, cancelled.Token));
        Assert.Equal(2, fixture.Engine.CompareIntervals(query, default).Totals[0].Difference);
    }
    [FixtureTheory]
    [InlineData("net452/net.4.5.2.x64.etl")]
    [InlineData("allocator/SimpleAllocator-dotnet6.etl")]
    public void RealIntervalsMatchIndependentEventEnumerationAndPreserveSources(string fixture)
    {
        var path = Path.Combine(Environment.GetEnvironmentVariable("WOI_FIXTURES")!, fixture.Replace('/', Path.DirectorySeparatorChar));
        var hash = SHA256.HashData(File.ReadAllBytes(path));
        var cache = Path.Combine(Path.GetTempPath(), "woi-intervals-" + Guid.NewGuid().ToString("N"));
        try {
            var session = EtlImporter.Import(path, cache, null, default);
            using var reader = new EtlReader(session); using var store = new CacheStore(session.CacheDirectory);
            var engine = new QueryEngine(reader, store);
            var a = new IntervalQuery(session.DurationMs * .11, session.DurationMs * .33);
            var b = new IntervalQuery(session.DurationMs * .44, session.DurationMs * .88);
            var page = engine.CompareIntervals(new(a, b, IntervalMetric.CpuSamples, Limit: 1), default);
            var allA = reader.ReadObservations(a, default).ToArray();
            var allB = reader.ReadObservations(b, default).ToArray();
            var av = allA.Where(x => x.Category == "CPU").ToArray();
            var bv = allB.Where(x => x.Category == "CPU").ToArray();
            Assert.Equal(av.Length, page.Totals[0].Baseline); Assert.Equal(bv.Length, page.Totals[0].Comparison);
            Assert.Equal(av.Count(x => x.CpuWeightMs == null), page.BaselineUnknownWeights);
            Assert.Equal(bv.Count(x => x.CpuWeightMs == null), page.ComparisonUnknownWeights);
            for (var side = 0; side < 2; side++) {
                var events = side == 0 ? allA : allB; var cpuEvents = side == 0 ? av : bv;
                double? Actual(int metric) => side == 0 ? page.Totals[metric].Baseline : page.Totals[metric].Comparison;
                if (cpuEvents.All(x => x.CpuWeightMs != null)) Assert.Equal(cpuEvents.Sum(x => x.CpuWeightMs!.Value), Actual(1)!.Value, 6);
                else Assert.Null(Actual(1));
                foreach (var (metric, capability, category) in new[] { (2, "Disk", "Disk"), (3, "FileBytes", "File"), (4, "NetworkBytes", "Network") }) {
                    if (!session.Capabilities.Any(x => x.Category == capability && x.Status == Availability.Available)) { Assert.Null(Actual(metric)); continue; }
                    var values = events.Where(x => x.Category == category && (category != "Network" ||
                        x.Operation.Contains("Send", StringComparison.OrdinalIgnoreCase) || x.Operation.Contains("Recv", StringComparison.OrdinalIgnoreCase) ||
                        x.Operation.Contains("Receive", StringComparison.OrdinalIgnoreCase)));
                    Assert.Equal(values.Sum(x => x.Bytes ?? 0), Actual(metric));
                }
                if (session.Capabilities.Any(x => x.Category == "Disk" && x.Status == Availability.Available))
                    Assert.Equal(events.Count(x => x.Category == "Disk"), Actual(5));
                else Assert.Null(Actual(5));
            }
            Assert.Equal(bv.Length / ((b.EndMs - b.StartMs) / 1000) - av.Length / ((a.EndMs - a.StartMs) / 1000), page.Totals[0].RateDifference!.Value, 8);
            var rows = page.Rows.ToList();
            while (page.More) { page = engine.CompareIntervals(page.Query with { Offset = rows.Count }, default); rows.AddRange(page.Rows); }
            Assert.Equal(av.Length, rows.Sum(x => x.Value.Baseline)); Assert.Equal(bv.Length, rows.Sum(x => x.Value.Comparison));
            foreach (var row in rows) {
                foreach (var reference in row.BaselineEvidence) Assert.InRange(reader.GetEvidence(reference.EtlxEventIndex, null, default).Observation.TimeMs, a.StartMs, Math.BitDecrement(a.EndMs));
                foreach (var reference in row.ComparisonEvidence) Assert.InRange(reader.GetEvidence(reference.EtlxEventIndex, null, default).Observation.TimeMs, b.StartMs, Math.BitDecrement(b.EndMs));
            }
            var process = av.First(x => x.ProcessId != null).ProcessId;
            var scoped = engine.CompareIntervals(new(a with { ProcessId = process }, b with { ProcessId = process }), default);
            Assert.Equal(av.Count(x => x.ProcessId == process), scoped.Totals[0].Baseline);
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(path)));
        } finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }
}
