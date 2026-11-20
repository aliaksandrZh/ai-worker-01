namespace PiAgent.Infrastructure.Persistence;

/// <summary>Workflow lifecycle statuses (02 §9, 03 §1). `failed` is terminal-intervention (decision 71).</summary>
public static class WorkflowStatuses
{
    public const string WaitingForHuman = "waiting_for_human";
    public const string Active = "active";
    public const string Failed = "failed";
    public const string Closed = "closed";
}

/// <summary>
/// One long-lived orchestration lifecycle per root WorkItem in V1 — `work_item_id` is unique (02 §9);
/// a reopened root never resurrects a `closed` Workflow (decision 73). The Orchestrator owns every transition (03 §1).
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
    /// <summary>Set when the automatic initial review Task is created (dedupe; ticket 12 writes it).</summary>
    public DateTime? InitialReviewCreatedAt { get; set; }
}
