namespace PiAgent.GithubSync;

/// <summary>
/// The GitHub read seam of the sync adapter (docs/github-sync.md): every call is a read;
/// the shape knowledge (paging, since-params) stays inside the implementation.
/// </summary>
public interface IGithubReadApi
{
    /// <summary>Issues/PRs updated since the timestamp (state, title, body, labels, isPr).</summary>
    Task<IReadOnlyList<GithubItem>> ListItemsUpdatedSinceAsync(
        string owner, string repo, DateTime sinceUtc, CancellationToken ct);

    /// <summary>Comments created since the timestamp, oldest first.</summary>
    Task<IReadOnlyList<GithubComment>> ListCommentsSinceAsync(
        string owner, string repo, DateTime sinceUtc, CancellationToken ct);

    /// <summary>Head sha of a pull request.</summary>
    Task<string?> GetPullHeadShaAsync(string owner, string repo, long prNumber, CancellationToken ct);

    /// <summary>Reviews on a PR created since the timestamp.</summary>
    Task<IReadOnlyList<GithubReview>> ListReviewsSinceAsync(
        string owner, string repo, long prNumber, DateTime sinceUtc, CancellationToken ct);

    /// <summary>The author's collaborator permission ("read"/"write"/"admin", …).</summary>
    Task<string?> GetPermissionAsync(string owner, string repo, string login, CancellationToken ct);
}

public sealed record GithubItem(
    long Number,
    bool IsPullRequest,
    string Title,
    string Body,
    string State,
    IReadOnlyList<string> Labels,
    DateTime UpdatedAtUtc);

public sealed record GithubComment(long Id, long ItemNumber, string Author, string Body, DateTime CreatedAtUtc);

public sealed record GithubReview(long Id, long PrNumber, string Author, string Verdict, string Body, DateTime CreatedAtUtc);
