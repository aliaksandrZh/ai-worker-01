namespace PiAgent.Infrastructure.Persistence;

/// <summary>Submitted PR review states as the REST API reports them: pending reviews are
/// visible only to their author; `dismissed` arrives on dismissal of a submitted review.</summary>
public static class GitHubReviewStates
{
    public const string Pending = "pending";
    public const string Commented = "commented";
    public const string Approved = "approved";
    public const string ChangesRequested = "changes_requested";
    public const string Dismissed = "dismissed";
}

/// <summary>A submitted PR review, distinct from inline review comments — those are
/// GitHubComment rows with `kind = review_comment` linked by `github_review_id`.</summary>
public class GitHubReview
{
    public Guid Id { get; set; }
    public Guid WorkItemId { get; set; }
    public long GithubReviewId { get; set; }
    public string AuthorLogin { get; set; } = null!;
    public string State { get; set; } = null!;
    public string Body { get; set; } = null!;
    public string? CommitSha { get; set; }
    public DateTime? SubmittedAtGithub { get; set; }
    public DateTime LastSyncedAt { get; set; }
}
