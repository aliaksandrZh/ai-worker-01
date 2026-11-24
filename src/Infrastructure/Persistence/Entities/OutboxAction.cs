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

/// <summary>Outbox delivers orchestration-owned GitHub writes: system comments (the agent's own
/// writes go through GitHub MCP), the AI-lifecycle label transitions, and the AI-driven board
/// status moves (Projects v2 GraphQL).</summary>
public static class OutboxActionTypes
{
    public const string Comment = "comment";
    /// <summary>Payload { issue_number, label } — `label = null` clears all four AI labels.
    /// Applied as DELETE (the other three; 404 = fine) + POST (the target).</summary>
    public const string AiLabel = "ai_label";
    /// <summary>Payload { issue_number, label, add } — add/remove ONE named label (the review
    /// loop's `Code changes are Required`). Idempotent by construction: POST on a present label
    /// converges, DELETE on an absent one answers 404 = fine.</summary>
    public const string Label = "label";
    /// <summary>Payload { owner, project_number, item_id, field, status } — sets the board's
    /// single-select `field` to the `status` option on project item `item_id` (GraphQL
    /// updateProjectV2ItemFieldValue; the option id resolves by name at delivery time). Idempotent
    /// by construction: setting the same value twice converges.</summary>
    public const string ProjectStatus = "project_status";
}

/// <summary>One AI-driven board status move: the `owner/number` board's item `itemId` gets its
/// single-select field `field` set to option `status` (exact-case name). The Orchestrator resolves
/// everything except the option id (which only GitHub knows for the field schema) at enqueue time;
/// the Outbox Worker resolves the field/option ids via GraphQL at delivery time.</summary>
public sealed record ProjectStatusEnqueueRequest(
    Guid RepositoryId, Guid WorkItemId, Guid? TaskId, long IssueNumber,
    string Owner, int ProjectNumber, long ItemId, string? ItemNodeId, string Field, string Status);

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
