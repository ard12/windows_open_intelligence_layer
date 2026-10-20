namespace Woi.Core;

public enum IntervalMetric { CpuSamples, CpuMs, DiskBytes, FileBytes, NetworkBytes, DiskOperations }
public record IntervalComparisonQuery(IntervalQuery Baseline, IntervalQuery Comparison,
    IntervalMetric Metric = IntervalMetric.CpuMs, int Offset = 0, int Limit = 200);
public record IntervalDelta(string Name, string Unit, double? Baseline, double? Comparison,
    double? Difference, double? PercentChange, double? BaselineRate, double? ComparisonRate, double? RateDifference);
public record IntervalContributorDelta(string ProcessId, string Name, int? Pid, IntervalDelta Value,
    IReadOnlyList<EvidenceReference> BaselineEvidence, IReadOnlyList<EvidenceReference> ComparisonEvidence);
public record IntervalComparisonPage(string SourceHash, IntervalComparisonQuery Query,
    long? BaselineUnknownWeights, long? ComparisonUnknownWeights, IReadOnlyList<IntervalDelta> Totals,
    int TotalRows, IReadOnlyList<IntervalContributorDelta> Rows, bool More, IReadOnlyList<string> Limitations);

public sealed partial class QueryEngine
{
    public IntervalComparisonPage CompareIntervals(IntervalComparisonQuery query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (query.Baseline == null || query.Comparison == null || !Enum.IsDefined(query.Metric) ||
            query.Offset < 0 || query.Limit is < 1 or > 500)
            throw new WoiException("InvalidComparison", "Choose two intervals, a supported metric and a page size from 1 to 500.");
        var a = Clamp(query.Baseline); var b = Clamp(query.Comparison);
        if (a.ProcessId != b.ProcessId)
            throw new WoiException("IncompatibleScope", "Use the same captured process lifetime on both sides, or all processes on both sides.");
        if (a.ProcessId != null && entitiesById[a.ProcessId].Kind != "Process")
            throw new WoiException("IncompatibleScope", "Choose a captured process lifetime.");
        query = query with { Baseline = a, Comparison = b };
        var first = ExactRows(a, token).Where(x => x.ProcessId != "").ToArray();
        var second = ExactRows(b, token).Where(x => x.ProcessId != "").ToArray();
        bool Has(string category) => Session.Capabilities.Any(x => x.Category == category && x.Status == Availability.Available);
        double? Value(AggregateRow[] rows, IntervalMetric metric) => metric switch {
            IntervalMetric.CpuSamples => Has("CPU") ? rows.Sum(x => x.CpuSamples) : null,
            IntervalMetric.CpuMs => Has("CPU") && rows.Sum(x => x.CpuSamples) == rows.Sum(x => x.WeightedSamples) ? rows.Sum(x => x.CpuMs) : null,
            IntervalMetric.DiskBytes => Has("Disk") ? rows.Sum(x => x.DiskBytes) : null,
            IntervalMetric.FileBytes => Has("FileBytes") ? rows.Sum(x => x.FileBytes) : null,
            IntervalMetric.NetworkBytes => Has("NetworkBytes") ? rows.Sum(x => x.NetworkBytes) : null,
            IntervalMetric.DiskOperations => Has("Disk") ? rows.Sum(x => x.DiskOperations) : null,
            _ => null
        };
        IntervalDelta Delta(AggregateRow[] left, AggregateRow[] right, IntervalMetric metric)
        {
            var (name, unit) = metric switch {
                IntervalMetric.CpuSamples => ("Recorded CPU samples", "samples"),
                IntervalMetric.CpuMs => ("Estimated sampled CPU", "ms"),
                IntervalMetric.DiskBytes => ("Recorded physical disk transfers", "bytes"),
                IntervalMetric.FileBytes => ("Recorded logical file requests", "bytes"),
                IntervalMetric.NetworkBytes => ("Recorded network sends/receives", "bytes"),
                _ => ("Recorded disk operations", "operations")
            };
            var av = Value(left, metric); var bv = Value(right, metric);
            var ar = av / ((a.EndMs - a.StartMs) / 1000); var br = bv / ((b.EndMs - b.StartMs) / 1000);
            return new(name, unit, av, bv, bv - av, av > 0 ? (bv - av) / av * 100 : null, ar, br, br - ar);
        }
        var totals = Enum.GetValues<IntervalMetric>().Select(x => Delta(first, second, x)).ToArray();
        var leftGroups = first.GroupBy(x => x.ProcessId).ToDictionary(x => x.Key, x => x.ToArray());
        var rightGroups = second.GroupBy(x => x.ProcessId).ToDictionary(x => x.Key, x => x.ToArray());
        List<IntervalContributorDelta> rows = [];
        foreach (var id in leftGroups.Keys.Union(rightGroups.Keys)) {
            token.ThrowIfCancellationRequested();
            var left = leftGroups.GetValueOrDefault(id) ?? []; var right = rightGroups.GetValueOrDefault(id) ?? [];
            var entity = entitiesById.GetValueOrDefault(id);
            var delta = Delta(left, right, query.Metric);
            // Retain rows with unknown CPU weights; a null delta is not evidence of no change.
            if (delta.Baseline == 0 && delta.Comparison == 0) continue;
            rows.Add(new(id, entity?.Name ?? "Unattributed activity", entity?.OsId, delta,
                left.SelectMany(x => x.Evidence).DistinctBy(x => x.EtlxEventIndex).Take(6).ToArray(),
                right.SelectMany(x => x.Evidence).DistinctBy(x => x.EtlxEventIndex).Take(6).ToArray()));
        }
        var ranked = rows.OrderByDescending(x => Math.Abs(x.Value.Difference ?? double.NegativeInfinity))
            .ThenBy(x => x.ProcessId, StringComparer.Ordinal).ToArray();
        List<string> limits = [
            "B minus A is a recorded difference, not a causal or equivalent-work conclusion. Intervals may overlap.",
            "Rates divide recorded totals by each interval's duration; they do not normalize work quantity.",
            "Percent change is unavailable for a zero baseline or missing value. Unknown CPU weights suppress CPU-ms differences.",
            "Rows match exact trace-local process lifetimes, including activity present on only one side. Names and PIDs are not matching keys.",
            "Explorer display filters do not affect comparison. Evidence samples identify supporting events, not every measured operation."
        ];
        limits.AddRange(Session.Warnings);
        token.ThrowIfCancellationRequested();
        return new(Session.SourceHash, query,
            Has("CPU") ? first.Sum(x => x.CpuSamples - x.WeightedSamples) : null,
            Has("CPU") ? second.Sum(x => x.CpuSamples - x.WeightedSamples) : null,
            totals, ranked.Length, ranked.Skip(query.Offset).Take(query.Limit).ToArray(),
            (long)query.Offset + query.Limit < ranked.Length, limits);
    }
}
