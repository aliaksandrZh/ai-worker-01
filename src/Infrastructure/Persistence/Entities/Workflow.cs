namespace PiAgent.Infrastructure.Persistence;

/// <summary>Workflow lifecycle statuses. `failed` is terminal-intervention.</summary>
public static class WorkflowStatuses
{
    public const string WaitingForHuman = "waiting_for_human";
    public const string Active = "active";
    public const string Failed = "failed";
    public const string Closed = "closed";
}

/// <summary>
/// One long-lived orchestration lifecycle per root WorkItem — `work_item_id` is unique;
/// a reopened root never resurrects a `closed` Workflow. The Orchestrator owns every transition.
/// </summary>
public class Workflow
{
    public Guid Id { get; set; }
    public Guid WorkItemId { get; set; }
    public string Status { get; set; } = null!;
    /// <summary>Set when a Task is the Workflow's open work; cleared when it turns terminal.</summary>
    public Guid? CurrentTaskId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? FailureReason { get; set; }
    /// <summary>Set when the automatic initial review Task is created (dedupe).</summary>
    public DateTime? InitialReviewCreatedAt { get; set; }
}
