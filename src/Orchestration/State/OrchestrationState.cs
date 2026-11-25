using PiAgent.Events;

namespace PiAgent.Orchestration.State;

/// <summary>docs/orchestrator.md §3 — the Workflow → Task → Run hierarchy.</summary>
public sealed record WorkflowRow(
    long Id,
    string Owner,
    string Repo,
    long ItemNumber,
    string Status,
    string? NodeId,
    bool PlanApproved,
    int ReviewRounds,
    string? AgentBranch,
    string? HeadSha)
{
    public ItemRef Item => new(Owner, Repo, ItemNumber);
}

public sealed record TaskRow(long Id, long WorkflowId, int Seq, string Type, string Status);

public sealed record RunRow(long Id, long TaskId, string Status, RunInput Input);

/// <summary>Task types = flow stages (docs/orchestrator.md §3).</summary>
public static class TaskTypes
{
    public const string Plan = "plan";
    public const string Implement = "implement";
    public const string Review = "review";
    public const string TestDesign = "test_design";

    /// <summary>The fixed flow order: development → review → testing (plan is command-driven).</summary>
    public static string? Next(string type) => type switch
    {
        Implement => Review,
        Review => TestDesign,
        _ => null,
    };
}

/// <summary>Task machine states (docs/orchestrator.md §3 — simplified target).</summary>
public static class TaskStatuses
{
    public const string Queued = "queued";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string WaitingForHuman = "waiting_for_human";
}

public static class WorkflowStatuses
{
    public const string Active = "active";
    public const string Cancelled = "cancelled";
    public const string Closed = "closed";
}

public static class RunStatuses
{
    public const string Scheduled = "scheduled";
    public const string Processing = "processing";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>Task states that occupy the Workflow's single active slot.</summary>
public static class OccupiedTaskStatuses
{
    public static readonly HashSet<string> Set =
    [
        TaskStatuses.Queued, TaskStatuses.Processing, TaskStatuses.WaitingForHuman,
    ];
}
