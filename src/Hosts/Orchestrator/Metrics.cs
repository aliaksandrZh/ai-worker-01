using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Http;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator;

/// <summary>§14 baseline: queue metrics are null placeholders until the queue tables land (task 03+).</summary>
public sealed class OrchestratorMetrics(WorkerLoopState state) : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new KeyValuePair<string, object?>("queuePendingJobs", null);
        yield return new KeyValuePair<string, object?>("queueOldestJobAgeSeconds", null);
        yield return new KeyValuePair<string, object?>("queueClaimWaitSeconds", null);
    }
}
