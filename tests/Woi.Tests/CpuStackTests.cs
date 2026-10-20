using System.Security.Cryptography;
using Microsoft.Diagnostics.Tracing.Etlx;
using Woi.Core;
using Woi.Trace;
using Xunit;

namespace Woi.Tests;

public sealed class CpuStackTests
{
    private static CpuStackFrame Frame(string name) => new(name, name, "module", "module", name != "unknown");
    private static CpuStackSample Sample(long id, double? weight, params string[] frames) => new(CoreTests.Obs(id, id, weight: weight), frames.Select(Frame).ToArray());
    private static CpuStackSample[] RecursiveSamples() => [Sample(1, 2, "A", "B", "A", "R"), Sample(2, null, "B", "A", "R"), Sample(3, 3)];
    [Fact] public void ThreadScopeUsesLifetimeIdentityAndAccountsForUnattributedSamples()
    {
        var oldThread = Sample(1, 1, "A") with { Event = CoreTests.Obs(1, 1, weight: 1) with { ThreadId = "old-thread", Tid = 42 } };
        var reusedTid = Sample(2, 2, "B") with { Event = CoreTests.Obs(2, 2, weight: 2) with { ThreadId = "new-thread", Tid = 42 } };
        var unknown = Sample(3, 3) with { Event = CoreTests.Obs(3, 3, weight: 3) with { ThreadId = null } };
        var samples = new[] { oldThread, reusedTid, unknown };
        var scoped = CpuStackAggregation.Build("hash", new(0, 10, ThreadId: "old-thread"), samples, default);
        Assert.Equal(1, scoped.Selected.Samples); Assert.Equal(1, scoped.Selected.CpuMs);
        Assert.Equal(oldThread.Event.Evidence, Assert.Single(scoped.Rows).Evidence);
        var all = CpuStackAggregation.Build("hash", new(0, 10), samples, default);
        Assert.Equal(all.Selected.Samples, all.Threads!.Sum(x => x.Metric.Samples));
        Assert.Equal(1, all.Threads!.Single(x => x.ThreadId == null).Metric.Samples);
        Assert.Equal(3, all.Threads!.Single(x => x.ThreadId == null).Metric.CpuMs);
        Assert.Equal(all.Threads, scoped.Threads);
        Assert.Equal(all.Selected.Samples, scoped.Selected.Samples +
            CpuStackAggregation.Build("hash", new(0, 10, ThreadId: "new-thread"), samples, default).Selected.Samples + 1);
        Assert.Empty(CpuStackAggregation.Build("hash", new(0, 10, ThreadId: "missing"), samples, default).Rows);
    }
    [Fact] public void HottestPathUsesOnePassAndSampleCountsWithExplicitUnknownWeights()
    {
        int enumerations = 0;
        IEnumerable<CpuStackSample> Once() {
            Assert.Equal(1, ++enumerations);
            yield return Sample(1, 1, "B", "A", "R"); yield return Sample(2, null, "B", "A", "R");
            yield return Sample(3, 100, "C", "R");
        }
        var page = CpuStackAggregation.Build("hash", new(0, 10, View: CpuStackView.CallTree, HottestPath: true), Once(), default);
        Assert.Equal(1, enumerations); Assert.Equal(new[] { "R", "A", "B" }, page.HotPath!.Keys);
        Assert.Equal(new[] { "R", "A" }, page.Query.Path); Assert.False(page.Query.HottestPath);
        Assert.Null(page.Selected.CpuMs); Assert.Equal(101, page.Selected.KnownCpuMs);
        Assert.Equal(2, Assert.Single(page.Rows).Inclusive.Samples);
        Assert.Equal(1, page.HotPath.Evidence!.EtlxEventIndex);
    }
    [Fact] public void HottestPathStopsAtDominantSelfAndBreaksTiesDeterministically()
    {
        var query = new CpuStackQuery(0, 10, View: CpuStackView.CallTree, HottestPath: true);
        var self = CpuStackAggregation.Build("hash", query, [Sample(1, 1, "R"), Sample(2, 1, "R"), Sample(3, 1, "A", "R")], default);
        Assert.Equal(new[] { "R" }, self.HotPath!.Keys);
        var ties = CpuStackAggregation.Build("hash", query, [Sample(1, 1, "B", "R"), Sample(2, 1, "A", "R")], default);
        Assert.Equal(new[] { "R", "A" }, ties.HotPath!.Keys);
        var unknown = CpuStackAggregation.Build("hash", query, [Sample(1, 1), Sample(2, null), Sample(3, 10, "A")], default);
        Assert.Equal(new[] { "missing-stack" }, unknown.HotPath!.Keys);
        Assert.Equal(2, unknown.WithoutStacks.Samples);
        var empty = CpuStackAggregation.Build("hash", query, [], default);
        Assert.Empty(empty.HotPath!.Keys); Assert.Empty(empty.Rows); Assert.Null(empty.HotPath.Evidence);
        Assert.Throws<WoiException>(() => CpuStackAggregation.Build("hash", query with { ThreadId = "" }, [], default));
        Assert.Throws<WoiException>(() => CpuStackAggregation.Build("hash", query with { Path = ["R"] }, [], default));
    }
    [Fact] public void HottestPathBoundsItsContextualTree()
    {
        var samples = Enumerable.Range(0, CpuStackAggregation.MaximumRows + 1).Select(i => Sample(i, 1, "frame" + i));
        var error = Assert.Throws<WoiException>(() => CpuStackAggregation.Build("hash",
            new(0, 200000, View: CpuStackView.CallTree, HottestPath: true), samples, default));
        Assert.Equal("StackLimit", error.Code);
    }
    [Fact] public void FunctionsDeduplicateRecursionAndRetainMissingStacksAndUnknownWeights()
    {
        var page = CpuStackAggregation.Build("hash", new(0, 10), RecursiveSamples(), default);
        Assert.Equal(3, page.Selected.Samples); Assert.Equal(2, page.WithStacks.Samples); Assert.Equal(1, page.WithoutStacks.Samples);
        Assert.Equal(5, page.Selected.KnownCpuMs); Assert.Null(page.Selected.CpuMs);
        Assert.Equal(3, page.Rows.Sum(x => x.Exclusive.Samples)); Assert.Equal(5, page.Rows.Sum(x => x.Exclusive.KnownCpuMs));
        var a = page.Rows.Single(x => x.Key == "A");
        Assert.Equal(2, a.Inclusive.Samples); Assert.Equal(1, a.Exclusive.Samples); Assert.Equal(2, a.Exclusive.CpuMs);
        Assert.Equal(200d / 3, a.SamplePercent, 8); Assert.Null(a.Inclusive.CpuMs);
        Assert.Equal(3, page.Rows.Single(x => x.Key == "missing-stack").Exclusive.CpuMs);
        var modules = CpuStackAggregation.Build("hash", new(0, 10, Grouping: CpuStackGrouping.Module), RecursiveSamples(), default);
        Assert.Equal(2, modules.Rows.Single(x => x.Key == "module").Inclusive.Samples);
        Assert.Equal(2, modules.Rows.Single(x => x.Key == "module").Exclusive.Samples);
    }
    [Fact] public void TreeAndFocusedPathsConserveSamplesWithoutInflatingRecursion()
    {
        CpuStackPage Build(CpuStackView view, string[]? path = null, string? focus = null) => CpuStackAggregation.Build("hash", new(0, 10, View: view, FocusKey: focus, Path: path), RecursiveSamples(), default);
        var root = Build(CpuStackView.CallTree);
        Assert.Equal(3, root.Rows.Sum(x => x.Inclusive.Samples));
        var branch = Build(CpuStackView.CallTree, ["R", "A", "B"]);
        Assert.Equal(2, branch.Context.Samples); Assert.Equal(1, branch.ContextSelf.Samples);
        Assert.Equal(branch.Context.Samples, branch.ContextSelf.Samples + branch.Rows.Sum(x => x.Inclusive.Samples));
        Assert.Equal("A", Assert.Single(branch.Rows).Key);
        var callers = Build(CpuStackView.Callers, focus: "A");
        Assert.Equal(2, callers.Context.Samples); Assert.Equal(1, Assert.Single(callers.Rows).Exclusive.Samples);
        var recursiveCaller = Build(CpuStackView.Callers, ["A", "B"], "A");
        Assert.Equal(0, Assert.Single(recursiveCaller.Rows).Exclusive.Samples);
        var callees = Build(CpuStackView.Callees, ["A"], "A");
        Assert.Equal(2, callees.Context.Samples); Assert.Equal(1, callees.ContextSelf.Samples);
        Assert.Equal("B", Assert.Single(callees.Rows).Key);
    }
    [Fact] public void BoundariesIdentityPagingCoverageAndCancellationAreExplicit()
    {
        var samples = new[] { Sample(1, 1, "unknown"), Sample(2, 2, "A") with { Truncated = true }, Sample(3, 3, "B") };
        var page = CpuStackAggregation.Build("hash", new(1, 3, Limit: 1), samples, default);
        Assert.Equal(2, page.Selected.Samples); Assert.Equal(1, page.UnresolvedSamples); Assert.Equal(1, page.TruncatedSamples); Assert.True(page.More);
        var next = CpuStackAggregation.Build("hash", page.Query with { Offset = 1 }, samples, default);
        Assert.NotEqual(page.Rows[0].Key, next.Rows[0].Key);
        var other = CpuStackAggregation.Build("hash", new(0, 10, ProcessId: "different-lifetime"), samples, default);
        Assert.Empty(other.Rows); Assert.Equal(0, other.Selected.Samples);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => CpuStackAggregation.Build("hash", new(), samples, cancelled.Token));
        foreach (var query in new[] { new CpuStackQuery(View: (CpuStackView)999), new(Limit: 501), new(Offset: -1), new(View: CpuStackView.Callers), new(Path: ["bad"]) })
            Assert.Throws<WoiException>(() => CpuStackAggregation.Build("hash", query, samples, default));
    }
    [FixtureTheory]
    [InlineData("net452/net.4.5.2.x64.etl")]
    [InlineData("allocator/SimpleAllocator-dotnet6.etl")]
    public void RealCapturedStacksReconcileWithRawEtlxAndContributorWeights(string fixture)
    {
        var source = Path.Combine(Environment.GetEnvironmentVariable("WOI_FIXTURES")!, fixture.Replace('/', Path.DirectorySeparatorChar));
        var hash = SHA256.HashData(File.ReadAllBytes(source));
        var cache = Path.Combine(Path.GetTempPath(), "woi-stacks-" + Guid.NewGuid().ToString("N"));
        try {
            var session = EtlImporter.Import(source, cache, null, default);
            using var reader = new EtlReader(session);
            using var store = new CacheStore(session.CacheDirectory);
            var engine = new QueryEngine(reader, store);
            var interval = new IntervalQuery(session.DurationMs * .1, session.DurationMs * .9);
            var selected = reader.ReadObservations(interval, default).Where(x => x.Category == "CPU").ToArray();
            var ids = selected.Select(x => x.Index).ToHashSet();
            using var raw = new TraceLog(Path.Combine(session.CacheDirectory, "events.etlx"));
            long captured = 0;
            foreach (var data in raw.Events) if (ids.Contains((long)data.EventIndex) && data.CallStack() != null) captured++;
            Assert.True(captured > 0, "Fixture must contain native captured CPU stacks.");
            PerfViewReferenceTests.AssertNativeCapturedCountParity(reader, session, interval);
            var query = new CpuStackQuery(interval.StartMs, interval.EndMs, Limit: 500);
            var page = reader.QueryCpuStacks(query, default);
            Assert.Equal(selected.Length, page.Selected.Samples); Assert.Equal(captured, page.WithStacks.Samples);
            Assert.Equal(selected.Length - captured, page.WithoutStacks.Samples);
            Assert.Equal(selected.Length, page.Threads!.Sum(x => x.Metric.Samples));
            Assert.Equal(selected.Count(x => x.ThreadId == null), page.Threads!.Where(x => x.ThreadId == null).Sum(x => x.Metric.Samples));
            Assert.Equal(selected.Sum(x => x.CpuWeightMs ?? 0), page.Selected.KnownCpuMs, 7);
            Assert.Equal(engine.Contributors(interval, default).Sum(x => x.CpuSamples), page.Selected.Samples);
            var rows = page.Rows.ToList();
            while (page.More) { page = reader.QueryCpuStacks(query with { Offset = rows.Count }, default); rows.AddRange(page.Rows); }
            Assert.Equal(selected.Length, rows.Sum(x => x.Exclusive.Samples));
            Assert.Equal(selected.Sum(x => x.CpuWeightMs ?? 0), rows.Sum(x => x.Exclusive.KnownCpuMs), 7);
            var chosen = rows.First(x => x.Exclusive.Samples > 0);
            Assert.Equal(chosen.Evidence.EtlxEventIndex, reader.GetEvidence(chosen.Evidence.EtlxEventIndex, null, default).Observation.Index);
            var process = selected.First(x => x.ProcessId != null && x.ThreadId != null).ProcessId;
            var scoped = reader.QueryCpuStacks(query with { ProcessId = process }, default);
            Assert.Equal(selected.Count(x => x.ProcessId == process), scoped.Selected.Samples);
            var thread = selected.First(x => x.ProcessId == process && x.ThreadId != null).ThreadId!;
            var threadPage = reader.QueryCpuStacks(query with { ProcessId = process, ThreadId = thread }, default);
            Assert.Equal(selected.Count(x => x.ProcessId == process && x.ThreadId == thread), threadPage.Selected.Samples);
            Assert.Equal(thread, threadPage.Query.ThreadId);
            Assert.Equal(threadPage.Selected, threadPage.Threads!.Single(x => x.ThreadId == thread).Metric);
            var hot = reader.QueryCpuStacks(query with { ProcessId = process, ThreadId = thread, View = CpuStackView.CallTree, HottestPath = true }, default);
            Assert.Equal(threadPage.Selected, hot.Selected); Assert.NotEmpty(hot.HotPath!.Keys);
            var hotEvent = reader.GetEvidence(hot.HotPath.Evidence!.EtlxEventIndex, null, default).Observation;
            Assert.Equal(thread, hotEvent.ThreadId); Assert.Equal(process, hotEvent.ProcessId);
            Assert.Throws<WoiException>(() => reader.QueryCpuStacks(query with { ThreadId = "missing" }, default));
            var foreignProcess = reader.Entities.FirstOrDefault(x => x.Kind == "Process" && x.Id != process);
            if (foreignProcess != null) Assert.Throws<WoiException>(() => reader.QueryCpuStacks(query with { ProcessId = foreignProcess.Id, ThreadId = thread }, default));
            Assert.Throws<WoiException>(() => reader.QueryCpuStacks(query with { ProcessId = "missing" }, default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => reader.QueryCpuStacks(query, cancelled.Token));
            Assert.Equal(selected.Length, reader.QueryCpuStacks(query, default).Selected.Samples);
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        } finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }
}
