namespace PiAgent.Infrastructure.Persistence;

/// <summary>OutboxAction delivery states. `failed` is reserved — never produced
/// (transient failures reuse `pending` with a future `available_at`; terminal failures go to
/// `dead_letter`).</summary>
public static class OutboxStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string DeadLetter = "dead_letter";
    public const string Cancelled = "cancelled";
}

/// <summary>Outbox delivers orchestration-owned comments only (agent GitHub writes go through GitHub MCP).</summary>
public static class OutboxActionTypes
{
    public const string Comment = "comment";
}

/// <summary>
/// Reliable external write queue for orchestration-owned comments. WorkItemId/TaskId are
/// plain Guids (the referencing rows live in other services' tables). Lease columns
/// (LockedBy/LockedUntil) implement a DB lease — one dispatcher owns one action at a time.
/// </summary>
public class OutboxAction
{
    public Guid Id { get; set; }

    public Guid RepositoryId { get; set; }

    public Guid WorkItemId { get; set; }

    public Guid? TaskId { get; set; }

    public string ActionType { get; set; } = OutboxActionTypes.Comment;

    /// <summary>jsonb. Contract: { "issue_number": &lt;long&gt;, "text": &lt;string&gt; }.</summary>
    public string Payload { get; set; } = null!;

    public string Status { get; set; } = OutboxStatuses.Pending;

    public int Attempts { get; set; }

    public DateTime AvailableAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>Unique. Carried inside the invisible HTML-comment marker of the delivered comment.</summary>
    public string IdempotencyKey { get; set; } = null!;

    /// <summary>DB lease: dispatcher instance currently owning the action.</summary>
    public Guid? LockedBy { get; set; }

    public DateTime? LockedUntil { get; set; }

    public DateTime CreatedAt { get; set; }
}
