using Woi.Core;

namespace Woi.Desktop;

internal static class MonitorChartSeries
{
    public static double IoMiBPerSecond(MonitorPoint point) =>
        point.ReadBytesPerSecond is { } read && point.WriteBytesPerSecond is { } write
            ? (read + write) / 1048576d : double.NaN;

    public static (double[] X, double[] Y) Create(IReadOnlyList<MonitorPoint> points,
        SourceDescriptor? source, DateTime startedUtc, Func<MonitorPoint, double> measure)
    {
        var x = new List<double>(points.Count);
        var y = new List<double>(points.Count);
        foreach (var point in points) {
            // Memory gauges survive a pause, but no line should imply measurements during it.
            if (x.Count > 0 && point.IntervalSeconds == null) { x.Add(double.NaN); y.Add(double.NaN); }
            x.Add(source is { } descriptor
                ? (point.MonotonicTick - descriptor.StartTick) / (double)descriptor.MonotonicFrequency
                : (point.Utc - startedUtc).TotalSeconds);
            y.Add(measure(point));
        }
        return (x.ToArray(), y.ToArray());
    }
}
