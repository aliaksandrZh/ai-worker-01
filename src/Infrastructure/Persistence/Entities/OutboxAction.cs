namespace PiAgent.Infrastructure.Persistence;

/// <summary>OutboxAction delivery states (03 §8). `failed` is reserved — V1 never produces it
/// (transient failures reuse `pending` with a future `available_at`; terminal failures go to
/// `dead_letter`, decision 67).</summary>
public static class OutboxStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string DeadLetter = "dead_letter";
    public const string Cancelled = "cancelled";
}

/// <summary>Outbox delivers orchestration-owned comments only (02 §20; agent GitHub writes go through GitHub MCP).</summary>
public static class OutboxActionTypes
{
    public const string Comment = "comment";
}

/// <summary>
/// Reliable external write queue for orchestration-owned comments (02 §20). WorkItemId/TaskId are
/// plain Guids until tickets 04/07 add the orchestration tables and their FKs. Lease columns
/// (LockedBy/LockedUntil) implement decision 67's DB lease — one dispatcher owns one action at a time.
/// </summary>
public class OutboxAction
{
    public Guid Id { get; set; }

    public Guid RepositoryId { get; set; }

    public Guid WorkItemId { get; set; }

    public Guid? TaskId { get; set; }

    public string ActionType { get; set; } = OutboxActionTypes.Comment;

    /// <summary>jsonb. V1 contract: { "issue_number": &lt;long&gt;, "text": &lt;string&gt; }.</summary>
    public string Payload { get; set; } = null!;

    public string Status { get; set; } = OutboxStatuses.Pending;

    public int Attempts { get; set; }

    public DateTime AvailableAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>Unique (02 §23). Carried inside the invisible HTML-comment marker of the delivered comment.</summary>
    public string IdempotencyKey { get; set; } = null!;

    /// <summary>Decision-67 DB lease: dispatcher instance currently owning the action.</summary>
    public Guid? LockedBy { get; set; }

    public DateTime? LockedUntil { get; set; }

    public DateTime CreatedAt { get; set; }
}
