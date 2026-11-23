namespace PiAgent.Infrastructure.Persistence;

/// <summary>Workspace lifecycle states: created `provisioning` by the Orchestrator on the
/// Task's first Run and reused by retries; the Runtime API moves it `ready` → `in_use` per attempt
/// and back to `ready` when the container stops.</summary>
public static class WorkspaceStatuses
{
    public const string Provisioning = "provisioning";
    public const string Ready = "ready";
    public const string InUse = "in_use";
    public const string Failed = "failed";
    public const string CleanupPending = "cleanup_pending";
    public const string Deleted = "deleted";
}

/// <summary>Storage kinds: plain host directories.</summary>
public static class WorkspaceStorageKinds
{
    public const string HostPath = "host_path";
}

/// <summary>Workspace access modes: read-write by default; reviewer workspaces are read-only where possible.</summary>
public static class WorkspaceAccessModes
{
    public const string ReadWrite = "read_write";
    public const string ReadOnly = "read_only";
}

/// <summary>File states tracked separately from the lifecycle status.</summary>
public static class WorkspaceStorageStatuses
{
    public const string Available = "available";
    public const string CleanupPending = "cleanup_pending";
    public const string Deleted = "deleted";
    public const string Missing = "missing";
}

/// <summary>
/// Persistent task-specific storage. One Workspace per Task (`task_id` unique); the DB row is
/// created by the Orchestrator on the Task's first Run and reused by retries (no new Task ever reuses
/// another Task's session or workspace); the Runtime API provisions the physical directory and writes
/// runtime metadata (storage reference, SHAs when Git data is available) back. Storage persists after
/// the container stops and is deleted at root closure.
/// </summary>
public class Workspace
{
    public Guid Id { get; set; }
    public Guid TaskId { get; set; }
    /// <summary>Denormalized reference for queries.</summary>
    public Guid WorkItemId { get; set; }
    public Guid RepositoryId { get; set; }
    public string StorageKind { get; set; } = WorkspaceStorageKinds.HostPath;
    /// <summary>Host storage path written back by the Runtime API at provisioning.</summary>
    public string? StorageReference { get; set; }
    /// <summary>Git facts from workspace provisioning; null for stub pipelines without a clone.</summary>
    public string? BranchName { get; set; }
    /// <summary>Default-branch HEAD captured at first provision; null when nothing was captured.</summary>
    public string? BaseSha { get; set; }
    public string? CurrentSha { get; set; }
    public string AccessMode { get; set; } = WorkspaceAccessModes.ReadWrite;
    public string Status { get; set; } = WorkspaceStatuses.Provisioning;
    public string StorageStatus { get; set; } = WorkspaceStorageStatuses.Available;
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public DateTime? DeletedAt { get; set; }
}
