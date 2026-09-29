using System.Text.Json;
using System.Text.Json.Serialization;

namespace Woi.Core;

public static class Wire
{
    public const int Version = 1;
    public const string AnalysisVersion = "1.1.1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        IncludeFields = true,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };
}
public enum Availability { Available, NotCaptured, Unsupported, Unresolved }
public enum Provenance { Observed, ResourceAttribution, TemporalCorrelation, Hypothesis }
public record Capability(string Category, Availability Status, long Events, string Detail);
public record EvidenceReference(string TraceHash, string ParserVersion, long EtlxEventIndex,
    long TimestampQpc, double TimeMs, string Provider, string EventName, int Pid, int Tid);
public record EntityLifetime(string Id, string Kind, int OsId, string Name, double StartMs, double EndMs,
    bool StartKnown, bool EndKnown, string? OwnerId, string? ParentId, string ImagePath, string CommandLine)
{
    public string DisplayLabel => $"{Name} · PID {OsId} · {StartMs/1000:F3}s";
}
public record TraceSession(string Id, string SourcePath, string SourceHash, string ParserVersion,
    string AnalysisVersion, string CacheDirectory, DateTime StartUtc, double DurationMs, int ProcessorCount,
    long IndexedEvents, int ReportedLostEvents, IReadOnlyList<Capability> Capabilities,
    IReadOnlyList<string> Warnings, double ImportSeconds, long PeakWorkingSetBytes, long RecordedQpcFrequency = 0);
public record Observation(long Index, double TimeMs, long Qpc, string Category, string Operation,
    string? ProcessId, string? ThreadId, int Pid, int Tid, string ProcessName, string Target,
    long? Bytes, double? DurationMs, double? CpuWeightMs, double? WorkingSetBytes,
    EvidenceReference Evidence);
public record MetricPoint(double TimeMs, long? CpuSamples, double? CpuPercent, long? DiskBytes,
    double? DiskP95Ms, long? FileBytes, long? NetworkBytes, double? WorkingSetBytes, long? DiskOperations);
public record Contributor(string? ProcessId, string Name, int? Pid, long? CpuSamples, double? CpuMs,
    double? CpuPercent, long? DiskBytes, long? FileBytes, long? NetworkBytes, double? RecordedIoMs,
    double? BaselineCpuPercent, IReadOnlyList<EvidenceReference> Evidence);
public record Incident(string Id, string Category, string Title, double StartMs, double EndMs,
    string Explanation, Provenance Provenance, IReadOnlyList<EvidenceReference> Evidence);
public record DetectionSettings(double CpuThreshold = 80, double DiskLatencyThresholdMs = 20,
    int ConsecutiveSeconds = 3, int MinimumDiskOperations = 10);
public record IntervalQuery(double StartMs = 0, double EndMs = double.MaxValue, string? ProcessId = null, string Ranking = "CPU");
public record EventQuery(double StartMs = 0, double EndMs = double.MaxValue, string? ProcessId = null,
    string? Category = null, string? Operation = null, string? Target = null, long? Cursor = null, int Limit = 500);
public record EventPage(IReadOnlyList<Observation> Events, long? NextCursor, bool More);
public record EvidenceDetail(Observation Observation, IReadOnlyDictionary<string, string> Payload,
    IReadOnlyList<string> Stack, string Coordinates, string Interpretation, Provenance Provenance = Provenance.Observed);
public record EntityDetail(EntityLifetime Entity, IReadOnlyList<EntityLifetime> Threads,
    IReadOnlyList<string> Modules, IReadOnlyList<Contributor> Contributors);
public record Summary(TraceSession Session, IReadOnlyList<EntityLifetime> Processes,
    IReadOnlyList<Incident> Incidents, DetectionSettings Settings);
public record ImportProgress(string Stage, string Message, double? Percent = null);
public record Request(string Id, int Version, string Method, JsonElement Parameters);
public record Response(string Id, int Version, string Kind, object? Result = null,
    string? Error = null, string? Code = null);
public sealed class WoiException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
public record Bookmark(string Id, string Label, double StartMs, double EndMs);
public record Explanation(Provenance Provenance, string Text, IReadOnlyList<EvidenceReference> Evidence);
public record Report(TraceSession Session, IntervalQuery Interval, string Explanation,
    IReadOnlyList<Contributor> Contributors, IReadOnlyList<Incident> Incidents,
    IReadOnlyList<string> Limitations, Provenance Provenance = Provenance.ResourceAttribution, IReadOnlyList<Explanation>? Findings = null);
public record AggregateRow(int Second, string ProcessId, long CpuSamples, double CpuMs, long WeightedSamples,
    long DiskBytes, long FileBytes, long NetworkBytes, double IoMs, long DiskOperations,
    double? DiskP95Ms, double? WorkingSetBytes, IReadOnlyList<EvidenceReference> Evidence);
public interface ITraceReader : IDisposable
{
    TraceSession Session { get; }
    IEnumerable<Observation> ReadObservations(IntervalQuery query, CancellationToken token);
    IEnumerable<Observation> ReadContributorObservations(IntervalQuery query,CancellationToken token) =>
        ReadObservations(query,token).Where(x=>x.Category is "CPU" or "Disk" or "File" or "Network");
    EventPage QueryEvents(EventQuery query, CancellationToken token);
    EvidenceDetail GetEvidence(long index, string? localSymbolDirectory, CancellationToken token);
    EntityDetail GetEntity(string id, CancellationToken token);
}

public record CacheStatistics(long EntityCount, long RowCount, long CpuSamples, long DiskOperations,
    long DiskBytes, long FileBytes, long NetworkBytes);
