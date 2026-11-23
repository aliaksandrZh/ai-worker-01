namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// RunEvent types; names normalized per the telemetry mapping, one row per delivered
/// callback. Persisted by the Runtime API before any acknowledgement; unique `(run_id, event_id)`
/// is the idempotency key.
/// </summary>
public static class RunEventTypes
{
    public const string AgentStarted = "agent_started";
    public const string TurnStarted = "turn_started";
    public const string TurnCompleted = "turn_completed";
    public const string AssistantOutput = "assistant_output";
    public const string ToolStarted = "tool_started";
    public const string ToolProgress = "tool_progress";
    public const string ToolCompleted = "tool_completed";
    public const string AgentSettled = "agent_settled";
    public const string Heartbeat = "heartbeat";
    public const string PermissionBlocked = "permission_blocked";
    public const string ArtifactProduced = "artifact_produced";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    /// <summary>The route accepts only this set (persistence contract).</summary>
    public static readonly string[] All =
    [
        AgentStarted, TurnStarted, TurnCompleted, AssistantOutput, ToolStarted, ToolProgress,
        ToolCompleted, AgentSettled, Heartbeat, PermissionBlocked, ArtifactProduced, Completed, Failed, Cancelled,
    ];

    /// <summary>Progress types: assistant text deltas (aggregated), tool start/update/end,
    /// turn completion, artifact creation — heartbeat, thinking deltas, and liveness never count.</summary>
    public static readonly string[] Progress =
        [TurnCompleted, AssistantOutput, ToolStarted, ToolProgress, ToolCompleted, ArtifactProduced];
}

/// <summary>
/// One delivered callback. `event_id` is the sender's idempotency key; terminal callbacks carry
/// the full body in `payload` (the frozen facts the Orchestrator's terminal consumer reads — e.g. the
/// `outcome` of a `completed`).
/// </summary>
public class RunEvent
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    /// <summary>Runtime-generated idempotency key; API-generated `artifact_produced` entries use the `artifact:<id>` namespace.</summary>
    public string EventId { get; set; } = null!;
    public string Type { get; set; } = null!;
    public string? Payload { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime ReceivedAt { get; set; }
}
