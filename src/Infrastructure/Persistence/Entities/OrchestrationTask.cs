namespace PiAgent.Infrastructure.Persistence;

/// <summary>Task machine states (03 §2). The partial unique index `ux_task_one_unfinished_per_workflow`
/// applies to the four unfinished ones (`Unfinished`).</summary>
public static class OrchestrationTaskStatuses
{
    public const string Queued = "queued";
    public const string Claimed = "claimed";
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Blocked = "blocked";
    public const string Cancelled = "cancelled";

    /// <summary>States that occupy the Workflow's single unfinished-Task slot (02 §23, decision 79).</summary>
    public static readonly string[] Unfinished = [Queued, Claimed, Starting, Running];
}

/// <summary>Canonical task types (02 §10/§12a): command names map to `OrchestrationTaskTypes`.</summary>
public static class OrchestrationTaskTypes
{
    public const string Plan = "plan";
    public const string Implement = "implement";
    public const string Review = "review";
    public const string TestDesign = "test_design";
}

/// <summary>
/// A discrete operation within a Workflow (02 §10). Runs execute sequentially; `sequence` is unique per
/// Workflow and assigned by the Orchestrator. The profile version is pinned at creation; retry budget is
/// pinned as the effective policy (AgentProfileVersion defaults → system defaults, decision 28).
/// </summary>
public class OrchestrationTask
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    /// <summary>Unique per Workflow (02 §23); max+1 under the Workflow-row lock.</summary>
    public int Sequence { get; set; }
    /// <summary>`plan` | `implement` | `review` | `test_design` (02 §12a).</summary>
    public string Type { get; set; } = null!;
    /// <summary>Pinned at creation; claim-time recheck verifies it is still `ready` (decision 35).</summary>
    public Guid AgentProfileVersionId { get; set; }
    public string Status { get; set; } = null!;
    /// <summary>Command that produced this Task; null only for system-created Tasks (e.g. automatic initial review, ticket 12).</summary>
    public Guid? TriggerCommandId { get; set; }
    /// <summary>Review Tasks pin the PR Head SHA at creation; never rewritten on drift (decision 31).</summary>
    public string? TargetHeadSha { get; set; }
    /// <summary>Review Tasks pin the Base SHA used to form the diff locally (decision 64).</summary>
    public string? TargetBaseSha { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    /// <summary>Recorded reason code for `failed`/`cancelled` terminal states.</summary>
    public string? FailureReason { get; set; }
    /// <summary>Effective retry policy pinned at Task creation, jsonb (02 §10, decision 28).</summary>
    public string RetryPolicy { get; set; } = null!;
}
