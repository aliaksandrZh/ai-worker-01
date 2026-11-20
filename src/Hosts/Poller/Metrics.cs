using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Workers;

namespace PiAgent.Poller;

/// <summary>§14 baseline: GitHub polling metrics are null placeholders until the poller lands (task 04+).</summary>
public sealed class PollerMetrics(WorkerLoopState state) : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new KeyValuePair<string, object?>("pollerLagSeconds", null);
        yield return new KeyValuePair<string, object?>("pollerLastSuccessUtc", null);
        yield return new KeyValuePair<string, object?>("pollerLastCycleDurationSeconds", null);
        yield return new KeyValuePair<string, object?>("pollerErrorsTotal", 0L);
        yield return new KeyValuePair<string, object?>("pollerRateLimitRemaining", null);
    }
}
