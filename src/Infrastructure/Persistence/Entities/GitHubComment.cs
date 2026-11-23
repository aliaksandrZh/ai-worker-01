namespace PiAgent.Infrastructure.Persistence;

/// <summary>GitHubComment discriminator: conversation comment (Issue or PR) vs inline PR review comment.</summary>
public static class GitHubCommentKinds
{
    public const string IssueComment = "issue_comment";
    public const string ReviewComment = "review_comment";
}

/// <summary>
/// A GitHub comment: `issue_comment` for Issue/PR conversation comments, `review_comment` for
/// inline PR review comments. `body` is always the latest synced version; deleted comments are
/// tombstoned (`is_deleted`/`deleted_at`), never removed (the deleted-comment sweep tombstones only).
/// `github_review_id` is an external ID only — no DB FK, the review row may not be known yet.
/// </summary>
public class GitHubComment
{
    public Guid Id { get; set; }
    public Guid WorkItemId { get; set; }
    public long GithubCommentId { get; set; }
    public string Kind { get; set; } = null!;
    public string? AuthorLogin { get; set; }
    public string Body { get; set; } = null!;
    public DateTime CreatedAtGithub { get; set; }
    public DateTime UpdatedAtGithub { get; set; }
    /// <summary>Local reference when the API provides one.</summary>
    public long? InReplyToCommentId { get; set; }
    /// <summary>External review ID for inline comments; no DB FK.</summary>
    public long? GithubReviewId { get; set; }
    public string? Path { get; set; }
    public string? CommitSha { get; set; }
    public int? DiffPosition { get; set; }
    public DateTime LastSyncedAt { get; set; }
    /// <summary>Tombstone flag; set by the deleted-comment sweep; record stays.</summary>
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }
}
