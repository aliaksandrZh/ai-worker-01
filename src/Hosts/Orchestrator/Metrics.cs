using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.Platform.Http;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator;

public sealed class OrchestratorMetrics(WorkerLoopState state, CommandMetrics commandMetrics, JobQueueMetrics queueMetrics) : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new("commandsReceivedTotal", commandMetrics.CommandsReceivedTotal);
        yield return new("commandsAuthorizedTotal", commandMetrics.CommandsAuthorizedTotal);
        yield return new("commandsRejectedTotal", commandMetrics.CommandsRejectedTotal);
        yield return new("authorizationSystemErrorsTotal", commandMetrics.AuthorizationSystemErrorsTotal);
        // durable job queue (task 07)
        yield return new KeyValuePair<string, object?>("queuePendingJobs", queueMetrics.PendingJobs);
        yield return new KeyValuePair<string, object?>("queueOldestJobAgeSeconds", queueMetrics.OldestPendingJobAgeSeconds);
        yield return new KeyValuePair<string, object?>("queueClaimWaitSeconds", queueMetrics.ClaimWaitSeconds);
        yield return new KeyValuePair<string, object?>("activeRuns", queueMetrics.ActiveRuns);
        yield return new KeyValuePair<string, object?>("jobsClaimedTotal", queueMetrics.JobsClaimedTotal);
        yield return new KeyValuePair<string, object?>("leaseExpirationsTotal", queueMetrics.LeaseExpirationsTotal);
        yield return new KeyValuePair<string, object?>("claimCancellationsTotal", queueMetrics.ClaimCancellationsTotal);
    }
}
