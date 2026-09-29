namespace Woi.Core;

public record ObservationGap(DateTime Utc, string Kind, string Detail);
public record MonitorProcess(string LifetimeId, int Pid, int SessionId, long CreationFileTime,
    string Name, double? CpuPercent, double? ReadBytesPerSecond, double? WriteBytesPerSecond,
    long? PrivateBytes, long? WorkingSetBytes);
public record MonitorSnapshot(long MonotonicTick, DateTime Utc, double? IntervalSeconds, double? CpuPercent, double? ReadBytesPerSecond, double? WriteBytesPerSecond,
    long? AvailablePhysicalBytes, long? CommitBytes, int EnumeratedProcesses, int UnreadableProcesses,
    IReadOnlyList<MonitorProcess> Processes, IReadOnlyList<ObservationGap> Gaps);
public record MonitorPoint(DateTime Utc, double? IntervalSeconds, double? CpuPercent, double? ReadBytesPerSecond, double? WriteBytesPerSecond, long? AvailablePhysicalBytes,
    long? CommitBytes, int EnumeratedProcesses, int UnreadableProcesses);
public record MonitorMark(string Id, long MonotonicTick, DateTime Utc, string Label);
public record MonitorView(DateTime StartedUtc, DateTime? LatestUtc, double RetainedSeconds,
    int SnapshotCount, int RetainedProcessRows, long EstimatedRetainedBytes,
    IReadOnlyList<MonitorPoint> History, IReadOnlyList<MonitorProcess> TopProcesses,
    IReadOnlyList<MonitorMark> Marks, IReadOnlyList<ObservationGap> RecentGaps)
{
    public string Source => "WindowsApiSnapshot";
}
public record MonitorLifetime(string LifetimeId, int Pid, int SessionId, long CreationFileTime,
    string Name, DateTime FirstObservedUtc, DateTime LastObservedUtc, bool PresentAtLatest, int SnapshotCount);
