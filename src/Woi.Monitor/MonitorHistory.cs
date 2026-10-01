using System.Diagnostics;
using Woi.Core;

namespace Woi.Monitor;

public record RawProcess(int Pid, int SessionId, long CreationFileTime, string Name,
    long CpuTime100ns, ulong? ReadBytes, ulong? WriteBytes, long? PrivateBytes, long? WorkingSetBytes);
public record RawSnapshot(long Tick, DateTime Utc, long? AvailablePhysicalBytes, long? CommitBytes,
    int EnumeratedProcesses, int UnreadableProcesses, IReadOnlyList<RawProcess> Processes,
    IReadOnlyList<ObservationGap> Gaps, double? SamplingDurationMs = null);
public interface IRawSampler { RawSnapshot Sample(); }

public sealed class SnapshotCalculator(int logicalProcessors)
{
    private RawSnapshot? previous;
    private Dictionary<(int Session, int Pid, long Creation), (string Id, string Name)> identityStrings = [];
    public MonitorSnapshot Calculate(RawSnapshot raw)
    {
        var gaps = raw.Gaps.ToList();
        var earlier = previous;
        previous = raw;
        var elapsed = earlier == null ? 0 : (raw.Tick - earlier.Tick) / (double)Stopwatch.Frequency;
        var validInterval = elapsed >= .05 && elapsed <= 2.5;
        if (earlier != null && !validInterval)
            gaps.Add(new(raw.Utc, elapsed > 2.5 ? "MissedTicks" : "InvalidInterval",
                $"Elapsed monotonic interval was {elapsed:F3} seconds; rates are unavailable."));
        if (earlier != null && validInterval &&
            Math.Abs((raw.Utc - earlier.Utc).TotalSeconds - elapsed) > 2)
            gaps.Add(new(raw.Utc, "ClockChange", "UTC moved relative to monotonic time; rates use monotonic time."));
        if (logicalProcessors <= 0)
            gaps.Add(new(raw.Utc, "ProcessorTopology", "Logical processor count is unavailable; CPU percentages are unavailable."));

        var previousById = earlier?.Processes.ToDictionary(Key) ?? [];
        var nextIdentityStrings = new Dictionary<(int Session, int Pid, long Creation), (string Id, string Name)>();
        var rows = new List<MonitorProcess>(raw.Processes.Count);
        double systemCpu = 0;
        var knownCpu = 0;
        foreach (var item in raw.Processes) {
            var key = Key(item);
            identityStrings.TryGetValue(key, out var knownStrings);
            var id = knownStrings.Id ?? Identity(item);
            var name = string.Equals(knownStrings.Name, item.Name, StringComparison.Ordinal) ? knownStrings.Name! : item.Name;
            nextIdentityStrings[key] = (id, name);
            previousById.TryGetValue(key, out var before);
            var hasPrevious = validInterval && before != null;
            double? cpu = null, read = null, write = null;
            if (hasPrevious && before != null) {
                var delta = item.CpuTime100ns - before.CpuTime100ns;
                if (delta >= 0 && logicalProcessors > 0) {
                    cpu = delta / 10_000_000d / elapsed / logicalProcessors * 100;
                    systemCpu += cpu.Value; knownCpu++;
                } else gaps.Add(new(raw.Utc, "CounterReset", $"CPU time for PID {item.Pid} decreased or processor topology is unsupported."));
                read = Rate(item.ReadBytes, before.ReadBytes, elapsed, raw.Utc, item.Pid, "read", gaps);
                write = Rate(item.WriteBytes, before.WriteBytes, elapsed, raw.Utc, item.Pid, "write", gaps);
            }
            rows.Add(new(id, item.Pid, item.SessionId, item.CreationFileTime, name, cpu, read, write,
                item.PrivateBytes, item.WorkingSetBytes));
        }
        identityStrings = nextIdentityStrings;
        // Totals cover readable process counters; unavailable counters remain excluded.
        double? totalRead = rows.Any(x => x.ReadBytesPerSecond != null) ? rows.Sum(x => x.ReadBytesPerSecond ?? 0) : null;
        double? totalWrite = rows.Any(x => x.WriteBytesPerSecond != null) ? rows.Sum(x => x.WriteBytesPerSecond ?? 0) : null;
        // This is accounted process CPU, not a machine-wide utilization counter.
        return new(raw.Tick, raw.Utc, validInterval ? elapsed : null, knownCpu > 0 ? systemCpu : null, totalRead, totalWrite, raw.AvailablePhysicalBytes,
            raw.CommitBytes, raw.EnumeratedProcesses, raw.UnreadableProcesses, rows, gaps, raw.SamplingDurationMs);
    }
    private static double? Rate(ulong? current, ulong? prior, double elapsed, DateTime utc, int pid,
        string name, List<ObservationGap> gaps)
    {
        if (current == null || prior == null) return null;
        if (current < prior) {
            gaps.Add(new(utc, "CounterReset", $"Process {name} byte counter for PID {pid} decreased."));
            return null;
        }
        return (current.Value - prior.Value) / elapsed;
    }
    private static (int Session, int Pid, long Creation) Key(RawProcess value) =>
        (value.SessionId, value.Pid, value.CreationFileTime);
    public static string Identity(RawProcess value) =>
        $"{value.SessionId}:{value.Pid}:{value.CreationFileTime}";
}

