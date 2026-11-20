using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Orchestrator.Commands;
using PiAgent.Platform.Http;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator;

public sealed class OrchestratorMetrics(WorkerLoopState state, CommandMetrics commandMetrics) : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new("commandsReceivedTotal", commandMetrics.CommandsReceivedTotal);
        yield return new("commandsAuthorizedTotal", commandMetrics.CommandsAuthorizedTotal);
        yield return new("commandsRejectedTotal", commandMetrics.CommandsRejectedTotal);
        yield return new("authorizationSystemErrorsTotal", commandMetrics.AuthorizationSystemErrorsTotal);
        // queue gauges stay null placeholders until the durable queue lands (task 07)
        yield return new KeyValuePair<string, object?>("queuePendingJobs", null);
        yield return new KeyValuePair<string, object?>("queueOldestJobAgeSeconds", null);
        yield return new KeyValuePair<string, object?>("queueClaimWaitSeconds", null);
    }
}
