namespace Woi.Core;

public sealed class AnalysisBuilder
{
    private readonly Dictionary<(int Second, string Process), Bucket> buckets = new();
    private readonly Dictionary<string, long> counts = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, long> Counts => counts;
    public void Add(Observation value)
    {
        counts[value.Category] = counts.GetValueOrDefault(value.Category) + 1;
        var second = Math.Max(0, (int)Math.Floor(value.TimeMs / 1000));
        AddBucket((second, ""), value);
        AddBucket((second, value.ProcessId ?? "?"), value);
    }
    private void AddBucket((int Second, string Process) key, Observation value)
    {
        if (!buckets.TryGetValue(key, out var bucket)) buckets[key] = bucket = new();
        if (value.Category == "CPU") {
            bucket.Samples++;
            if (value.CpuWeightMs is { } weight) { bucket.CpuMs += weight; bucket.Weighted++; }
        }
        if (value.Category == "Disk") {
            bucket.DiskBytes += value.Bytes ?? 0; bucket.DiskOps++;
            if (value.DurationMs is >= 0 and { } latency) { bucket.Latencies.Add(latency); bucket.IoMs += latency; }
        }
        if (value.Category == "File") bucket.FileBytes += value.Bytes ?? 0;
        if (value.Category == "Network" && (value.Operation.Contains("Send", StringComparison.OrdinalIgnoreCase) ||
            value.Operation.Contains("Recv", StringComparison.OrdinalIgnoreCase) ||
            value.Operation.Contains("Receive", StringComparison.OrdinalIgnoreCase))) bucket.NetworkBytes += value.Bytes ?? 0;
        if (value.WorkingSetBytes is { } memory) bucket.MemoryByProcess[value.ProcessId ?? "?"] = memory;
        if (bucket.Evidence.Count < 3 && value.Category is "CPU" or "Disk") bucket.Evidence.Add(value.Evidence);
    }
    public IReadOnlyList<AggregateRow> Finish() => buckets.OrderBy(x => x.Key.Second).ThenBy(x => x.Key.Process,
        StringComparer.Ordinal).Select(x => new AggregateRow(x.Key.Second, x.Key.Process, x.Value.Samples,
        x.Value.CpuMs, x.Value.Weighted, x.Value.DiskBytes, x.Value.FileBytes, x.Value.NetworkBytes,
        x.Value.IoMs, x.Value.DiskOps, Percentile95(x.Value.Latencies), x.Value.MemoryByProcess.Count == 0 ? null : x.Value.MemoryByProcess.Values.Sum(), x.Value.Evidence)).ToList();
    public static double? Percentile95(List<double> values)
    {
        if (values.Count == 0) return null;
        values.Sort();
        return values[Math.Max(0, (int)Math.Ceiling(values.Count * .95) - 1)];
    }
    private sealed class Bucket
    {
        public long Samples, Weighted, DiskBytes, FileBytes, NetworkBytes, DiskOps;
        public double CpuMs, IoMs;
        public Dictionary<string, double> MemoryByProcess { get; } = [];
        public List<double> Latencies { get; } = [];
        public List<EvidenceReference> Evidence { get; } = [];
    }
}

