namespace PiAgent.OutboxWorker;

/// <summary>
/// Mutable snapshot refreshed by the dispatcher each WorkerLoop tick and read by <see cref="OutboxMetrics"/>
/// (§14: outboxPending / outboxFailed / outboxDeadLetter). Failed stays 0 in V1 (ruling 7: the `failed`
/// status is reserved — transient failures reuse pending, terminal failures go to dead_letter).
/// </summary>
public sealed class OutboxState
{
    public long Pending { get; set; }

    public long Failed { get; set; }

    public long DeadLetter { get; set; }
}
