using PiAgent.OutboxWorker.GitHub;
namespace PiAgent.OutboxWorker;

/// <summary>
/// Mutable snapshot refreshed by the dispatcher each WorkerLoop tick and read by <see cref="OutboxMetrics"/>
/// (outboxPending / outboxFailed / outboxDeadLetter). Failed stays 0 (the `failed`
/// status is reserved — transient failures reuse pending, terminal failures go to dead_letter).
/// </summary>
public sealed class OutboxState
{
    public long Pending { get; set; }

    public long Failed { get; set; }

    public long DeadLetter { get; set; }
}
