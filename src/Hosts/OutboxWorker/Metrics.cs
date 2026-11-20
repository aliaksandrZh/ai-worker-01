using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Platform.Workers;

namespace PiAgent.OutboxWorker;

/// <summary>§14 baseline: outbox metrics are null placeholders until the outbox lands (task 03+).</summary>
public sealed class OutboxMetrics(WorkerLoopState state) : WorkerMetricsBase(state)
{
    protected override IEnumerable<KeyValuePair<string, object?>> CollectExtra()
    {
        yield return new KeyValuePair<string, object?>("outboxPending", null);
        yield return new KeyValuePair<string, object?>("outboxFailed", null);
        yield return new KeyValuePair<string, object?>("outboxDeadLetter", null);
    }
}
