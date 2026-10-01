namespace Woi.Core;

public static class ComparisonEngine
{
    public static Comparison Compare(InvestigationDocument first, InvestigationDocument second, ComparisonRequest request)
    {
        if (!request.SameTaskConfirmed || string.IsNullOrWhiteSpace(request.Task) || request.Task.Length > 300 || request.Change == null || request.Change.Length > 1000)
            throw new WoiException("TaskConfirmationRequired", "Describe the same task and confirm both intervals contain equivalent work.");
        if (first.Investigation.Id == second.Investigation.Id) throw new WoiException("DistinctAttemptsRequired", "Choose two distinct marked attempts.");
        if (first.Source.Id == second.Source.Id && first.Source != second.Source) throw new WoiException("EvidenceSourceMismatch", "One source identity has conflicting recorded metadata.");
        bool application = first.Investigation.ProcessLifetimeId != null || second.Investigation.ProcessLifetimeId != null;
        bool sameLifetime = first.Source.Id == second.Source.Id && first.Investigation.ProcessLifetimeId == second.Investigation.ProcessLifetimeId;
        if (application && !sameLifetime && (!request.ApplicationContextConfirmed || string.IsNullOrWhiteSpace(request.ApplicationContext) || request.ApplicationContext.Length > 1000))
            throw new WoiException("ApplicationConfirmationRequired", "Confirm application path/version context across sources; PID and process name cannot identify the same application.");
        if ((first.Investigation.ProcessLifetimeId == null) != (second.Investigation.ProcessLifetimeId == null))
            throw new WoiException("IncompatibleScope", "Compare two whole-system intervals or two explicitly matched application intervals.");
        var a = first.Investigation.Metrics; var b = second.Investigation.Metrics;
        bool definitions = a.DurationSeconds >= 3 && b.DurationSeconds >= 3 && first.Source.Kind == second.Source.Kind && first.Source.Version == second.Source.Version;
        double Readability(InvestigationDocument doc) {
            var rows = doc.Snapshots.Where(s => InvestigationAnalyzer.Time(doc.Source, s) >= doc.Investigation.Window.StartSeconds && InvestigationAnalyzer.Time(doc.Source, s) <= doc.Investigation.Window.EndSeconds).ToArray();
            var count = rows.Sum(x => (long)x.EnumeratedProcesses);
            return count > 0 ? 1 - rows.Sum(x => (long)x.UnreadableProcesses) / (double)count : 0;
        }
        bool coverageSimilar = application || Math.Abs(Readability(first) - Readability(second)) <= .05;
        bool Comparable(double ca, double cb) => a.DurationSeconds >= 3 && b.DurationSeconds >= 3 && definitions && coverageSimilar && ca / a.DurationSeconds >= .8 && cb / b.DurationSeconds >= .8;
        var metrics = new List<ComparisonMetric> {
            Metric("User-marked task duration", "seconds", a.DurationSeconds, b.DurationSeconds, true, "Intervals and equivalent work were confirmed by the user."),
            Metric("Accounted CPU time", "CPU-seconds", a.CpuSeconds, b.CpuSeconds, Comparable(a.CpuCoveredSeconds,b.CpuCoveredSeconds), "Requires at least three seconds per attempt, matching rate definitions, at least 80% usable duration in both attempts, and comparable process readability."),
            Metric("Accounted reads", "bytes", a.ReadBytes, b.ReadBytes, Comparable(a.ReadCoveredSeconds,b.ReadCoveredSeconds), "Process I/O accounting is not physical disk I/O. At least three seconds and comparable coverage are required."),
            Metric("Accounted writes", "bytes", a.WriteBytes, b.WriteBytes, Comparable(a.WriteCoveredSeconds,b.WriteCoveredSeconds), "Process I/O accounting is not physical disk I/O. At least three seconds and comparable coverage are required."),
            Metric("Observed minimum available physical memory", "bytes", a.MinimumAvailableBytes, b.MinimumAvailableBytes, definitions && a.MemorySnapshotCount >= Math.Ceiling(a.DurationSeconds * .8) && b.MemorySnapshotCount >= Math.Ceiling(b.DurationSeconds * .8), "Point observations may miss a lower value between polls."),
            Metric("Observed peak process private allocation", "bytes", a.PeakPrivateBytes, b.PeakPrivateBytes, definitions && application && a.PrivateSnapshotCount >= Math.Ceiling(a.DurationSeconds * .8) && b.PrivateSnapshotCount >= Math.Ceiling(b.DurationSeconds * .8), "Private allocation is not system memory pressure; application context was user-confirmed.")
        };
        var statement = FormattableString.Invariant($"The second user-marked attempt lasted {b.DurationSeconds:F2}s versus {a.DurationSeconds:F2}s for the first ({b.DurationSeconds - a.DurationSeconds:+0.00;-0.00;0.00}s). Compare absolute resource use alongside duration; lower CPU alone does not establish improvement.");
        return new(Guid.NewGuid().ToString("N"), first.Investigation.Id, second.Investigation.Id,
            request.Task.Trim(), request.Change, request.ApplicationContext, statement, metrics,
            ["Equivalent work and any application matching are user confirmations, not inferred from PID.",
             "A before/after difference does not prove that the stated change caused it. Repeat trials to assess variation.",
             "Partial counter intervals use proportional rate estimates; exact subsecond activity is unresolved.",
             "No throughput normalization is applied because work quantity is not recorded.",
             "Unavailable or incompatible resource measurements have no calculated difference."]);
    }
    private static ComparisonMetric Metric(string name, string unit, double? a, double? b, bool comparable, string limitation)
    {
        comparable &= a != null && b != null;
        return new(name, unit, a, b, comparable && a > 0 ? (b - a) / a * 100 : null, comparable, limitation);
    }
}
