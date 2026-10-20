using System.Diagnostics;
using Woi.Core;
using Woi.Monitor;
using Xunit;

namespace Woi.Tests;

public sealed class MonitorTests
{
    private static RawProcess Process(long creation, long cpu, ulong read = 0, ulong write = 0) =>
        new(42, 1, creation, "work", cpu, read, write, 1234, 5678);
    private static RawSnapshot Raw(long tick, DateTime utc, params RawProcess[] processes) =>
        new(tick, utc, 4096, 8192, processes.Length, 0, processes, []);

    [Fact]
    public void NativeSamplerReadsCurrentLifetimeWithHandleBoundName()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var raw = new WindowsSampler().Sample();
        var row = Assert.Single(raw.Processes, x => x.Pid == Environment.ProcessId);
        Assert.Equal(current.ProcessName, row.Name, ignoreCase: true);
        Assert.Equal(current.SessionId, row.SessionId);
        Assert.Equal(current.StartTime.ToUniversalTime().ToFileTimeUtc(), row.CreationFileTime);
        Assert.Equal(raw.Processes.Count, raw.Processes.Select(SnapshotCalculator.Identity).Distinct().Count());
        Assert.True(raw.EnumeratedProcesses >= raw.Processes.Count);
        Assert.NotNull(raw.SamplingDurationMs);
    }

    [Fact]
    public void RatesNeedSameLifetimeAndFirstSampleIsBaseline()
    {
        var calculator = new SnapshotCalculator(2);
        var utc = DateTime.UtcNow;
        var first = calculator.Calculate(Raw(0, utc, Process(100, 0, 10, 20)));
        Assert.Null(first.IntervalSeconds);
        Assert.Null(first.ReadBytesPerSecond);
        Assert.Null(first.WriteBytesPerSecond);
        Assert.Null(Assert.Single(first.Processes).CpuPercent);
        Assert.Null(Assert.Single(first.Processes).ReadBytesPerSecond);
        var second = calculator.Calculate(Raw(Stopwatch.Frequency, utc.AddSeconds(1),
            Process(100, 10_000_000, 110, 220)));
        Assert.InRange(second.IntervalSeconds!.Value, .999, 1.001);
        Assert.InRange(second.ReadBytesPerSecond!.Value, 99.999, 100.001);
        Assert.InRange(second.WriteBytesPerSecond!.Value, 199.999, 200.001);
        var row = Assert.Single(second.Processes);
        Assert.Equal("1:42:100", row.LifetimeId);
        Assert.InRange(row.CpuPercent!.Value, 49.999, 50.001);
        Assert.InRange(row.ReadBytesPerSecond!.Value, 99.999, 100.001);
        Assert.InRange(row.WriteBytesPerSecond!.Value, 199.999, 200.001);
        var reused = calculator.Calculate(Raw(2L * Stopwatch.Frequency, utc.AddSeconds(2),
            Process(200, 20_000_000, 500, 600)));
        Assert.Equal("1:42:200", Assert.Single(reused.Processes).LifetimeId);
        Assert.Null(Assert.Single(reused.Processes).CpuPercent);
    }

    [Fact]
    public void RetainedSnapshotsShareStableMetadataAndPreserveRecordedNameChanges()
    {
        var calculator = new SnapshotCalculator(2); var utc = DateTime.UtcNow;
        var first = calculator.Calculate(Raw(0, utc, Process(100, 0) with { Name = new string("work".ToCharArray()) })).Processes[0];
        var second = calculator.Calculate(Raw(Stopwatch.Frequency, utc.AddSeconds(1), Process(100, 1) with { Name = new string("work".ToCharArray()) })).Processes[0];
        Assert.Same(first.LifetimeId, second.LifetimeId);
        Assert.Same(first.Name, second.Name);
        var renamed = calculator.Calculate(Raw(2L * Stopwatch.Frequency, utc.AddSeconds(2), Process(100, 2) with { Name = "renamed" })).Processes[0];
        Assert.Same(first.LifetimeId, renamed.LifetimeId);
        Assert.Equal("renamed", renamed.Name);
        Assert.Equal("work", first.Name);
        var reused = calculator.Calculate(Raw(3L * Stopwatch.Frequency, utc.AddSeconds(3), Process(200, 3))).Processes[0];
        Assert.NotEqual(first.LifetimeId, reused.LifetimeId);
        Assert.Null(reused.CpuPercent);
    }

    [Fact]
    public void MissedTickAndCounterResetProduceGapsInsteadOfRates()
    {
        var calculator = new SnapshotCalculator(2);
        var utc = DateTime.UtcNow;
        calculator.Calculate(Raw(0, utc, Process(100, 10_000_000, 200, 200)));
        var missed = calculator.Calculate(Raw(4L * Stopwatch.Frequency, utc.AddSeconds(4),
            Process(100, 20_000_000, 300, 300)));
        Assert.Null(missed.IntervalSeconds);
        Assert.Null(Assert.Single(missed.Processes).CpuPercent);
        Assert.Contains(missed.Gaps, x => x.Kind == "MissedTicks");
        var reset = calculator.Calculate(Raw(5L * Stopwatch.Frequency, utc.AddSeconds(5),
            Process(100, 1_000_000, 10, 10)));
        Assert.Null(Assert.Single(reset.Processes).CpuPercent);
        Assert.Null(Assert.Single(reset.Processes).ReadBytesPerSecond);
        Assert.Contains(reset.Gaps, x => x.Kind == "CounterReset");
    }

    [Fact]
    public void LifetimeQuerySeparatesReusedPidAndRetainsExitedProcess()
    {
        var ring = new SnapshotRing();
        var calculator = new SnapshotCalculator(1);
        var utc = DateTime.UtcNow;
        ring.Add(calculator.Calculate(Raw(0, utc, Process(100, 0))));
        ring.Add(calculator.Calculate(Raw(Stopwatch.Frequency, utc.AddSeconds(1), Process(200, 0))));
        var lifetimes = ring.Lifetimes();
        Assert.Equal(2, lifetimes.Count);
        Assert.Contains(lifetimes, x => x.LifetimeId == "1:42:100" && !x.PresentAtLatest);
        Assert.Contains(lifetimes, x => x.LifetimeId == "1:42:200" && x.PresentAtLatest);
    }

    [Fact]
    public void RingEvictsByRowsAndReportsActualRetainedDuration()
    {
        var ring = new SnapshotRing(maximumSnapshots: 10, maximumRows: 2, maximumBytes: 100_000);
        var calculator = new SnapshotCalculator(1);
        var utc = DateTime.UtcNow;
        for (var i = 0; i < 5; i++)
            ring.Add(calculator.Calculate(Raw(i * Stopwatch.Frequency, utc.AddSeconds(i),
                Process(100, i * 10_000_000))));
        var view = ring.View(utc, []);
        Assert.Equal("WindowsApiSnapshot", view.Source);
        Assert.Equal(2, view.SnapshotCount);
        Assert.Equal(2, view.RetainedProcessRows);
        Assert.InRange(view.RetainedSeconds, .99, 1.01);
        Assert.Equal(2, view.History.Count);
    }
    [Fact]
    public void DownsamplingPreservesCpuPeakMemoryLowAndEndpoints()
    {
        var ring = new SnapshotRing();
        var utc = DateTime.UtcNow;
        for (var i = 0; i < 3600; i++)
            ring.Add(new MonitorSnapshot(i * (long)Stopwatch.Frequency, utc.AddSeconds(i), 1,
                i == 1011 ? 80 : 5, i == 3001 ? 1000 : 10, 20, i == 2017 ? 1000 : 10000, 20000, 0, 0, [], []));
        var view = ring.View(utc, []);
        Assert.Equal(3600, view.SnapshotCount);
        Assert.InRange(view.History.Count, 1, 2000);
        Assert.Equal(utc, view.History[0].Utc);
        Assert.Equal(utc.AddSeconds(3599), view.History[^1].Utc);
        Assert.Contains(view.History, x => x.Utc == utc.AddSeconds(1011) && x.CpuPercent == 80);
        Assert.Contains(view.History, x => x.Utc == utc.AddSeconds(2017) && x.AvailablePhysicalBytes == 1000);
        Assert.Contains(view.History, x => x.Utc == utc.AddSeconds(3001) && x.ReadBytesPerSecond == 1000);
    }
    [Fact]
    public void DownsamplingDoesNotHideUnavailableMeasurementsAndKeepsRawEvidence()
    {
        var ring = new SnapshotRing(); var utc = DateTime.UtcNow;
        for (var i = 0; i < 3600; i++)
            ring.Add(new MonitorSnapshot(i * (long)Stopwatch.Frequency, utc.AddSeconds(i), i == 1013 ? null : 1,
                i == 1013 ? null : 5, i == 1013 ? null : 10, i == 1013 ? null : 20,
                i == 2013 ? null : 10000, i == 3013 ? null : 20000, 0, 0, [], []));
        var history = ring.View(utc, []).History;
        Assert.InRange(history.Count, 1, 2000);
        var rateBlock = history.Where(x => x.MonotonicTick >= 1008L * Stopwatch.Frequency && x.MonotonicTick < 1016L * Stopwatch.Frequency).ToArray();
        Assert.NotEmpty(rateBlock);
        Assert.All(rateBlock, x => {
            Assert.Null(x.IntervalSeconds); Assert.Null(x.CpuPercent);
            Assert.Null(x.ReadBytesPerSecond); Assert.Null(x.WriteBytesPerSecond);
        });
        Assert.Contains(history, x => x.AvailablePhysicalBytes == null);
        Assert.Contains(history, x => x.CommitBytes == null);
        Assert.Equal(5, ring.Snapshots()[1008].CpuPercent);
        Assert.Null(ring.Snapshots()[1013].CpuPercent);
        Assert.Equal(3600, ring.Snapshots().Count);
        Assert.Equal(3599L * Stopwatch.Frequency, history[^1].MonotonicTick);
    }

    [Fact]
    public void DownsamplingKeepsTheFinalBlockUnavailableInsteadOfReintroducingItsEndpoint()
    {
        var ring = new SnapshotRing(); var utc = DateTime.UtcNow;
        for (var i = 0; i < 3600; i++)
            ring.Add(new MonitorSnapshot(i * (long)Stopwatch.Frequency, utc.AddSeconds(i), 1,
                i == 3597 ? null : 5, 10, 20, 10000, 20000, 0, 0, [], []));
        var history = ring.View(utc, []).History;
        Assert.Equal(3599L * Stopwatch.Frequency, history[^1].MonotonicTick);
        Assert.Null(history[^1].CpuPercent);
        Assert.Equal(5, ring.Snapshots()[^1].CpuPercent);
    }

    [Theory]
    [InlineData(-3600)]
    [InlineData(3600)]
    public void WallClockChangeDoesNotAlterMonotonicRates(int clockOffset)
    {
        var calculator = new SnapshotCalculator(2); var utc = DateTime.UtcNow;
        calculator.Calculate(Raw(0, utc, Process(100, 0)));
        var changed = calculator.Calculate(Raw(Stopwatch.Frequency, utc.AddSeconds(1 + clockOffset),
            Process(100, 10_000_000, 100, 200)));
        Assert.Equal(1, changed.IntervalSeconds); Assert.Equal(50, changed.CpuPercent);
        Assert.Equal(100, changed.ReadBytesPerSecond); Assert.Equal(200, changed.WriteBytesPerSecond);
        Assert.Contains(changed.Gaps, x => x.Kind == "ClockChange");
        Assert.DoesNotContain(changed.Gaps, x => x.Kind == "MissedTicks");
    }

    [Fact]
    public void LongPauseInvalidatesRatesAndRecoveryUsesTheResumedBaseline()
    {
        var calculator = new SnapshotCalculator(2); var utc = DateTime.UtcNow;
        calculator.Calculate(Raw(0, utc, Process(100, 0, 0, 0)));
        var resumed = calculator.Calculate(Raw(3601L * Stopwatch.Frequency, utc.AddSeconds(3601),
            Process(100, 50_000_000, 10000, 20000)));
        Assert.Null(resumed.IntervalSeconds); Assert.Null(resumed.CpuPercent);
        Assert.Null(resumed.ReadBytesPerSecond); Assert.Null(resumed.WriteBytesPerSecond);
        Assert.Equal(4096, resumed.AvailablePhysicalBytes);
        Assert.Contains(resumed.Gaps, x => x.Kind == "MissedTicks");
        var recovered = calculator.Calculate(Raw(3602L * Stopwatch.Frequency, utc.AddSeconds(3602),
            Process(100, 60_000_000, 10100, 20200)));
        Assert.Equal(50, recovered.CpuPercent);
        Assert.Equal(100, recovered.ReadBytesPerSecond); Assert.Equal(200, recovered.WriteBytesPerSecond);
        Assert.Empty(recovered.Gaps);
    }

    [Fact]
    public void SamplingFailureRequiresFreshBaselineInsteadOfAssumingZeroActivity()
    {
        var calculator = new SnapshotCalculator(2); var utc = DateTime.UtcNow;
        calculator.Calculate(Raw(0, utc, Process(100, 0)));
        var failed = calculator.Calculate(Raw(Stopwatch.Frequency, utc.AddSeconds(1)) with {
            AvailablePhysicalBytes = null, CommitBytes = null,
            Gaps = [new(utc.AddSeconds(1), "SamplingFailure", "Controlled failure")]
        });
        Assert.Null(failed.CpuPercent); Assert.Null(failed.ReadBytesPerSecond); Assert.Null(failed.AvailablePhysicalBytes);
        var baseline = calculator.Calculate(Raw(2L * Stopwatch.Frequency, utc.AddSeconds(2), Process(100, 20_000_000)));
        Assert.Null(baseline.CpuPercent); Assert.Null(baseline.ReadBytesPerSecond);
        var recovered = calculator.Calculate(Raw(3L * Stopwatch.Frequency, utc.AddSeconds(3), Process(100, 30_000_000)));
        Assert.Equal(50, recovered.CpuPercent);
    }

    [Fact]
    public void DownsamplingKeepsMonotonicOrderAfterWallClockRollback()
    {
        var ring = new SnapshotRing(); var utc = DateTime.UtcNow;
        for (var i = 0; i < 3600; i++)
            ring.Add(new MonitorSnapshot(i * (long)Stopwatch.Frequency, utc.AddSeconds(i >= 1800 ? i - 7200 : i), 1,
                i == 2011 ? 80 : 5, 1, 1, 10000, 20000, 0, 0, [], []));
        var view = ring.View(utc, []);
        Assert.Equal(0, view.History[0].MonotonicTick);
        Assert.Equal(3599L * Stopwatch.Frequency, view.History[^1].MonotonicTick);
        Assert.True(view.History.Zip(view.History.Skip(1)).All(pair => pair.First.MonotonicTick < pair.Second.MonotonicTick));
        Assert.Contains(view.History, x => x.CpuPercent == 80);
    }
    [Fact]
    public void SessionStatisticsKeepFailuresAfterEvidenceEviction()
    {
        var utc = DateTime.UtcNow;
        var source = new SequenceSampler([
            Raw(0, utc, Process(100, 0)),
            Raw(4L * Stopwatch.Frequency, utc.AddSeconds(4), Process(100, 1)),
            Raw(5L * Stopwatch.Frequency, utc.AddSeconds(5)) with {
                Gaps = [new(utc.AddSeconds(5), "SamplingFailure", "Controlled failure")]
            },
            Raw(6L * Stopwatch.Frequency, utc.AddSeconds(6), Process(100, 1)),
            Raw(7L * Stopwatch.Frequency, utc.AddSeconds(7), Process(100, 2))
        ]);
        var session = new MonitorSession(source, 2, new(maximumSnapshots: 2));
        for (var i = 0; i < 5; i++) session.SampleOnce();
        var view = session.View();
        Assert.Equal(2, view.SnapshotCount);
        Assert.DoesNotContain(view.RecentGaps, x => x.Kind is "MissedTicks" or "SamplingFailure");
        Assert.Equal(5, view.Statistics!.TotalSnapshots);
        Assert.Equal(1, view.Statistics.MissedTickGaps);
        Assert.Equal(1, view.Statistics.SamplingFailures);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellingAnActiveSampleDoesNotPublishFailureOrPartialSnapshot(bool throwOnCancellation)
    {
        using var cancellation = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new DelegateSampler(token => {
            using var registration = token.Register(release.Set);
            entered.SetResult(token);
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Controlled sampler was not released.");
            if (throwOnCancellation) token.ThrowIfCancellationRequested();
            return Raw(Stopwatch.GetTimestamp(), DateTime.UtcNow, Process(100, 0));
        });
        var session = new MonitorSession(source, 2);
        var running = Task.Run(() => session.Run(cancellation.Token));
        try {
            Assert.Equal(cancellation.Token, await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
            var view = session.View();
            Assert.Equal(0, view.SnapshotCount);
            Assert.Empty(view.History);
            Assert.Null(view.Descriptor);
            Assert.Equal(0, view.Statistics!.TotalSnapshots);
            Assert.Equal(0, view.Statistics.SamplingFailures);
            Assert.DoesNotContain(view.RecentGaps, x => x.Kind == "SamplingFailure");
        } finally {
            release.Set(); cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionStillPublishesGenuineSamplingFailureAsGap(bool unexpectedCancellation)
    {
        var session = new MonitorSession(new DelegateSampler(_ => {
            if (unexpectedCancellation) throw new OperationCanceledException("Controlled failure");
            throw new IOException("Controlled failure");
        }), 2);
        session.SampleOnce();
        var view = session.View();
        Assert.Equal(1, view.Statistics!.TotalSnapshots);
        Assert.Equal(1, view.Statistics.SamplingFailures);
        Assert.Contains(view.RecentGaps, x => x.Kind == "SamplingFailure" && x.Detail.Contains("Controlled failure"));
    }

    [Fact]
    public void NativeSamplerRejectsPreCancelledRequest()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => new WindowsSampler().Sample(cancellation.Token));
    }

    private sealed class DelegateSampler(Func<CancellationToken, RawSnapshot> sample) : IRawSampler
    {
        public RawSnapshot Sample(CancellationToken token = default) => sample(token);
    }

    private sealed class SequenceSampler(IEnumerable<RawSnapshot> samples) : IRawSampler
    {
        private readonly Queue<RawSnapshot> remaining = new(samples);
        public RawSnapshot Sample(CancellationToken token = default) {
            token.ThrowIfCancellationRequested();
            return remaining.Dequeue();
        }
    }

    [Fact]
    public void NativeSamplingDurationIsPreservedAndUnknownDurationStaysUnavailable()
    {
        var calculator = new SnapshotCalculator(2); var utc = DateTime.UtcNow; var ring = new SnapshotRing();
        ring.Add(calculator.Calculate(Raw(0, utc, Process(100, 0))));
        Assert.Null(ring.View(utc, []).Diagnostics!.SamplingP95Ms);
        ring.Add(calculator.Calculate(Raw(Stopwatch.Frequency, utc.AddSeconds(1), Process(100, 1)) with { SamplingDurationMs = 12.5 }));
        Assert.Equal(12.5, ring.View(utc, []).Diagnostics!.SamplingP95Ms);
        Assert.Equal(12.5, ring.View(utc, []).History[^1].SamplingDurationMs);
    }
}
