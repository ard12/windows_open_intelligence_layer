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
    public void Export(string id, string path, string format, bool includeLocalPaths = false, ExportPrivacyOptions? privacy = null) =>
        ReportExport.Write(path, format, GetView(id), privacy ?? new(IncludePaths: includeLocalPaths));
    public AttemptComparisonExport ComparisonReport(ComparisonRequest request) =>
        new(Compare(request), GetView(request.FirstId), GetView(request.SecondId));
    public void ExportComparison(ComparisonRequest request, string path, string format, ExportPrivacyOptions? privacy = null) =>
        ReportExport.Write(path, format, ComparisonReport(request), privacy);
    private InvestigationDocument Get(string id) => documents.GetValueOrDefault(id) ?? throw new WoiException("MissingInvestigation", "Open or create this investigation in the current worker first.");
    private static InvestigationView View(InvestigationDocument document) => new(document.Investigation, document.Source, document.AttachedTrace);
}
