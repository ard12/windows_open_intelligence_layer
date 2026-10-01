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
