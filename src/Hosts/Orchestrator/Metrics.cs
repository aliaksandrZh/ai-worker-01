using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Orchestrator.Builds;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.Platform.Http;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator;

public sealed class OrchestratorMetrics(WorkerLoopState state, CommandMetrics commandMetrics, JobQueueMetrics queueMetrics, BuildMetrics buildMetrics) : WorkerMetricsBase(state)
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
        // context assembler
        yield return new KeyValuePair<string, object?>("claimContextTooLargeTotal", queueMetrics.ClaimContextTooLargeTotal);
        // profile image builds
        yield return new KeyValuePair<string, object?>("buildJobsPending", buildMetrics.BuildJobsPending);
        yield return new KeyValuePair<string, object?>("buildJobsPolling", buildMetrics.BuildJobsPolling);
        yield return new KeyValuePair<string, object?>("oldestPendingBuildAgeSeconds", buildMetrics.OldestPendingBuildAgeSeconds);
        yield return new KeyValuePair<string, object?>("buildsDispatchedTotal", buildMetrics.BuildsDispatchedTotal);
        yield return new KeyValuePair<string, object?>("buildsCompletedTotal", buildMetrics.BuildsCompletedTotal);
        yield return new KeyValuePair<string, object?>("buildsFailedTotal", buildMetrics.BuildsFailedTotal);
        yield return new KeyValuePair<string, object?>("buildTransientErrorsTotal", buildMetrics.TransientBuildErrorsTotal);
    }
}
