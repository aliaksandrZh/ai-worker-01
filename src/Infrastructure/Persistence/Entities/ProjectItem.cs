namespace PiAgent.Infrastructure.Persistence;

/// <summary>
/// Per-project sync run state (SyncCursor semantics, keyed by the user-owned Projects v2 board
/// the Poller is configured for): the ETag for conditional item listings plus last
/// attempt/success/error bookkeeping. Rows are created lazily by the projects pass.
/// </summary>
public class ProjectSync
{
    public Guid Id { get; set; }
    public string Owner { get; set; } = null!;
    public int ProjectNumber { get; set; }
    /// <summary>Opaque ETag of the item list; NULL until the first successful listing.</summary>
    public string? Etag { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One Projects v2 item snapshot (the detection table for the projects pass): the item's content
/// coordinates — Issue/PullRequest resolve to a tracked repository row + number, DraftIssue is
/// repositoryless and stays sync-only — plus the raw field list the update diff runs over.
/// The full listing is authoritative: an item absent from a successful listing is removed
/// together with its `project_item_removed` event.
/// </summary>
public class ProjectItem
{
    public Guid Id { get; set; }
    public Guid ProjectSyncId { get; set; }
    /// <summary>GitHub's numeric item id — stable across field edits, new after a re-add.</summary>
    public long ItemId { get; set; }
    public string? ItemNodeId { get; set; }
    /// <summary>Issue | PullRequest | DraftIssue (Projects v2 item content types).</summary>
    public string ContentType { get; set; } = null!;
    /// <summary>Set only when the content maps to a tracked repository (FK SetNull on repo delete).</summary>
    public Guid? RepositoryId { get; set; }
    public string? RepositoryFullName { get; set; }
    public int? Number { get; set; }
    public string Title { get; set; } = "";
    /// <summary>Raw field list JSON: [{"id":123,"name":"Status","value":"Ready"},..].</summary>
    public string FieldValues { get; set; } = "[]";
    public DateTime CreatedAtGithub { get; set; }
    public DateTime UpdatedAtGithub { get; set; }
    public DateTime LastSyncedAt { get; set; }
}
