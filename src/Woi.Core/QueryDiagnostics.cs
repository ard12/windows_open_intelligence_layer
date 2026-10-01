namespace Woi.Core;

// Developer measurements only; they do not alter resource calculations.
public record ContributorQueryProfile(double TotalMilliseconds, double SelectedTotalsMilliseconds,
    double SelectedEdgeReplayMilliseconds, double BaselineTotalsMilliseconds, double BaselineEdgeReplayMilliseconds,
    double RankingMilliseconds, long SelectedEdgeObservations, long BaselineEdgeObservations,
    long? VisitedRecords, long AllocatedBytes);
public interface IContributorQueryDiagnostics
{
    long ContributorRecordsVisited { get; }
}

// Per-page worker measurements; serialization, IPC and client decoding are excluded.
public record EventPageQueryProfile(double TotalMilliseconds, double NormalizationMilliseconds,
    double TraversalAndFilteringMilliseconds, long VisitedRecords, int NormalizedRecords,
    int ReturnedRecords, bool ContinuedIterator, bool ReusedLookahead, bool Completed, long AllocatedBytes);
public interface IEventQueryDiagnostics
{
    EventPageQueryProfile? LastEventPageProfile { get; }
}
