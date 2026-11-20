namespace PiAgent.Infrastructure.Persistence;

/// <summary>Normalized detection events (02 §7, decision 78 names). The `*CommentDeleted` variants are
/// produced by the deleted-comment sweep (ticket 05); the rest are produced by tickets 04 streams.</summary>
public static class GitHubEventTypes
{
    public const string WorkItemCreated = "work_item_created";
    public const string WorkItemUpdated = "work_item_updated";
    public const string WorkItemClosed = "work_item_closed";
    public const string IssueCommentCreated = "issue_comment_created";
    public const string IssueCommentUpdated = "issue_comment_updated";
    public const string IssueCommentDeleted = "issue_comment_deleted";
    public const string ReviewCommentCreated = "review_comment_created";
    public const string ReviewCommentUpdated = "review_comment_updated";
    public const string ReviewCommentDeleted = "review_comment_deleted";
    public const string PullRequestReviewSubmitted = "pull_request_review_submitted";
    public const string PullRequestReviewDismissed = "pull_request_review_dismissed";
    public const string ReviewRequested = "review_requested";
    public const string ReviewRequestRemoved = "review_request_removed";
    public const string PullRequestSynchronized = "pull_request_synchronized";
}

/// <summary>Detection path enumeration (02 §7/decision 78). Ticket 04 emits rest_resource_sync and
/// snapshot_diff; timeline_api and full_reconciliation arrive with ticket 05.</summary>
public static class GitHubEventSources
{
    public const string RestResourceSync = "rest_resource_sync";
    public const string TimelineApi = "timeline_api";
    public const string SnapshotDiff = "snapshot_diff";
    public const string FullReconciliation = "full_reconciliation";
}

/// <summary>Orchestrator-driven lifecycle (ticket 07+); the Poller always writes `pending`.</summary>
public static class GitHubEventStatuses
{
    public const string Pending = "pending";
    public const string Processed = "processed";
    public const string Failed = "failed";
}

/// <summary>
/// A change the Poller detected (02 §7): one row per detection with a decision-78 normalized event_type,
/// a JSONB payload describing the change, and a `dedupe_key` — stable external ID or deterministic
/// fingerprint — unique per (source, dedupe_key). WorkItemId is nullable until resolved; FK SetNull.
/// </summary>
public class GitHubEvent
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public Guid? WorkItemId { get; set; }
    public string Source { get; set; } = null!;
    public string? ExternalEventId { get; set; }
    public string? DedupeKey { get; set; }
    public string EventType { get; set; } = null!;
    public string Payload { get; set; } = null!;
    public DateTime? OccurredAtGithub { get; set; }
    public DateTime ReceivedAt { get; set; }
    public string ProcessingStatus { get; set; } = null!;
    public DateTime? ProcessedAt { get; set; }
    public string? ProcessingError { get; set; }
}
