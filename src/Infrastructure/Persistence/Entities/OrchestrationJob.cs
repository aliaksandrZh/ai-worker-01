namespace PiAgent.Infrastructure.Persistence;

/// <summary>Durable-queue record states (02 §19, 03 §8): an expired `processing` lease returns a
/// retryable job to `pending`.</summary>
public static class OrchestrationJobStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>Job kinds (02 §19). V1 runs one kind: execute the job's Task.</summary>
public static class OrchestrationJobTypes
{
    public const string RunTask = "run_task";
}

/// <summary>
/// Durable queue record (02 §19). Queue order is FIFO by `available_at`, then `priority` (descending);
/// `/stop` cancellation runs outside the queue as an immediate control path (decision 38). Claiming is
/// transactional with a lease (`locked_until`/`locked_by`); expired leases return the job to `pending`.
/// Queued/`pending` work occupies no capacity slot — only claimed/starting/running/stopping runs do
/// (decisions 10, 75).
/// </summary>
public class OrchestrationJob
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    public string JobType { get; set; } = OrchestrationJobTypes.RunTask;
    public string Status { get; set; } = OrchestrationJobStatuses.Pending;
    /// <summary>Higher runs first within the same `available_at`; V1 creation uses 0.</summary>
    public int Priority { get; set; }
    public int Attempts { get; set; }
    /// <summary>A job created later re-enters the FIFO at its new `available_at` (e.g. retries, ticket 15).</summary>
    public DateTime AvailableAt { get; set; }
    public DateTime? LockedUntil { get; set; }
    public string? LockedBy { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
