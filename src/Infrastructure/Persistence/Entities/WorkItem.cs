namespace PiAgent.Infrastructure.Persistence;

/// <summary>WorkItem discriminator values: GitHub Issue or Pull Request.</summary>
public static class WorkItemTypes
{
    public const string Issue = "issue";
    public const string PullRequest = "pull_request";
}

/// <summary>
/// A GitHub Issue or Pull Request. The Poller updates synced fields whenever GitHub reports
/// a newer `updated_at_github`; PR-only fields are maintained by the `pulls` stream only. Cleanup
/// fields (`cleanup_status`, `closed_at_local`) are set by later orchestration stages / on first
/// observation of closure.
/// </summary>
public class WorkItem
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public string GithubNodeId { get; set; } = null!;
    public long Number { get; set; }
    public string Type { get; set; } = null!;
    public string Title { get; set; } = null!;
    /// <summary>Latest synced body (edits applied on sync; never removed).</summary>
    public string? Body { get; set; }
    /// <summary>`open` | `closed` — a merged PR is closed for lifecycle purposes.</summary>
    public string State { get; set; } = null!;
    public string AuthorLogin { get; set; } = null!;
    /// <summary>Synced metadata, not a trigger; logins, jsonb.</summary>
    public string Assignees { get; set; } = null!;
    /// <summary>Synced metadata; label names, jsonb.</summary>
    public string Labels { get; set; } = null!;
    public DateTime CreatedAtGithub { get; set; }
    public DateTime UpdatedAtGithub { get; set; }
    public DateTime? ClosedAtGithub { get; set; }
    // ---- PR-only (nulls for Issues) ----
    public string? HeadSha { get; set; }
    public string? BaseSha { get; set; }
    public bool? Draft { get; set; }
    /// <summary>Kept for history; merged PR still gets `work_item_closed` with `close_reason = merged`.</summary>
    public DateTime? MergedAt { get; set; }
    /// <summary>Currently-requested reviewer logins, jsonb; snapshot-diffed for review_requested/removed.</summary>
    public string? RequestedReviewers { get; set; }
    public DateTime LastSyncedAt { get; set; }
    /// <summary>Orchestration-owned cleanup flag (root closure); NULL until then.</summary>
    public string? CleanupStatus { get; set; }
    /// <summary>First observation of closure by this system; stays after reopen (history).</summary>
    public DateTime? ClosedAtLocal { get; set; }
}
