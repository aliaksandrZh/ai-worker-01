namespace PiAgent.Orchestrator.Builds;

/// <summary>§14 orchestrator build-flow counters (02 §12b/decision 81 bookkeeping).</summary>
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
