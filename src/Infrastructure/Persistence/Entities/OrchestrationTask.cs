namespace PiAgent.Infrastructure.Persistence;

/// <summary>Task machine states. The partial unique index `ux_task_one_unfinished_per_workflow`
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

    /// <summary>States that occupy the Workflow's single unfinished-Task slot.</summary>
    public static readonly string[] Unfinished = [Queued, Claimed, Starting, Running];
}

/// <summary>Canonical task types: command names map to `OrchestrationTaskTypes`.</summary>
public static class OrchestrationTaskTypes
{
    public const string Plan = "plan";
    public const string Implement = "implement";
    public const string Review = "review";
    public const string TestDesign = "test_design";
}

/// <summary>
/// A discrete operation within a Workflow. Runs execute sequentially; `sequence` is unique per
/// Workflow and assigned by the Orchestrator. The profile version is pinned at creation; retry budget is
/// pinned as the effective policy (AgentProfileVersion defaults → system defaults).
/// </summary>
public class OrchestrationTask
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    /// <summary>Unique per Workflow; max+1 under the Workflow-row lock.</summary>
    public int Sequence { get; set; }
    /// <summary>`plan` | `implement` | `review` | `test_design`.</summary>
    public string Type { get; set; } = null!;
    /// <summary>Pinned at creation; claim-time recheck verifies it is still `ready`.</summary>
    public Guid AgentProfileVersionId { get; set; }
    public string Status { get; set; } = null!;
    /// <summary>Command that produced this Task; null only for system-created Tasks (e.g. automatic initial review).</summary>
    public Guid? TriggerCommandId { get; set; }
    /// <summary>System-created Tasks (automatic initial review): the GitHub event that produced the
    /// Task (`review_requested`, or the draft→ready `work_item_updated`). Null for command-created Tasks.
    /// Unique where set — one event creates at most one Task.</summary>
    public Guid? TriggerGithubEventId { get; set; }
    /// <summary>GitHub-side time of the trigger (the no-new-input admission gate
    /// anchors "no new signal since" on it): the trigger comment's `CreatedAtGithub` for command Tasks, the
    /// triggering event's GitHub time (or local receipt) for system-created Tasks. Null for rows created
    /// before the watermark existed — the gate then never rejects.</summary>
    public DateTime? TriggerAtGithub { get; set; }
    /// <summary>Review Tasks pin the PR Head SHA at creation; never rewritten on drift.</summary>
    public string? TargetHeadSha { get; set; }
    /// <summary>Review Tasks pin the Base SHA used to form the diff locally.</summary>
    public string? TargetBaseSha { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    /// <summary>Recorded reason code for `failed`/`cancelled` terminal states.</summary>
    public string? FailureReason { get; set; }
    /// <summary>Effective retry policy pinned at Task creation, jsonb.</summary>
    public string RetryPolicy { get; set; } = null!;
    /// <summary>Budget: automatic retries spent over the Task's lifetime (manual /retry
    /// attempts never consume it). Checked and incremented by the terminal-Run consumer.</summary>
    public int AutomaticRetriesUsed { get; set; }
}
