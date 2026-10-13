using Woi.Core;
using Woi.Desktop;
using Xunit;

namespace Woi.Tests;

public sealed class MonitorChartTests
{
    private static readonly DateTime Utc = new(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc);
    private static MonitorPoint Point(long tick, double? interval = 1) =>
        new(Utc.AddSeconds(tick), interval, 5, 10, 20, 4096, 8192, 1, 0, tick);

    [Fact]
    public void MemoryChartBreaksAcrossCollectionPauseWithoutDiscardingGauges()
    {
        var points = new[] { Point(0, null), Point(1), Point(3601, null), Point(3602) };
        var (x, y) = MonitorChartSeries.Create(points, null, Utc, p => p.AvailablePhysicalBytes ?? double.NaN);
        Assert.Equal(5, x.Length); Assert.Equal(x.Length, y.Length);
        Assert.True(double.IsNaN(x[2])); Assert.True(double.IsNaN(y[2]));
        Assert.Equal(new double[] { 0, 1, 3601, 3602 }, x.Where(double.IsFinite));
        Assert.Equal(new double[] { 4096, 4096, 4096, 4096 }, y.Where(double.IsFinite));
    }

    [Fact]
    public void CombinedIoRemainsUnavailableWhenEitherCounterIsMissing()
    {
        Assert.True(double.IsNaN(MonitorChartSeries.IoMiBPerSecond(Point(0) with { ReadBytesPerSecond = null })));
        Assert.True(double.IsNaN(MonitorChartSeries.IoMiBPerSecond(Point(0) with { WriteBytesPerSecond = null })));
        Assert.Equal(0, MonitorChartSeries.IoMiBPerSecond(Point(0) with { ReadBytesPerSecond = 0, WriteBytesPerSecond = 0 }));
        Assert.Equal(3, MonitorChartSeries.IoMiBPerSecond(Point(0) with { ReadBytesPerSecond = 1048576, WriteBytesPerSecond = 2097152 }));
    }

    [Fact]
    public void ChartUsesRecordedMonotonicClockAndPreservesUnknownAndZeroValues()
    {
        var source = new SourceDescriptor("test", "WindowsApiSnapshot", InvestigationAnalyzer.Version,
            "recorded", "recorded", 2, 1000, 10000, Utc);
        var points = new[] {
            Point(10) with { MonotonicTick = 10000, CpuPercent = null },
            Point(11) with { MonotonicTick = 11000, CpuPercent = 0 },
            Point(12) with { MonotonicTick = 12000, Utc = Utc.AddHours(-1), CpuPercent = 5 }
        };
        var (x, y) = MonitorChartSeries.Create(points, source, Utc, p => p.CpuPercent ?? double.NaN);
        Assert.Equal(new double[] { 0, 1, 2 }, x);
        Assert.True(double.IsNaN(y[0])); Assert.Equal(0, y[1]); Assert.Equal(5, y[2]);
        Assert.Empty(MonitorChartSeries.Create([], source, Utc, _ => 0).X);
    }
}
