using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Woi.Core;

public sealed class QueryEngine(ITraceReader reader, CacheStore store)
{
    private readonly IReadOnlyList<EntityLifetime> entities = store.Entities();
    private readonly Dictionary<string,EntityLifetime> entitiesById = store.Entities().ToDictionary(x=>x.Id);
    public TraceSession Session => reader.Session;
    public DetectionSettings Settings { get; private set; } = new();
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
    private IReadOnlyList<AggregateRow> ExactRows(IntervalQuery query, CancellationToken token)
    {
        query = Clamp(query);
        var firstFull = Math.Ceiling(query.StartMs / 1000) * 1000;
        var lastFull = Math.Floor(query.EndMs / 1000) * 1000;
        var rows = firstFull < lastFull ? store.ContributorTotals(firstFull, lastFull, query.ProcessId, token) : [];
        var builder = new AnalysisBuilder();
        var firstEnd = Math.Min(firstFull, query.EndMs);
        if (firstEnd > query.StartMs)
            foreach (var value in reader.ReadContributorObservations(query with { EndMs = firstEnd }, token)) builder.Add(value);
        var lastStart = Math.Max(lastFull, firstEnd);
        if (query.EndMs > lastStart)
            foreach (var value in reader.ReadContributorObservations(query with { StartMs = lastStart }, token)) builder.Add(value);
        // When the entire selection lies inside one bucket, firstFull >= end handled the whole interval.
        return rows.Concat(builder.Finish().Where(x => query.ProcessId == null || x.ProcessId == query.ProcessId)).ToArray();
    }
    public IReadOnlyList<Contributor> Contributors(IntervalQuery query, CancellationToken token)
    {
        query = Clamp(query);
        var rows = ExactRows(query, token).Where(x => x.ProcessId != "");
        var baselineStart = Math.Max(0, query.StartMs - 30000);
        var baselineRows = query.StartMs > baselineStart ?
            ExactRows(new(baselineStart, query.StartMs, query.ProcessId), token).Where(x => x.ProcessId != "").ToArray() : [];
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
        return query.Ranking switch {
            "Disk" => result.OrderByDescending(x => x.DiskBytes).ThenByDescending(x => x.RecordedIoMs).ToArray(),
            "Latency" => result.OrderByDescending(x => x.RecordedIoMs).ThenByDescending(x => x.DiskBytes).ToArray(),
            _ => result.OrderByDescending(x => weightedRanking ? x.CpuMs ?? 0 : x.CpuSamples ?? 0).ThenByDescending(x => x.DiskBytes).ToArray()
        };
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
        var explanation=EvidenceLanguage.Explain(interval, contributors, Session.Warnings);
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
    public void Export(string path, string format, IntervalQuery interval, CancellationToken token)
    {
        if(format is not ("json" or "markdown"))throw new WoiException("InvalidFormat","Choose json or markdown export.");
        var report = Report(interval, token);
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase)) {
            File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions(Wire.Json) { WriteIndented = true }));
            return;
        }
        static string MiB(long? value) => value is {} bytes ? (bytes/1048576d).ToString("F2",CultureInfo.InvariantCulture) : "unavailable";
        var text = new StringBuilder("# Windows Open Intelligence incident report\n\n");
        text.AppendLine(report.Explanation).AppendLine().AppendLine("Provenance: resource attribution.").AppendLine();
        text.AppendLine($"Source SHA-256: {Session.SourceHash}").AppendLine($"Parser: {Session.ParserVersion} / analysis: {Wire.AnalysisVersion}");
        text.AppendLine(FormattableString.Invariant($"Interval: {interval.StartMs/1000:F3}–{interval.EndMs/1000:F3} seconds from trace start.")).AppendLine();
        text.AppendLine("| Process | PID | CPU samples | Physical disk MiB | Logical file MiB | Network event MiB |").AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var x in report.Contributors)
            text.AppendLine(FormattableString.Invariant($"| {x.Name.Replace("|", "\\|").Replace("\n", " ")} | {x.Pid} | {x.CpuSamples} | {MiB(x.DiskBytes)} | {MiB(x.FileBytes)} | {MiB(x.NetworkBytes)} |"));
        text.AppendLine().AppendLine("## Classified findings").AppendLine();
        foreach(var finding in report.Findings ?? []) text.AppendLine($"- {finding.Provenance}: {finding.Text}");
        text.AppendLine().AppendLine("## Evidence coordinates").AppendLine();
        foreach (var evidence in report.Contributors.SelectMany(x => x.Evidence).DistinctBy(x => x.EtlxEventIndex).Take(30))
            text.AppendLine(FormattableString.Invariant($"- ETLX index {evidence.EtlxEventIndex}; {evidence.TimeMs/1000:F6}s; {evidence.Provider}/{evidence.EventName}; PID {evidence.Pid}, TID {evidence.Tid}."));
        text.AppendLine().AppendLine("ETLX is derived data; its indices are not raw ETL offsets. Open the original trace in WPA and use the time/provider coordinates.").AppendLine();
        foreach (var warning in report.Limitations) text.AppendLine("- " + warning);
        File.WriteAllText(path, text.ToString());
    }
}
