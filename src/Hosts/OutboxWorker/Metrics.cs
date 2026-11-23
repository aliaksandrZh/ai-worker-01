using PiAgent.OutboxWorker.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Workers;

namespace PiAgent.OutboxWorker;

/// <summary>§14 baseline metrics: pending/failed/dead_letter counts from the dispatcher's OutboxState (task 03).</summary>
public sealed class OutboxMetrics(WorkerLoopState state, OutboxState outboxState) : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new("outboxPending", outboxState.Pending);
        yield return new("outboxFailed", outboxState.Failed);
        yield return new("outboxDeadLetter", outboxState.DeadLetter);
    }
}
