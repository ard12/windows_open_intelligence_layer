using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Woi.Core;

public sealed partial class QueryEngine(ITraceReader reader, CacheStore store)
{
    private readonly IReadOnlyList<EntityLifetime> entities = store.Entities();
    private readonly Dictionary<string,EntityLifetime> entitiesById = store.Entities().ToDictionary(x=>x.Id);
    public ContributorQueryProfile? LastContributorProfile { get; private set; }
    public TraceSession Session => reader.Session;
    public DetectionSettings Settings { get; private set; } = new();
    private ProcessHistoryIndex? processHistory;
    public ProcessHistoryPage History(ProcessHistoryQuery query, CancellationToken token)
    {
        var interval = Clamp(new(query.StartMs, query.EndMs));
        processHistory ??= new(entities, token);
        processHistory.Validate(query);
        var contributors = Contributors(interval, token);
        return processHistory.Page(Session.SourceHash, query with { EndMs = interval.EndMs }, contributors, token);
    }
    public Summary Summary() => new(Session, entities.Where(x => x.Kind == "Process").OrderBy(x => x.Name).ToArray(),
        IncidentDetector.Detect(store.Rows(0, Session.DurationMs + 1, ""), Session.ProcessorCount, Session.DurationMs, Settings), Settings);
    public void Configure(DetectionSettings value) { IncidentDetector.Validate(value); Settings = value; }
    public IntervalQuery Clamp(IntervalQuery query)
    {
        if (!double.IsFinite(query.StartMs) || !double.IsFinite(query.EndMs) || query.StartMs < 0 || query.EndMs <= query.StartMs || query.StartMs >= Session.DurationMs)
            throw new WoiException("InvalidInterval", "Choose a finite interval with end after start.");
        if(query.ProcessId!=null && !entitiesById.ContainsKey(query.ProcessId))
            throw new WoiException("EntityMissing","The requested process lifetime is unavailable.");
        if(query.Ranking is not ("CPU" or "Disk" or "Latency"))
            throw new WoiException("InvalidRanking","Choose CPU, Disk, or Latency ranking.");
        return query with { StartMs = Math.Min(query.StartMs, Session.DurationMs), EndMs = Math.Min(query.EndMs, Session.DurationMs) };
    }
    private IReadOnlyList<AggregateRow> ExactRows(IntervalQuery query, CancellationToken token, QueryMeasurement? measurement = null)
    {
        query = Clamp(query);
        var firstFull = Math.Ceiling(query.StartMs / 1000) * 1000;
        var lastFull = Math.Floor(query.EndMs / 1000) * 1000;
        var totalsClock = Stopwatch.StartNew();
        var rows = firstFull < lastFull ? store.ContributorTotals(firstFull, lastFull, query.ProcessId, token) : [];
        if (measurement != null) measurement.TotalsMs = totalsClock.Elapsed.TotalMilliseconds;
        var replayClock = Stopwatch.StartNew();
        var builder = new AnalysisBuilder();
        var firstEnd = Math.Min(firstFull, query.EndMs);
        if (firstEnd > query.StartMs)
            foreach (var value in reader.ReadContributorObservations(query with { EndMs = firstEnd }, token)) { builder.Add(value); if (measurement != null) measurement.EdgeObservations++; }
        var lastStart = Math.Max(lastFull, firstEnd);
        if (query.EndMs > lastStart)
            foreach (var value in reader.ReadContributorObservations(query with { StartMs = lastStart }, token)) { builder.Add(value); if (measurement != null) measurement.EdgeObservations++; }
        // When the entire selection lies inside one bucket, firstFull >= end handled the whole interval.
        var result = rows.Concat(builder.Finish().Where(x => query.ProcessId == null || x.ProcessId == query.ProcessId)).ToArray();
        if (measurement != null) measurement.ReplayMs = replayClock.Elapsed.TotalMilliseconds;
        return result;
    }
    public IReadOnlyList<Contributor> Contributors(IntervalQuery query, CancellationToken token)
    {
        LastContributorProfile = null;
        var clock = Stopwatch.StartNew();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var visitedBefore = (reader as IContributorQueryDiagnostics)?.ContributorRecordsVisited;
        query = Clamp(query);
        var selectedMeasurement = new QueryMeasurement();
        var baselineMeasurement = new QueryMeasurement();
        var rows = ExactRows(query, token, selectedMeasurement).Where(x => x.ProcessId != "");
        var baselineStart = Math.Max(0, query.StartMs - 30000);
        var baselineRows = query.StartMs > baselineStart ?
            ExactRows(new(baselineStart, query.StartMs, query.ProcessId), token, baselineMeasurement).Where(x => x.ProcessId != "").ToArray() : [];
        var rankClock = Stopwatch.StartNew();
        var baseline = baselineRows.GroupBy(x => x.ProcessId).ToDictionary(x => x.Key, x => x.ToArray());
        bool Has(string category) => Session.Capabilities.Any(x => x.Category == category && x.Status == Availability.Available);
        var result = rows.GroupBy(x => x.ProcessId).Select(group => {
            var item = entitiesById.GetValueOrDefault(group.Key);
            long samples = group.Sum(x => x.CpuSamples), weighted = group.Sum(x => x.WeightedSamples);
            double? cpu = samples > 0 && weighted == samples ? group.Sum(x => x.CpuMs) : null;
            var baseData = baseline.GetValueOrDefault(group.Key);
            double? basePercent = baseData is { Length: > 0 } && baseData.Sum(x => x.CpuSamples) > 0 && baseData.Sum(x => x.CpuSamples) == baseData.Sum(x => x.WeightedSamples)
                ? baseData.Sum(x => x.CpuMs) / (query.StartMs-baselineStart) / Math.Max(1, Session.ProcessorCount)*100 : null;
            return new Contributor(item?.Id, item?.Name ?? "Unattributed activity", item?.OsId, Has("CPU") ? samples : null, cpu,
                cpu / Math.Max(1, query.EndMs-query.StartMs) / Math.Max(1, Session.ProcessorCount)*100,
                Has("Disk") ? group.Sum(x => x.DiskBytes) : null, Has("FileBytes") ? group.Sum(x => x.FileBytes) : null, Has("NetworkBytes") ? group.Sum(x => x.NetworkBytes) : null,
                Has("Disk") ? group.Sum(x => x.IoMs) : null, basePercent, group.SelectMany(x => x.Evidence).DistinctBy(x => x.EtlxEventIndex).Take(6).ToArray());
        }).Where(x => x.CpuSamples > 0 || x.DiskBytes > 0 || x.FileBytes > 0 || x.NetworkBytes > 0).ToArray();
        bool weightedRanking=result.Where(x=>x.CpuSamples>0).All(x=>x.CpuMs!=null);
        var ranked = query.Ranking switch {
            "Disk" => result.OrderByDescending(x => x.DiskBytes).ThenByDescending(x => x.RecordedIoMs).ToArray(),
            "Latency" => result.OrderByDescending(x => x.RecordedIoMs).ThenByDescending(x => x.DiskBytes).ToArray(),
            _ => result.OrderByDescending(x => weightedRanking ? x.CpuMs ?? 0 : x.CpuSamples ?? 0).ThenByDescending(x => x.DiskBytes).ToArray()
        };
        var visitedAfter = (reader as IContributorQueryDiagnostics)?.ContributorRecordsVisited;
        LastContributorProfile = new(clock.Elapsed.TotalMilliseconds, selectedMeasurement.TotalsMs,
            selectedMeasurement.ReplayMs, baselineMeasurement.TotalsMs, baselineMeasurement.ReplayMs,
            rankClock.Elapsed.TotalMilliseconds, selectedMeasurement.EdgeObservations, baselineMeasurement.EdgeObservations,
            visitedAfter - visitedBefore, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        return ranked;
    }
    private sealed class QueryMeasurement
    {
        public double TotalsMs, ReplayMs;
        public long EdgeObservations;
    }
    public IReadOnlyList<MetricPoint> Metrics(IntervalQuery query)
    {
        query = Clamp(query);
        var process = query.ProcessId ?? "";
        var data = store.Rows(Math.Floor(query.StartMs/1000)*1000, query.EndMs, process);
        bool Has(string category) => Session.Capabilities.Any(x => x.Category == category && x.Status == Availability.Available);
        var result = data.Select(x => new MetricPoint(x.Second*1000, Has("CPU") ? x.CpuSamples : null,
            x.CpuSamples > 0 && x.WeightedSamples == x.CpuSamples ? IncidentDetector.CpuPercent(x, Session.ProcessorCount, Session.DurationMs) : null,
            Has("Disk") ? x.DiskBytes : null, x.DiskP95Ms, Has("FileBytes") ? x.FileBytes : null,
            Has("NetworkBytes") ? x.NetworkBytes : null, x.WorkingSetBytes, Has("Disk") ? x.DiskOperations : null)).ToArray();
        return Downsample(result, 2000);
    }
    public static IReadOnlyList<MetricPoint> Downsample(IReadOnlyList<MetricPoint> data, int limit)
    {
        if(limit<8)throw new ArgumentOutOfRangeException(nameof(limit),"At least eight points are required to preserve resource peaks.");
        if (data.Count <= limit) return data;
        var groupSize = (int)Math.Ceiling(data.Count / (double)(limit / 8));
        bool estimated=data.Any(x=>x.CpuPercent!=null);
        List<MetricPoint> result = [];
        for (int i=0; i<data.Count; i+=groupSize) {
            var group = data.Skip(i).Take(groupSize).ToArray();
            result.AddRange(new[] { group[0], group[^1],
                group.MaxBy(x => estimated ? x.CpuPercent ?? -1 : x.CpuSamples ?? -1)!,
                group.MaxBy(x => x.DiskP95Ms ?? -1)!, group.MaxBy(x => x.DiskBytes ?? -1)!,
                group.MaxBy(x => x.FileBytes ?? -1)!, group.MaxBy(x => x.NetworkBytes ?? -1)!,
                group.MaxBy(x => x.WorkingSetBytes ?? -1)! }.DistinctBy(x => x.TimeMs).OrderBy(x => x.TimeMs));
        }
        return result;
    }
    public Report Report(IntervalQuery interval, CancellationToken token)
    {
        interval = Clamp(interval);
        var contributors = Contributors(interval, token);
        var explanation=EvidenceLanguage.Explain(interval, contributors, Session.Capabilities, Session.Warnings);
        var references=contributors.SelectMany(x=>x.Evidence).DistinctBy(x=>x.EtlxEventIndex).Take(12).ToArray();
        bool cpuCaptured=Session.Capabilities.Any(x=>x.Category=="CPU" && x.Status==Availability.Available);
        List<Explanation> findings = [new(Provenance.Observed,cpuCaptured ? $"Recorded interval [{interval.StartMs:F3}, {interval.EndMs:F3}) ms contains {contributors.Sum(x=>x.CpuSamples):n0} busy CPU samples." : "CPU sample coverage is unavailable for this recording.",references),
            new(Provenance.ResourceAttribution,explanation,references)];
        foreach(var contributor in contributors.Where(x=>x.Evidence.Count>0).Take(3)) {
            var entity=contributor.ProcessId==null?null:entitiesById.GetValueOrDefault(contributor.ProcessId);
            if(entity==null)continue;
            double overlap=Math.Min(interval.EndMs,entity.EndMs)-Math.Max(interval.StartMs,entity.StartMs);
            if(overlap>0)findings.Add(new(Provenance.TemporalCorrelation,$"Captured boundaries for {entity.Name} overlap the selection by {overlap/1000:n3} seconds. Boundaries may be partial; overlap does not establish causality.",contributor.Evidence));
        }
        return new(Session, interval, explanation, contributors,
            Summary().Incidents.Where(x => x.EndMs > interval.StartMs && x.StartMs < interval.EndMs).ToArray(), Session.Warnings, Findings:findings);
    }
    public void Export(string path, string format, IntervalQuery interval, CancellationToken token, ExportPrivacyOptions? privacy = null) =>
        ReportExport.Write(path, format, Report(interval, token), privacy, token);
}
