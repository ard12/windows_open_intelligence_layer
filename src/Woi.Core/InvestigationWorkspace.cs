using System.Text;
using System.Text.Json;

namespace Woi.Core;

public sealed class InvestigationWorkspace
{
    private readonly Dictionary<string, InvestigationDocument> documents = new(StringComparer.Ordinal);
    public InvestigationView Add(InvestigationDocument document)
    {
        if (documents.Count >= 8 && !documents.ContainsKey(document.Investigation.Id) ||
            documents.Where(x => x.Key != document.Investigation.Id).Sum(x => x.Value.Snapshots.Sum(s => (long)s.Processes.Count)) + document.Snapshots.Sum(s => (long)s.Processes.Count) > InvestigationAnalyzer.MaximumRows)
            throw new WoiException("WorkspaceLimit", "Close a pinned investigation before opening more evidence; the workspace is bounded to eight investigations and 250,000 snapshot process rows.");
        documents[document.Investigation.Id] = document;
        return View(document);
    }
    public InvestigationView Open(string path) => Add(InvestigationStore.Open(path));
    public InvestigationView GetView(string id) => View(Get(id));
    public void Save(string id, string path, CancellationToken token = default) => InvestigationStore.Save(path, Get(id), token);
    public bool Close(string id) => documents.Remove(id);
    public Comparison Compare(ComparisonRequest request) => ComparisonEngine.Compare(Get(request.FirstId), Get(request.SecondId), request);
    public InvestigationView Attach(string id, Report report)
    {
        if (report == null) throw new WoiException("InvalidAttachment", "Attach an imported ETL report.");
        ValidateAttachment(report);
        report = CompactReport(report);
        return Add(Get(id) with { AttachedTrace = report });
    }
    public static Report CompactReport(Report report) => report with {
        Contributors = report.Contributors.Take(20).ToArray(), Incidents = report.Incidents.Take(20).ToArray(), Findings = report.Findings?.Take(6).ToArray(),
        Limitations = report.Limitations.Concat(["User-attached ETL report is an excerpt: at most 20 contributors, 20 incidents, and six classified findings. The original ETL remains the separate canonical source."]).Distinct().ToArray()
    };
    internal static void ValidateAttachment(Report? report)
    {
        if (report == null) return;
        if (report.Session == null || string.IsNullOrWhiteSpace(report.Session.ParserVersion) || string.IsNullOrWhiteSpace(report.Session.SourceHash) || report.Session.SourceHash.Length != 64 || !report.Session.SourceHash.All(Uri.IsHexDigit) ||
            report.Contributors == null || report.Contributors.Count > 100000 || report.Incidents == null || report.Incidents.Count > 100000 ||
            report.Contributors.SelectMany(x => x.Evidence).Concat(report.Incidents.SelectMany(x => x.Evidence))
                .Any(x => x.TraceHash != report.Session.SourceHash || x.ParserVersion != report.Session.ParserVersion))
            throw new WoiException("InvalidAttachment", "ETL evidence references must retain the attached trace fingerprint and parser version.");
    }
    public SnapshotDetail Evidence(string id, SnapshotReference reference)
    {
        var document = Get(id);
        if (reference.SourceId != document.Source.Id) throw new WoiException("EvidenceSourceMismatch", "The reference belongs to another snapshot source.");
        var sample = document.Snapshots.FirstOrDefault(x => x.MonotonicTick == reference.MonotonicTick) ?? throw new WoiException("MissingEvidence", "The referenced snapshot is absent from this investigation.");
        var process = reference.ProcessLifetimeId == null ? null : sample.Processes.FirstOrDefault(x => x.LifetimeId == reference.ProcessLifetimeId) ?? throw new WoiException("UnknownLifetime", "The referenced process lifetime is absent from this snapshot.");
        return new(document.Source, reference with { Utc = sample.Utc }, sample.IntervalSeconds, sample.CpuPercent,
            sample.ReadBytesPerSecond, sample.WriteBytesPerSecond, sample.AvailablePhysicalBytes, sample.CommitBytes,
            sample.EnumeratedProcesses, sample.UnreadableProcesses, process, sample.Gaps);
    }
    public void Export(string id, string path, string format, bool includeLocalPaths = false)
    {
        var document = Get(id); var view = View(document);
        if (format is not ("json" or "markdown")) throw new WoiException("InvalidFormat", "Choose json or markdown.");
        if (!includeLocalPaths && view.AttachedTrace is { } trace)
            view = view with { AttachedTrace = trace with { Session = trace.Session with { SourcePath = "", CacheDirectory = "" } } };
        path = InvestigationStore.Local(path);
        if (format == "json") { File.WriteAllText(path, JsonSerializer.Serialize(view, new JsonSerializerOptions(Wire.Json) { WriteIndented = true })); return; }
        var i = view.Investigation;
        static string Clean(string text) => text.Replace("\r", " ").Replace("\n", " ").Replace("|", "\\|");
        var text = new StringBuilder("# Windows Open Intelligence investigation\n\n");
        text.AppendLine(Clean(i.Symptom)).AppendLine().AppendLine(FormattableString.Invariant($"Selected interval: {i.Window.StartSeconds:F3}–{i.Window.EndSeconds:F3}s from observation start; scope: {Clean(i.Scope)}."));
        text.AppendLine($"Source: {view.Source.Id} / {view.Source.Kind} / {view.Source.Version}");
        text.AppendLine("Snapshot times use recorded monotonic ticks and frequency. They have no ETLX event index.").AppendLine();
        foreach (var finding in i.Findings) {
            text.AppendLine("## " + finding.Title).AppendLine().AppendLine($"{finding.Provenance}: {finding.Statement}").AppendLine();
            foreach (var r in finding.Snapshots) text.AppendLine($"- Snapshot {r.SourceId}:{r.MonotonicTick}; UTC {r.Utc:O}; lifetime {r.ProcessLifetimeId ?? "system gauges"}.");
            foreach (var limitation in finding.Limitations) text.AppendLine("- " + limitation);
        }
        text.AppendLine().AppendLine("Next measurement: " + i.NextMeasurement).AppendLine();
        if (!string.IsNullOrWhiteSpace(i.Notes)) text.AppendLine("User notes: " + Clean(i.Notes)).AppendLine();
        foreach (var limitation in i.Limitations) text.AppendLine("- " + limitation);
        if (view.AttachedTrace is { } attached) {
            text.AppendLine().AppendLine("## User-attached ETL evidence").AppendLine().AppendLine($"SHA-256: {attached.Session.SourceHash}; parser: {attached.Session.ParserVersion}");
            if (includeLocalPaths) text.AppendLine("Original local path: " + attached.Session.SourcePath);
            text.AppendLine("This attachment is a separately recorded source. Attachment does not establish clock alignment, common machine identity, or causality.").AppendLine(attached.Explanation);
            foreach (var evidence in attached.Contributors.SelectMany(x => x.Evidence).DistinctBy(x => x.EtlxEventIndex).Take(30))
                text.AppendLine(FormattableString.Invariant($"- ETLX {evidence.EtlxEventIndex}; {evidence.TimeMs:F6}ms; {evidence.Provider}/{evidence.EventName}; source {evidence.TraceHash}."));
        }
        text.AppendLine().AppendLine("Sharing contains process names, source metadata, notes, and evidence coordinates. Raw ETL and the full snapshot evidence are excluded; review the report before sharing.");
        File.WriteAllText(path, text.ToString());
    }
    public void ExportComparison(ComparisonRequest request, string path, string format)
    {
        var comparison = Compare(request);
        var first = GetView(request.FirstId); var second = GetView(request.SecondId);
        InvestigationView Redact(InvestigationView value) => value.AttachedTrace == null ? value : value with {
            AttachedTrace = value.AttachedTrace with { Session = value.AttachedTrace.Session with { SourcePath = "", CacheDirectory = "" } }
        };
        path = InvestigationStore.Local(path);
        if (format == "json") {
            File.WriteAllText(path, JsonSerializer.Serialize(new { comparison, first = Redact(first), second = Redact(second) }, new JsonSerializerOptions(Wire.Json) { WriteIndented = true })); return;
        }
        if (format != "markdown") throw new WoiException("InvalidFormat", "Choose json or markdown.");
        static string Clean(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        static string Value(double? value) => value?.ToString("F3", System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable";
        var text = new StringBuilder("# Windows Open Intelligence attempt comparison\n\n");
        text.AppendLine("Task confirmed by user: " + Clean(comparison.Task)).AppendLine("User-stated change: " + Clean(comparison.Change)).AppendLine().AppendLine(comparison.Statement).AppendLine("Provenance: temporal correlation.").AppendLine();
        text.AppendLine($"First: {first.Investigation.Id}; source {first.Source.Id}; {first.Source.Kind}/{first.Source.Version}");
        text.AppendLine($"Second: {second.Investigation.Id}; source {second.Source.Id}; {second.Source.Kind}/{second.Source.Version}");
        if (!string.IsNullOrWhiteSpace(comparison.ApplicationContext)) text.AppendLine("User-confirmed application context: " + Clean(comparison.ApplicationContext));
        text.AppendLine().AppendLine("| Measurement | Unit | First | Second | Difference % | Comparable | ").AppendLine("|---|---|---:|---:|---:|---|");
        foreach (var metric in comparison.Metrics) text.AppendLine($"| {Clean(metric.Name)} | {metric.Unit} | {Value(metric.First)} | {Value(metric.Second)} | {Value(metric.ChangePercent)} | {metric.Comparable} |");
        text.AppendLine();
        foreach (var metric in comparison.Metrics) text.AppendLine("- " + metric.Name + ": " + metric.Limitation);
        foreach (var limitation in comparison.Limitations) text.AppendLine("- " + limitation);
        text.AppendLine().AppendLine("## Evidence references").AppendLine();
        foreach (var finding in first.Investigation.Findings.Concat(second.Investigation.Findings))
            foreach (var reference in finding.Snapshots) text.AppendLine($"- Snapshot {reference.SourceId}:{reference.MonotonicTick}; UTC {reference.Utc:O}; lifetime {reference.ProcessLifetimeId ?? "system gauges"}.");
        text.AppendLine().AppendLine("Raw ETL, full snapshot evidence, and original local trace paths are excluded. Notes, application context, and source references may contain private information; review before sharing.");
        File.WriteAllText(path, text.ToString());
    }
    private InvestigationDocument Get(string id) => documents.GetValueOrDefault(id) ?? throw new WoiException("MissingInvestigation", "Open or create this investigation in the current worker first.");
    private static InvestigationView View(InvestigationDocument document) => new(document.Investigation, document.Source, document.AttachedTrace);
}
