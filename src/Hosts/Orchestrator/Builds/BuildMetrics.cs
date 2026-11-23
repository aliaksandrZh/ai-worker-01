namespace PiAgent.Orchestrator.Builds;

/// <summary>Orchestrator build-flow counters.</summary>
public sealed class BuildMetrics
{
    public int BuildJobsPending;
    public int BuildJobsPolling;
    public double OldestPendingBuildAgeSeconds;
    public long BuildsDispatchedTotal;
    public long BuildsCompletedTotal;
    public long BuildsFailedTotal;
    public long TransientBuildErrorsTotal;
    public long InvalidPollResultsTotal;
}
