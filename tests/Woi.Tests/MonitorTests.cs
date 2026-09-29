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
}
