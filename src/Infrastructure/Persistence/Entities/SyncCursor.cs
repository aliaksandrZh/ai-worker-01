namespace PiAgent.Infrastructure.Persistence;

/// <summary>All six decision-53 endpoint families; order defines onboarding creation.</summary>
public static class SyncCursorStreams
{
    public const string Issues = "issues";
    public const string Pulls = "pulls";
    public const string IssueComments = "issue_comments";
    public const string PrReviews = "pr_reviews";
    public const string PrReviewComments = "pr_review_comments";
    public const string Timeline = "timeline";

    public static readonly string[] All = [Issues, Pulls, IssueComments, PrReviews, PrReviewComments, Timeline];
}

/// <summary>Per-repository/per-stream sync cursor (02 §8); stream values from decision 53 endpoint families.</summary>
public class SyncCursor
{
    public Guid Id { get; set; }
    public Guid RepositoryId { get; set; }
    public string Stream { get; set; } = null!;
    /// <summary>Opaque cursor; NULL until the Poller bootstraps (bootstrap semantics owned by ticket 04).</summary>
    public string? CursorValue { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public string? LastError { get; set; }
    public DateTime UpdatedAt { get; set; }
}