public static class IncidentDetector
{
    public static IReadOnlyList<Incident> Detect(IEnumerable<AggregateRow> rows, int processors,
        double durationMs, DetectionSettings settings)
    {
        Validate(settings);
        var system = rows.Where(x => x.ProcessId == "").OrderBy(x => x.Second).ToArray();
        var result = new List<Incident>();
        Scan("CPU", system, row => row.CpuSamples > 0 && row.WeightedSamples == row.CpuSamples &&
            CpuPercent(row, processors, durationMs) >= settings.CpuThreshold);
        Scan("Disk", system, row => row.DiskOperations >= settings.MinimumDiskOperations &&
            row.DiskP95Ms > settings.DiskLatencyThresholdMs);
        return result.OrderBy(x => x.StartMs).ThenBy(x => x.Category).ToArray();

        void Scan(string category, AggregateRow[] data, Func<AggregateRow, bool> predicate)
        {
            List<AggregateRow> run = [];
            foreach (var row in data) {
                if (!predicate(row) || (run.Count > 0 && row.Second != run[^1].Second + 1)) Flush();
                if (predicate(row)) run.Add(row);
            }
            Flush();
            void Flush() {
                if (run.Count >= settings.ConsecutiveSeconds && (category != "CPU" ||
                    Math.Min(durationMs,(run[^1].Second+1)*1000d)-run[0].Second*1000d >= settings.ConsecutiveSeconds*1000d)) {
                    var start = run[0].Second * 1000d;
                    var end = Math.Min(durationMs, (run[^1].Second + 1) * 1000d);
                    var title = category == "CPU" ? "Sustained high sampled CPU activity" : "Elevated recorded disk latency";
                    var explanation = category == "CPU"
                        ? $"Estimated CPU activity met the {settings.CpuThreshold:g}% heuristic for {(end-start)/1000:n1} seconds."
                        : $"Recorded disk p95 latency exceeded {settings.DiskLatencyThresholdMs:g} ms with at least {settings.MinimumDiskOperations} completed operations per second.";
                    result.Add(new($"{category}:{run[0].Second}", category, title, start, end,
                        explanation + " This condition does not establish an application freeze or its cause.",
                        Provenance.ResourceAttribution, run.SelectMany(x => x.Evidence).Take(6).ToArray()));
                }
                run.Clear();
            }
        }
    }
    public static double CpuPercent(AggregateRow row, int processors, double durationMs)
    {
        var width = Math.Min(1000, Math.Max(1, durationMs - row.Second * 1000));
        return row.CpuMs / (Math.Max(1, processors) * width) * 100;
    }
    public static void Validate(DetectionSettings s)
    {
        if (!double.IsFinite(s.CpuThreshold) || s.CpuThreshold is <= 0 or > 100 ||
            !double.IsFinite(s.DiskLatencyThresholdMs) || s.DiskLatencyThresholdMs <= 0 ||
            s.ConsecutiveSeconds is < 1 or > 120 || s.MinimumDiskOperations < 1)
            throw new WoiException("InvalidSettings", "Use CPU 1–100%, a positive disk latency, 1–120 consecutive seconds, and at least one disk operation.");
    }
}

public static class EvidenceLanguage
{
    public static string Explain(IntervalQuery interval, IReadOnlyList<Contributor> contributors,
        IReadOnlyList<Capability> capabilities, IReadOnlyList<string> warnings)
    {
        var duration = Math.Max(0, interval.EndMs - interval.StartMs) / 1000;
        var cpuAvailable = capabilities.Any(x => x.Category == "CPU" && x.Status == Availability.Available);
        var diskAvailable = capabilities.Any(x => x.Category == "Disk" && x.Status == Availability.Available);
        var ranked = contributors.Where(x => x.CpuSamples > 0).ToArray();
        var total = ranked.Sum(x => x.CpuSamples ?? 0);
        string description = $"Selected interval: {duration:n2} seconds.";
        if (total > 0) {
            // Match QueryEngine's CPU ordering: weights apply only when every contributor has them.
            var weighted = ranked.All(x => x.CpuMs is >= 0);
            var totalMs = ranked.Sum(x => x.CpuMs ?? 0);
            if (weighted && totalMs > 0) {
                var top = ranked.OrderByDescending(x => x.CpuMs).First();
                description += $" {top.Name} accounted for {(top.CpuMs!.Value / totalMs * 100):n1}% of estimated sampled CPU time ({top.CpuMs:n2} of {totalMs:n2} ms), using recorded sample weights.";
            } else {
                var top = ranked.OrderByDescending(x => x.CpuSamples).First();
                description += $" {top.Name} accounted for {(double)(top.CpuSamples ?? 0) / total * 100:n1}% of recorded CPU samples ({top.CpuSamples:n0} of {total:n0}); complete sample weights are unavailable.";
            }
        } else if (!cpuAvailable) description += " CPU sampling is unavailable in this recording.";
        else description += " No CPU samples were recorded in this interval.";
        var disk = contributors.OrderByDescending(x => x.DiskBytes).FirstOrDefault();
        if (disk is { DiskBytes: > 0 }) description += $" {disk.Name} accounted for {disk.DiskBytes/1048576d:n2} MiB of recorded physical disk transfers.";
        else if (!diskAvailable) description += " Physical disk transfer coverage is unavailable.";
        else description += " No physical disk transfers were recorded in this interval.";
        description += " These are resource attributions, not proof of why an application froze.";
        if (contributors.All(x => x.BaselineCpuPercent == null)) description += " A usable sampled CPU baseline is unavailable.";
        else description += " Baseline percentages use the preceding available interval, up to 30 seconds.";
        if (warnings.Count > 0) description += " Evidence limitations apply; review capture quality.";
        return description;
    }
}