public sealed class SnapshotRing(int maximumSnapshots = 3600, int maximumRows = 250000,
    long maximumBytes = 64L * 1024 * 1024)
{
    private readonly Queue<MonitorSnapshot> snapshots = new();
    private int rows;
    private long estimatedBytes;
    public void Add(MonitorSnapshot value)
    {
        snapshots.Enqueue(value);
        rows += value.Processes.Count;
        estimatedBytes += Estimate(value);
        while (snapshots.Count > maximumSnapshots || rows > maximumRows || estimatedBytes > maximumBytes) {
            var removed = snapshots.Dequeue();
            rows -= removed.Processes.Count;
            estimatedBytes -= Estimate(removed);
        }
    }
    public IReadOnlyList<MonitorSnapshot> Snapshots() => snapshots.ToArray();
    public IReadOnlyList<MonitorLifetime> Lifetimes(int limit = 500)
    {
        var values = snapshots.ToArray();
        if (values.Length == 0) return [];
        var present = values[^1].Processes.Select(x => x.LifetimeId).ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, LifetimeAccumulator>(StringComparer.Ordinal);
        foreach (var snapshot in values)
            foreach (var process in snapshot.Processes) {
                if (!found.TryGetValue(process.LifetimeId, out var lifetime))
                    found[process.LifetimeId] = lifetime = new(process, snapshot.Utc);
                lifetime.Last = snapshot.Utc;
                lifetime.Count++;
            }
        return found.Values.OrderByDescending(x => x.Last).Take(limit).Select(x =>
            new MonitorLifetime(x.Process.LifetimeId, x.Process.Pid, x.Process.SessionId,
                x.Process.CreationFileTime, x.Process.Name, x.First, x.Last,
                present.Contains(x.Process.LifetimeId), x.Count)).ToArray();
    }
    private sealed class LifetimeAccumulator(MonitorProcess process, DateTime first)
    {
        public MonitorProcess Process { get; } = process;
        public DateTime First { get; } = first;
        public DateTime Last { get; set; } = first;
        public int Count { get; set; }
    }
    private static long Estimate(MonitorSnapshot value) =>
        160L + value.Processes.Sum(x => 192L + 2L * (x.Name.Length + x.LifetimeId.Length)) +
        value.Gaps.Sum(x => 80L + 2L * (x.Kind.Length + x.Detail.Length));
    public MonitorView View(DateTime startedUtc, IReadOnlyList<MonitorMark> marks)
    {
        var values = snapshots.ToArray();
        var latest = values.LastOrDefault();
        var retained = values.Length < 2 ? 0 :
            (values[^1].MonotonicTick - values[0].MonotonicTick) / (double)Stopwatch.Frequency;
        var durations = values.Select(x => x.SamplingDurationMs).OfType<double>().Order().ToArray();
        double? Percentile(double p) => durations.Length > 0 ? durations[Math.Max(0, (int)Math.Ceiling(durations.Length * p) - 1)] : null;
        return new(startedUtc, latest?.Utc, retained, values.Length, rows, estimatedBytes,
            Downsample(values.Select(x => new MonitorPoint(x.Utc, x.IntervalSeconds, x.CpuPercent, x.ReadBytesPerSecond, x.WriteBytesPerSecond, x.AvailablePhysicalBytes,
                x.CommitBytes, x.EnumeratedProcesses, x.UnreadableProcesses, x.MonotonicTick, x.SamplingDurationMs)).ToArray()),
            latest?.Processes.OrderByDescending(x => x.CpuPercent ?? -1)
                .ThenByDescending(x => x.PrivateBytes ?? -1).Take(20).ToArray() ?? [],
            marks.Where(x => values.Length > 0 && x.MonotonicTick >= values[0].MonotonicTick).ToArray(),
            values.SelectMany(x => x.Gaps).GroupBy(x => x.Kind).Select(group => {
                var latest = group.Last();
                return new ObservationGap(latest.Utc, latest.Kind,
                    $"{group.Count()} occurrence(s) in retained history; latest: {latest.Detail}");
            }).OrderByDescending(x => x.Utc).Take(10).ToArray(), Diagnostics: new(Percentile(.5), Percentile(.95), Percentile(1),
                values.Sum(x => x.Gaps.Count(g => g.Kind == "MissedTicks"))));
    }
    private static IReadOnlyList<MonitorPoint> Downsample(IReadOnlyList<MonitorPoint> values)
    {
        if (values.Count <= 2000) return values;
        var step = (int)Math.Ceiling(values.Count / 475d);
        var result = new List<MonitorPoint>(2000);
        for (var i = 0; i < values.Count; i += step) {
            var group = values.Skip(i).Take(step).ToArray();
            result.Add(group[0]);
            var cpuPeak = group.MaxBy(x => x.CpuPercent ?? -1)!;
            if (cpuPeak != group[0]) result.Add(cpuPeak);
            var memoryLow = group.MinBy(x => x.AvailablePhysicalBytes ?? long.MaxValue)!;
            if (memoryLow != group[0] && memoryLow != cpuPeak) result.Add(memoryLow);
            var ioPeak = group.MaxBy(x => x.ReadBytesPerSecond == null && x.WriteBytesPerSecond == null
                ? -1 : (x.ReadBytesPerSecond ?? 0) + (x.WriteBytesPerSecond ?? 0))!;
            if (ioPeak != group[0] && ioPeak != cpuPeak && ioPeak != memoryLow) result.Add(ioPeak);
        }
        if (result[^1] != values[^1]) result.Add(values[^1]);
        return result.OrderBy(x => x.MonotonicTick).Take(2000).ToArray();
    }
}
