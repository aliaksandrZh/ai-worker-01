namespace PiAgent.Infrastructure.Persistence;

/// <summary>All six endpoint families; order defines onboarding creation.</summary>
public static class SyncCursorStreams
{
    public const string Issues = "issues";
    public const string Pulls = "pulls";
    public const string IssueComments = "issue_comments";
    public const string PrReviews = "pr_reviews";
    public const string PrReviewComments = "pr_review_comments";
    public const string Timeline = "timeline";
    /// <summary>Deleted-comment daily sweep run state — not an endpoint
    /// family, but per-repository run state lives in the same sync_cursor table.</summary>
    public const string DeletedCommentSweep = "deleted_comment_sweep";

    public static readonly string[] All =
        [Issues, Pulls, IssueComments, PrReviews, PrReviewComments, Timeline, DeletedCommentSweep];
}

/// <summary>Per-repository/per-stream sync cursor; stream values from the endpoint families.</summary>
public class SyncCursor
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public string Stream { get; set; } = null!;
    /// <summary>Opaque cursor; NULL until the Poller bootstraps.</summary>
    public string? CursorValue { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedAt { get; set; }
}
