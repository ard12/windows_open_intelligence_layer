namespace Woi.Core;

public record SourceDescriptor(string Id, string Kind, string Version, string Machine, string OperatingSystem,
    int LogicalProcessors, long MonotonicFrequency, long StartTick, DateTime StartUtc);
public record SnapshotReference(string SourceId, long MonotonicTick, DateTime Utc, string? ProcessLifetimeId = null);
public record InvestigationWindow(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}
public record InvestigationSettings(double CpuThreshold = 80, double SustainedSeconds = 3);
public record InvestigationRequest(InvestigationWindow Window, string Symptom,
    string? ProcessLifetimeId = null, string Notes = "", InvestigationSettings? Settings = null);
public record Measurement(string Name, double? Value, string Unit, double CoveredSeconds, string SourceId);
public record Finding(string Id, string Title, string Statement, Provenance Provenance,
    IReadOnlyList<Measurement> Measurements, IReadOnlyList<SnapshotReference> Snapshots,
    IReadOnlyList<string> Prerequisites, IReadOnlyList<string> Limitations, string NextMeasurement);
public record InvestigationMetrics(double DurationSeconds, double CpuCoveredSeconds, double? MeanCpuPercent,
    double? CpuSeconds, double ReadCoveredSeconds, double? ReadBytes, double WriteCoveredSeconds,
    double? WriteBytes, long? MinimumAvailableBytes, long? AvailableChangeBytes, long? CommitChangeBytes,
    long? PeakPrivateBytes, int MemorySnapshotCount = 0, int PrivateSnapshotCount = 0);
public record InvestigationContributor(string LifetimeId, int Pid, string Name, double? CpuSeconds,
    double? ReadBytes, double? WriteBytes, long? PeakPrivateBytes, SnapshotReference Evidence);
public record Investigation(string Id, string Symptom, InvestigationWindow Window, string? ProcessLifetimeId,
    string Scope, string Notes, InvestigationMetrics Metrics, InvestigationMetrics? PrecedingMetrics,
    IReadOnlyList<Finding> Findings, IReadOnlyList<InvestigationContributor> Contributors,
    IReadOnlyList<string> Limitations, string NextMeasurement, InvestigationSettings? Settings = null);
public record InvestigationDocument(int Schema, SourceDescriptor Source, Investigation Investigation,
    IReadOnlyList<MonitorSnapshot> Snapshots, Report? AttachedTrace = null);
public record InvestigationView(Investigation Investigation, SourceDescriptor Source, Report? AttachedTrace = null)
{
    public string DisplayLabel => $"{Investigation.Symptom} · {Investigation.Window.StartSeconds:F1}–{Investigation.Window.EndSeconds:F1}s from observation start · {Source.StartUtc:u}";
}
public record SnapshotDetail(SourceDescriptor Source, SnapshotReference Reference, double? IntervalSeconds,
    double? AccountedCpuPercent, double? AccountedReadBytesPerSecond, double? AccountedWriteBytesPerSecond,
    long? AvailablePhysicalBytes, long? CommitBytes, int EnumeratedProcesses, int UnreadableProcesses,
    MonitorProcess? Process, IReadOnlyList<ObservationGap> Gaps);
public record ComparisonRequest(string FirstId, string SecondId, string Task, string Change,
    bool SameTaskConfirmed, string? ApplicationContext = null, bool ApplicationContextConfirmed = false);
public record ComparisonMetric(string Name, string Unit, double? First, double? Second,
    double? ChangePercent, bool Comparable, string Limitation);
public record Comparison(string Id, string FirstId, string SecondId, string Task, string Change,
    string? ApplicationContext, string Statement, IReadOnlyList<ComparisonMetric> Metrics,
    IReadOnlyList<string> Limitations, Provenance Provenance = Provenance.TemporalCorrelation);
