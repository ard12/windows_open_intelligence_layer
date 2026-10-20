using Woi.Core;
using Xunit;

namespace Woi.Tests;

public sealed class ProcessHistoryTests
{
    private static EntityLifetime Life(string id, int pid, double start, double end, string? parent = null) =>
        new(id, "Process", pid, id, start, end, true, true, null, parent, "captured.exe", "");

    [Fact]
    public void ExitedParentsAndReusedPidsKeepDistinctHistoricalFamilies()
    {
        using var fixture = new Fixture([
            Life("parent", 7, 0, 900), Life("child", 8, 100, 2900, "parent"),
            Life("grandchild", 9, 200, 2500, "child"), Life("reused", 7, 1000, 3000),
            Life("new-child", 10, 1100, 3000, "reused")
        ], [CoreTests.Obs(1, 1200, process: "child", weight: 2), CoreTests.Obs(2, 1300, process: "grandchild", weight: 3),
            CoreTests.Obs(3, 1400, process: "new-child", weight: 10)]);
        var page = fixture.Engine.History(new(1000, 2000, "parent"), default);
        Assert.Equal(new[] { "parent", "child", "grandchild" }, page.Rows.Select(x => x.Entity.Id));
        Assert.Equal(new[] { 0, 1, 2 }, page.Rows.Select(x => x.Depth));
        Assert.Equal("Exited before interval", page.Rows[0].State);
        Assert.Equal(2, page.Totals.CpuSamples); Assert.Equal(5, page.Totals.CpuMs);
        Assert.Null(page.Rows[0].Activity); Assert.Equal("parent", page.Focus!.Id);
        var child = fixture.Engine.History(new(1000, 2000, "child"), default);
        Assert.Equal("parent", child.FocusParentId); Assert.Equal(0, child.Rows[0].Depth);
        Assert.Equal(10, fixture.Engine.History(new(1000, 2000, "reused"), default).Totals.CpuMs);
    }

    [Fact]
    public void MissingImpossibleSelfAndCyclicParentsRemainVisibleAsUnresolvedRoots()
    {
        using var fixture = new Fixture([
            Life("missing", 1, 100, 3000, "absent"), Life("self", 2, 100, 3000, "self"),
            Life("a", 3, 100, 3000, "b"), Life("b", 4, 100, 3000, "a"),
            Life("too-new", 5, 500, 3000), Life("early-child", 6, 100, 3000, "too-new"),
            Life("unknown-start", 7, 0, 3000, "a") with { StartKnown = false, EndKnown = false }
        ], []);
        var page = fixture.Engine.History(new(0, 3000), default);
        Assert.Equal(7, page.TotalLifetimes); Assert.Equal(7, page.UnresolvedParents);
        Assert.Equal(7, page.Rows.Select(x => x.Entity.Id).Distinct().Count());
        Assert.All(page.Rows, row => { Assert.Equal(0, row.Depth); Assert.Equal("Unresolved / not captured", row.Parent); });
        Assert.Equal("Overlaps; boundary incomplete", page.Rows.Single(x => x.Entity.Id == "unknown-start").State);
        Assert.Null(page.Totals.CpuSamples); Assert.Null(page.Totals.CpuMs);
    }

    [Fact]
    public void PagingNeverChangesFamilyTotalsAndPartialIntervalsUseExactEvents()
    {
        using var fixture = new Fixture([Life("p", 1, 0, 3000), Life("c", 2, 0, 3000, "p"), Life("later", 3, 2500, 3000, "p")],
            [CoreTests.Obs(1, 100, process: "p"), CoreTests.Obs(2, 250, process: "c"), CoreTests.Obs(3, 1500, process: "c"), CoreTests.Obs(4, 2250, process: "c")]);
        var first = fixture.Engine.History(new(250, 2250, "p", Limit: 1), default);
        var second = fixture.Engine.History(new(250, 2250, "p", Offset: 1, Limit: 1), default);
        var last = fixture.Engine.History(new(250, 2250, "p", Offset: 2, Limit: 1), default);
        Assert.True(first.More); Assert.True(second.More); Assert.False(last.More);
        Assert.Equal(2, first.Totals.CpuSamples); Assert.Equal(first.Totals, second.Totals); Assert.Equal(first.Totals, last.Totals);
        Assert.Equal("Starts after interval", Assert.Single(last.Rows).State);
        Assert.Empty(fixture.Engine.History(new(250, 2250, "p", Offset: int.MaxValue), default).Rows);
    }

    [Fact]
    public void UnknownWeightsAndAbsentByteCoverageDoNotBecomeMeasuredZero()
    {
        using var fixture = new Fixture([Life("p", 1, 0, 3000), Life("c", 2, 0, 3000, "p")],
            [CoreTests.Obs(1, 100, process: "p", weight: 5), CoreTests.Obs(2, 200, process: "c", weight: null)]);
        var page = fixture.Engine.History(new(0, 3000, "p"), default);
        Assert.Equal(2, page.Totals.CpuSamples); Assert.Null(page.Totals.CpuMs);
        Assert.Null(page.Totals.DiskBytes); Assert.Null(page.Totals.FileBytes); Assert.Null(page.Totals.NetworkBytes);
    }

    [Fact]
    public void DeepFamiliesUseBoundedPagesWithoutRecursiveTraversal()
    {
        var lives = Enumerable.Range(0, 10000).Select(i => Life("p" + i, i + 1, 0, 3000, i == 0 ? null : "p" + (i - 1))).ToArray();
        using var fixture = new Fixture(lives, []);
        var page = fixture.Engine.History(new(0, 3000, Offset: 9990, Limit: 10), default);
        Assert.Equal(10000, page.TotalLifetimes); Assert.Equal(9999, page.Rows[^1].Depth); Assert.False(page.More);
        Assert.Equal(5000, fixture.Engine.History(new(0, 3000, "p5000", Limit: 1), default).TotalLifetimes);
    }

    [Fact]
    public void EmptyHistoryBadQueriesAndCancellationAreExplicit()
    {
        using var fixture = new Fixture([], []);
        Assert.Empty(fixture.Engine.History(new(0, 3000), default).Rows);
        Assert.Equal("InvalidPage", Assert.Throws<WoiException>(() => fixture.Engine.History(new(0, 3000, Limit: 501), default)).Code);
        Assert.Equal("InvalidPage", Assert.Throws<WoiException>(() => fixture.Engine.History(new(0, 3000, Offset: -1), default)).Code);
        Assert.Equal("EntityMissing", Assert.Throws<WoiException>(() => fixture.Engine.History(new(0, 3000, "missing"), default)).Code);
        Assert.Equal("InvalidInterval", Assert.Throws<WoiException>(() => fixture.Engine.History(new(double.NaN, 3000), default)).Code);
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Engine.History(new(0, 3000), new CancellationToken(true)));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "woi-family-" + Guid.NewGuid().ToString("N"));
        private readonly CacheStore store;
        public QueryEngine Engine { get; }
        public Fixture(IReadOnlyList<EntityLifetime> lives, IReadOnlyList<Observation> values)
        {
            store = new(directory);
            var session = new TraceSession("hash", "source", "hash", "parser", Wire.AnalysisVersion,
                directory, DateTime.UtcNow, 3000, 1, values.Count, 0,
                [new Capability("CPU", Availability.Available, values.Count, "fixture")], [], 0, 0);
            var builder = new AnalysisBuilder(); foreach (var value in values) builder.Add(value);
            store.Save(session, lives, builder.Finish());
            Engine = new(new CoreTests.FakeReader(session, values), store);
        }
        public void Dispose() { store.Dispose(); Directory.Delete(directory, true); }
    }
}
