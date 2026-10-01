using System.Globalization;

namespace Woi.Core;

public static class InvestigationAnalyzer
{
    public const string Version = "snapshot-analysis-1";
    public const int MaximumSnapshots = 3600, MaximumRows = 250000;
    public static InvestigationDocument Analyze(SourceDescriptor source, IReadOnlyList<MonitorSnapshot> snapshots,
        InvestigationRequest request, string? id = null)
    {
        Validate(source, snapshots, request);
        var window = request.Window;
        var settings = request.Settings ?? new InvestigationSettings();
        var selected = snapshots.Where(x => PointInside(source, x, window) || Overlap(source, x, window) > 0).ToArray();
        var metrics = Measure(source, selected, window, request.ProcessLifetimeId);
        var priorWindow = new InvestigationWindow(Math.Max(0, window.StartSeconds - 30), window.StartSeconds);
        var prior = priorWindow.DurationSeconds > 0 ? Measure(source, snapshots, priorWindow, request.ProcessLifetimeId) : null;
        var limitations = new List<string> {
            "One-second snapshots can miss short-lived processes and brief activity. Recorded rates cover readable counters only.",
            "The preceding interval is a comparison interval; it has not been confirmed healthy.",
            "Representative snapshot references accompany findings; saved evidence contains every retained input sample used."
        };
        var partialEdges = selected.Any(x => Overlap(source, x, window) > 0 && Overlap(source, x, window) < (x.IntervalSeconds ?? 0) - .00001);
        if (partialEdges) limitations.Add("Partial counter-interval edges use proportional estimates, assuming activity was spread evenly within each measured interval. Snapshots cannot resolve the exact subsecond distribution.");
        if (selected.Any(x => x.UnreadableProcesses > 0)) limitations.Add("Some enumerated processes were unreadable. Accounted activity is incomplete.");
        foreach (var gap in selected.SelectMany(x => x.Gaps).GroupBy(x => x.Kind))
            limitations.Add($"{gap.Key}: {gap.Count()} recorded occurrence(s); {gap.Last().Detail}");
        if (metrics.CpuCoveredSeconds < window.DurationSeconds * .8) limitations.Add("Less than 80% of the selected duration has usable CPU-rate coverage.");
        if (prior is null || Math.Max(prior.CpuCoveredSeconds, Math.Max(prior.ReadCoveredSeconds, prior.WriteCoveredSeconds)) < 3)
            limitations.Add("A usable preceding rate interval of at least three seconds is absent.");
        var contributors = Contributors(source, selected, window, request.ProcessLifetimeId);
        var scope = request.ProcessLifetimeId == null ? "All readable processes" :
            selected.SelectMany(x => x.Processes).FirstOrDefault(x => x.LifetimeId == request.ProcessLifetimeId)?.Name ?? "Selected lifetime (unavailable in selection)";
        var references = selected.Where(x => Overlap(source, x, window) > 0 && (request.ProcessLifetimeId == null || Process(x, request.ProcessLifetimeId) != null)).TakeLast(6)
            .Select(x => new SnapshotReference(source.Id, x.MonotonicTick, x.Utc, request.ProcessLifetimeId)).ToArray();
        var gaugeReferences = selected.Where(x => PointInside(source, x, window) && x.AvailablePhysicalBytes != null).TakeLast(6)
            .Select(x => new SnapshotReference(source.Id, x.MonotonicTick, x.Utc)).ToArray();
        var findings = new List<Finding>();
        if (metrics.MeanCpuPercent is { } cpu) {
            var leader = contributors.Where(x => x.CpuSeconds > 0).OrderByDescending(x => x.CpuSeconds).FirstOrDefault();
            var sustained = SustainedCpu(source, selected, window, request.ProcessLifetimeId, settings.CpuThreshold);
            var statement = FormattableString.Invariant($"{scope} averaged {cpu:F1}% of machine CPU capacity over {metrics.CpuCoveredSeconds:F2} measured seconds ({metrics.CpuSeconds:F3} CPU-seconds).");
            if (partialEdges) statement += " Partial interval edges are proportional estimates.";
            if (leader != null) statement += FormattableString.Invariant($" {leader.Name} (PID {leader.Pid}) accounted for {leader.CpuSeconds:F3} CPU-seconds in its recorded lifetime.");
            if (sustained >= settings.SustainedSeconds) statement += FormattableString.Invariant($" The configured {settings.CpuThreshold:g}% / {settings.SustainedSeconds:g}-second heuristic was met for a longest observed run of {sustained:F2}s.");
            findings.Add(new("cpu", "Recorded CPU activity", statement, leader == null ? Provenance.Observed : Provenance.ResourceAttribution,
                [new("Accounted CPU", cpu, "% of machine capacity", metrics.CpuCoveredSeconds, source.Id),
                 new("Accounted CPU time", metrics.CpuSeconds, "CPU-seconds", metrics.CpuCoveredSeconds, source.Id)],
                leader == null ? references : [leader.Evidence], ["Two matching-lifetime CPU counters", "Monotonic elapsed time", "Recorded logical processor count"],
                ["This attribution does not establish why another application was delayed.", "CPU-time utilization differs from frequency-adjusted CPU utility."],
                cpu < 10 && request.ProcessLifetimeId != null ? "For a persistent application delay, obtain a scheduling/wait trace using an external WPR profile; the built-in profiles do not diagnose waits." :
                "Inspect the leading lifetime and, if more detail is needed, record a short CPU trace during another occurrence."));
        }
        if (metrics.AvailableChangeBytes is { } change) {
            var statement = FormattableString.Invariant($"System available physical memory changed by {change / 1048576d:F2} MiB between recorded endpoints; its observed minimum was {metrics.MinimumAvailableBytes / 1048576d:F2} MiB.");
            if (metrics.CommitChangeBytes is { } commit) statement += FormattableString.Invariant($" System commit changed by {commit / 1048576d:F2} MiB.");
            if (metrics.PeakPrivateBytes is { } privateBytes) statement += FormattableString.Invariant($" The selected process's observed peak private allocation was {privateBytes / 1048576d:F2} MiB.");
            findings.Add(new("memory", "Recorded memory conditions", statement, Provenance.Observed,
                [new("Minimum available physical memory", metrics.MinimumAvailableBytes, "bytes", 0, source.Id),
                 new("Available-memory endpoint change", change, "bytes", 0, source.Id)], gaugeReferences,
                ["At least two recorded available-memory snapshots"],
                ["Memory gauges are point observations, not integrated allocations.", "High private allocation or falling available memory alone does not establish a memory shortage."],
                "If the symptom repeats, record memory and hard-fault evidence and check whether page-in activity overlaps the delay."));
        }
        if (metrics.ReadBytes != null || metrics.WriteBytes != null) {
            var statement = $"{scope} accounted for {Bytes(metrics.ReadBytes)} read and {Bytes(metrics.WriteBytes)} written across the selected measured counter intervals.";
            if (partialEdges) statement += " Partial interval edges are proportional estimates.";
            if (prior is { ReadCoveredSeconds: >= 3 } && metrics.ReadCoveredSeconds >= 3 && prior.ReadBytes is { } priorRead && metrics.ReadBytes is { } read)
                statement += FormattableString.Invariant($" Accounted read rate was {read / metrics.ReadCoveredSeconds / 1048576d:F2} MiB/s during selection versus {priorRead / prior.ReadCoveredSeconds / 1048576d:F2} MiB/s in the preceding interval.");
            findings.Add(new("io", "Recorded process I/O accounting", statement, Provenance.Observed,
                [new("Accounted reads", metrics.ReadBytes, "bytes", metrics.ReadCoveredSeconds, source.Id),
                 new("Accounted writes", metrics.WriteBytes, "bytes", metrics.WriteCoveredSeconds, source.Id)], references,
                ["Two matching-lifetime I/O counters", "Valid monotonic rate interval"],
                ["Process I/O counters include accounting activity; they do not measure physical disk transfers, saturation, or file-specific operations."],
                "Use a resource/file ETL recording to distinguish physical disk transfers from logical operations and inspect recorded latency."));
        }
        var next = findings.FirstOrDefault()?.NextMeasurement ??
            "Start observing before reproducing the symptom, then select an interval with readable snapshots or import an existing ETL with the required coverage.";
        var investigation = new Investigation(id ?? Guid.NewGuid().ToString("N"), request.Symptom.Trim(), window,
            request.ProcessLifetimeId, scope, request.Notes, metrics, prior, findings.Take(3).ToArray(), contributors,
            limitations.Distinct().ToArray(), next, settings);
        return new(1, source, investigation, snapshots);
    }
    private static string Bytes(double? value) => value is { } bytes ? (bytes / 1048576d).ToString("F2", CultureInfo.InvariantCulture) + " MiB" : "unavailable bytes";
    public static double Time(SourceDescriptor source, MonitorSnapshot snapshot) => (snapshot.MonotonicTick - source.StartTick) / (double)source.MonotonicFrequency;
    private static bool PointInside(SourceDescriptor source, MonitorSnapshot snapshot, InvestigationWindow window) =>
        Time(source, snapshot) >= window.StartSeconds && Time(source, snapshot) <= window.EndSeconds;
    private static double Overlap(SourceDescriptor source, MonitorSnapshot snapshot, InvestigationWindow window)
    {
        if (snapshot.IntervalSeconds is not (>= .05 and <= 2.5)) return 0;
        var end = Time(source, snapshot);
        return Math.Max(0, Math.Min(end, window.EndSeconds) - Math.Max(end - snapshot.IntervalSeconds.Value, window.StartSeconds));
    }
    private static MonitorProcess? Process(MonitorSnapshot value, string? id) => id == null ? null : value.Processes.FirstOrDefault(x => x.LifetimeId == id);
    private static double? Cpu(MonitorSnapshot value, string? id) => id == null ? value.CpuPercent : Process(value, id)?.CpuPercent;
    private static double? Read(MonitorSnapshot value, string? id) => id == null ? value.ReadBytesPerSecond : Process(value, id)?.ReadBytesPerSecond;
    private static double? Write(MonitorSnapshot value, string? id) => id == null ? value.WriteBytesPerSecond : Process(value, id)?.WriteBytesPerSecond;
    private static InvestigationMetrics Measure(SourceDescriptor source, IReadOnlyList<MonitorSnapshot> snapshots, InvestigationWindow window, string? processId)
    {
        double cpu = 0, read = 0, write = 0, cpuCoverage = 0, readCoverage = 0, writeCoverage = 0;
        foreach (var sample in snapshots) {
            var overlap = Overlap(source, sample, window);
            if (Cpu(sample, processId) is { } c) { cpu += c * overlap; cpuCoverage += overlap; }
            if (Read(sample, processId) is { } r) { read += r * overlap; readCoverage += overlap; }
            if (Write(sample, processId) is { } w) { write += w * overlap; writeCoverage += overlap; }
        }
        var gauges = snapshots.Where(x => PointInside(source, x, window)).ToArray();
        var available = gauges.Where(x => x.AvailablePhysicalBytes != null).ToArray();
        var commit = gauges.Where(x => x.CommitBytes != null).ToArray();
        var privateBytes = processId == null ? [] : gauges.Select(x => Process(x, processId)?.PrivateBytes).OfType<long>().ToArray();
        return new(window.DurationSeconds, cpuCoverage, cpuCoverage > 0 ? cpu / cpuCoverage : null,
            cpuCoverage > 0 ? cpu / 100 * source.LogicalProcessors : null,
            readCoverage, readCoverage > 0 ? read : null, writeCoverage, writeCoverage > 0 ? write : null,
            available.Length > 0 ? available.Min(x => x.AvailablePhysicalBytes) : null,
            available.Length > 1 ? available[^1].AvailablePhysicalBytes - available[0].AvailablePhysicalBytes : null,
            commit.Length > 1 ? commit[^1].CommitBytes - commit[0].CommitBytes : null,
            privateBytes.Length > 0 ? privateBytes.Max() : null, available.Length, privateBytes.Length);
    }
    private static IReadOnlyList<InvestigationContributor> Contributors(SourceDescriptor source, IReadOnlyList<MonitorSnapshot> samples,
        InvestigationWindow window, string? processId)
    {
        var rows = samples.SelectMany(s => s.Processes.Where(p => processId == null || p.LifetimeId == processId)
            .Select(p => (Snapshot: s, Process: p, Overlap: Overlap(source, s, window))))
            .Where(x => x.Overlap > 0 || PointInside(source, x.Snapshot, window));
        return rows.GroupBy(x => x.Process.LifetimeId).Select(group => {
            var data = group.ToArray(); var process = data[0].Process;
            double? Total(Func<MonitorProcess, double?> measure) => data.Any(x => x.Overlap > 0 && measure(x.Process) != null)
                ? data.Sum(x => (measure(x.Process) ?? 0) * x.Overlap) : null;
            var memory = data.Where(x => PointInside(source, x.Snapshot, window)).Select(x => x.Process.PrivateBytes).OfType<long>().ToArray();
            var evidence = data.MaxBy(x => x.Process.CpuPercent ?? -1).Snapshot;
            return new InvestigationContributor(process.LifetimeId, process.Pid, process.Name,
                Total(x => x.CpuPercent) * source.LogicalProcessors / 100, Total(x => x.ReadBytesPerSecond), Total(x => x.WriteBytesPerSecond),
                memory.Length > 0 ? memory.Max() : null, new(source.Id, evidence.MonotonicTick, evidence.Utc, process.LifetimeId));
        }).OrderByDescending(x => x.CpuSeconds ?? -1).ThenByDescending(x => (x.ReadBytes ?? 0) + (x.WriteBytes ?? 0)).Take(20).ToArray();
    }
    private static double SustainedCpu(SourceDescriptor source, IReadOnlyList<MonitorSnapshot> samples, InvestigationWindow window, string? processId, double threshold)
    {
        double run = 0, longest = 0, previousEnd = double.NaN;
        foreach (var sample in samples) {
            var duration = Overlap(source, sample, window); var end = Time(source, sample);
            if (duration <= 0 || Cpu(sample, processId) < threshold || Cpu(sample, processId) == null || Math.Abs(end - (sample.IntervalSeconds ?? 0) - previousEnd) > .05) run = 0;
            if (duration > 0 && Cpu(sample, processId) >= threshold) run += duration;
            longest = Math.Max(longest, run); previousEnd = end;
        }
        return longest;
    }
    public static void Validate(SourceDescriptor source, IReadOnlyList<MonitorSnapshot> snapshots, InvestigationRequest request)
    {
        var settings = request?.Settings ?? new InvestigationSettings();
        if (!double.IsFinite(settings.CpuThreshold) || settings.CpuThreshold is <= 0 or > 100 || !double.IsFinite(settings.SustainedSeconds) || settings.SustainedSeconds is < 1 or > 120)
            throw new WoiException("InvalidSettings", "Use a CPU threshold above zero and at most 100%, and a sustained duration of 1–120 seconds.");
        if (source == null || source.Kind != "WindowsApiSnapshot" || source.Version != Version ||
            string.IsNullOrWhiteSpace(source.Id) || source.Id.Length > 100 || source.MonotonicFrequency is < 1 or > 10_000_000_000 || source.LogicalProcessors is < 1 or > 4096 || source.StartTick < 0)
            throw new WoiException("UnsupportedSource", "The recorded snapshot source or rate definition is unsupported.");
        if (request == null || request.Window == null || !double.IsFinite(request.Window.StartSeconds) || !double.IsFinite(request.Window.EndSeconds) ||
            request.Window.StartSeconds < 0 || request.Window.EndSeconds <= request.Window.StartSeconds || request.Window.DurationSeconds > 3600 ||
            string.IsNullOrWhiteSpace(request.Symptom) || request.Symptom.Length > 200 || request.Notes == null || request.Notes.Length > 4000)
            throw new WoiException("InvalidInvestigation", "Choose a retained interval up to 60 minutes, a symptom of 1–200 characters, and notes up to 4,000 characters.");
        if (snapshots == null || snapshots.Count is < 1 or > MaximumSnapshots || snapshots.Sum(x => (long)x.Processes.Count) > MaximumRows)
            throw new WoiException("EvidenceLimit", "Snapshot evidence exceeds the supported recording bounds or is absent.");
        long previous = long.MinValue; double previousRateEnd = double.NegativeInfinity;
        HashSet<string>? previousLifetimes = null;
        foreach (var sample in snapshots) {
            if (sample.MonotonicTick <= previous || sample.Processes.Count > 250000 || sample.EnumeratedProcesses < 0 || sample.UnreadableProcesses < 0 ||
                sample.Processes.Select(x => x.LifetimeId).Distinct().Count() != sample.Processes.Count)
                throw new WoiException("InvalidEvidence", "Snapshot ordering or lifetime identities are invalid.");
            var time = Time(source, sample);
            if (!double.IsFinite(time) || time < 0 || (sample.IntervalSeconds is { } seconds && (!double.IsFinite(seconds) || seconds is < .05 or > 2.5 || time - seconds < previousRateEnd - .05)))
                throw new WoiException("InvalidEvidence", "Snapshot rate intervals overlap or are invalid.");
            previous = sample.MonotonicTick; previousRateEnd = time;
            Check(sample.CpuPercent); Check(sample.ReadBytesPerSecond); Check(sample.WriteBytesPerSecond);
            if (sample.AvailablePhysicalBytes < 0 || sample.CommitBytes < 0) throw new WoiException("InvalidEvidence", "A recorded memory gauge is negative.");
            foreach (var process in sample.Processes) {
                if (string.IsNullOrWhiteSpace(process.LifetimeId) || process.LifetimeId.Length > 100 || process.Name.Length > 300 || process.CreationFileTime <= 0 || process.Pid < 0 || process.SessionId < 0 ||
                    process.LifetimeId != $"{process.SessionId}:{process.Pid}:{process.CreationFileTime}")
                    throw new WoiException("InvalidEvidence", "A process lifetime identity is unresolved or invalid.");
                Check(process.CpuPercent); Check(process.ReadBytesPerSecond); Check(process.WriteBytesPerSecond);
                if (sample.IntervalSeconds == null && (process.CpuPercent != null || process.ReadBytesPerSecond != null || process.WriteBytesPerSecond != null) ||
                    previousLifetimes != null && !previousLifetimes.Contains(process.LifetimeId) && (process.CpuPercent != null || process.ReadBytesPerSecond != null || process.WriteBytesPerSecond != null))
                    throw new WoiException("InvalidEvidence", "A rate lacks a recorded matching-lifetime baseline.");
            }
            previousLifetimes = sample.Processes.Select(x => x.LifetimeId).ToHashSet(StringComparer.Ordinal);
        }
        if (request.Window.StartSeconds < Time(source, snapshots[0]) - .01 || request.Window.EndSeconds > Time(source, snapshots[^1]) + .01)
            throw new WoiException("HistoryUnavailable", "The requested interval is outside retained snapshots. Select the visible retained history.");
        if (request.ProcessLifetimeId != null && !snapshots.Any(x => x.Processes.Any(p => p.LifetimeId == request.ProcessLifetimeId)))
            throw new WoiException("UnknownLifetime", "The selected lifetime was not observed in this source.");
        static void Check(double? value) { if (value is { } n && (!double.IsFinite(n) || n < 0 || n > 1e25)) throw new WoiException("InvalidEvidence", "A recorded rate is invalid."); }
    }
}
