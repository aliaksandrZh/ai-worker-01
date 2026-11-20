namespace PiAgent.Infrastructure.Persistence;

/// <summary>Session record states (04 §3, 03 §4 prose): `provisioning` at Orchestrator creation, then
/// `in_use` when the Run starts, `terminated` at a Run terminal state. `cleanup_pending`/`deleted` are
/// file states tracked in `storage_status`, not session states (03 §4).</summary>
public static class AgentSessionStatuses
{
    public const string Provisioning = "provisioning";
    public const string InUse = "in_use";
    public const string Terminated = "terminated";
}

/// <summary>Backing-file states tracked separately from the session record (02 §13, 03 §4).</summary>
public static class AgentSessionStorageStatuses
{
    public const string Available = "available";
    public const string CleanupPending = "cleanup_pending";
    public const string Deleted = "deleted";
    public const string Missing = "missing";
}

/// <summary>
/// One Pi session per Run attempt — 1:1 (`run_id` unique), one-shot, never pooled or reassigned; a
/// terminated session never returns to service (decision 57). The DB row is created by the Orchestrator
/// (`provisioning`) when the Run is prepared; the Runtime API provisions the backing files and writes
/// runtime metadata back. The record persists after file deletion.
/// </summary>
public class AgentSession
{
    public Guid Id { get; set; }
    /// <summary>The single Run this session has ever executed (02 §13, decision 57).</summary>
    public Guid RunId { get; set; }
    /// <summary>Denormalized reference for queries (02 §13); never changed after creation.</summary>
    public Guid TaskId { get; set; }
    public Guid AgentProfileVersionId { get; set; }
    /// <summary>Runtime metadata written back by the Runtime API.</summary>
    public string? RuntimeSessionId { get; set; }
    /// <summary>Actual path discovered by the Bridge (decision 57).</summary>
    public string? SessionFilePath { get; set; }
    public string Status { get; set; } = AgentSessionStatuses.Provisioning;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
    public DateTime? LastHeartbeatAt { get; set; }
    public DateTime? FileDeletedAt { get; set; }
    public string StorageStatus { get; set; } = AgentSessionStorageStatuses.Available;
    public string? Metadata { get; set; }
}
