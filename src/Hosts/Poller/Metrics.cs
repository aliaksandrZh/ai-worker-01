using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Workers;

namespace PiAgent.Poller;

/// <summary>§14 poller metrics (live since task 04): cycle duration, success lag, error/rate-limit counters.</summary>
public sealed class PollerMetrics(WorkerLoopState state, PollerState pollerState) : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new KeyValuePair<string, object?>("pollerLagSeconds",
            pollerState.LastSuccessUtc is { } last
                ? (DateTime.UtcNow - last).TotalSeconds
                : null);
        yield return new KeyValuePair<string, object?>("pollerLastSuccessUtc", pollerState.LastSuccessUtc);
        yield return new KeyValuePair<string, object?>("pollerLastCycleDurationSeconds",
            pollerState.LastCycleDuration?.TotalSeconds);
        yield return new KeyValuePair<string, object?>("pollerErrorsTotal", pollerState.ErrorsTotal);
        yield return new KeyValuePair<string, object?>("pollerRateLimitRemaining", pollerState.RateLimitRemaining);
        yield return new KeyValuePair<string, object?>("pollerPausedUntil", pollerState.PauseUntil);
    }
}
