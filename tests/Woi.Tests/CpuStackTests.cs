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
            Assert.Equal(selected.Sum(x => x.CpuWeightMs ?? 0), page.Selected.KnownCpuMs, 7);
            Assert.Equal(engine.Contributors(interval, default).Sum(x => x.CpuSamples), page.Selected.Samples);
            var rows = page.Rows.ToList();
            while (page.More) { page = reader.QueryCpuStacks(query with { Offset = rows.Count }, default); rows.AddRange(page.Rows); }
            Assert.Equal(selected.Length, rows.Sum(x => x.Exclusive.Samples));
            Assert.Equal(selected.Sum(x => x.CpuWeightMs ?? 0), rows.Sum(x => x.Exclusive.KnownCpuMs), 7);
            var chosen = rows.First(x => x.Exclusive.Samples > 0);
            Assert.Equal(chosen.Evidence.EtlxEventIndex, reader.GetEvidence(chosen.Evidence.EtlxEventIndex, null, default).Observation.Index);
            var process = selected.First(x => x.ProcessId != null).ProcessId;
            var scoped = reader.QueryCpuStacks(query with { ProcessId = process }, default);
            Assert.Equal(selected.Count(x => x.ProcessId == process), scoped.Selected.Samples);
            Assert.Throws<WoiException>(() => reader.QueryCpuStacks(query with { ProcessId = "missing" }, default));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => reader.QueryCpuStacks(query, cancelled.Token));
            Assert.Equal(selected.Length, reader.QueryCpuStacks(query, default).Selected.Samples);
            Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
        } finally { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
    }
}
