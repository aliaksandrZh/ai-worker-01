using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// Queue gauges + counters for the health surface (filled by JobQueueWorker each tick; Orchestrator
/// surfaces them; the three null placeholders from task 01 become real values). Gauges are tick-sample
/// staleness-tolerant values (same contract as the other worker metrics).
/// </summary>
public sealed class JobQueueMetrics
{
    public long PendingJobs { get; private set; }
    public double OldestPendingJobAgeSeconds { get; private set; }
    public double ClaimWaitSeconds { get; private set; }
    public long ActiveRuns { get; private set; }

    public long JobsClaimedTotal => Interlocked.Read(ref _jobsClaimed);
    public long LeaseExpirationsTotal => Interlocked.Read(ref _leaseExpirations);
    public long ClaimCancellationsTotal => Interlocked.Read(ref _claimCancellations);

    private long _jobsClaimed;
    private long _leaseExpirations;
    private long _claimCancellations;

    public void RecordClaim() => Interlocked.Increment(ref _jobsClaimed);
    public void RecordLeaseExpiration() => Interlocked.Increment(ref _leaseExpirations);
    public void RecordClaimCancellation() => Interlocked.Increment(ref _claimCancellations);

    public void UpdateGauges(long pendingJobs, double oldestPendingAgeSeconds, double claimWaitSeconds, long activeRuns)
    {
        PendingJobs = pendingJobs;
        OldestPendingJobAgeSeconds = oldestPendingAgeSeconds;
        ClaimWaitSeconds = claimWaitSeconds;
        ActiveRuns = activeRuns;
    }
}
