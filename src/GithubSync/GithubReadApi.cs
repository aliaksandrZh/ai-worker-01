namespace PiAgent.GithubSync;

/// <summary>
/// The GitHub read seam of the sync adapter (docs/github-sync.md): every call is a read;
/// the shape knowledge (paging, sort orders) stays inside the implementation.
/// </summary>
public interface IGithubReadApi
{
    /// <summary>Issues/PRs, newest update first (state, title, body, labels, isPr) — page 1 only:
    /// the open set is the working set; the just-closed item lingers near the top and witnesses
    /// its own terminal beat. No `since`, no cursor — detection is stateless.</summary>
    Task<IReadOnlyList<GithubItem>> ListItemsAsync(string owner, string repo, CancellationToken ct);

    /// <summary>Comments on one item (issues and PRs — a PR's conversation comments are issue
    /// comments), newest update first.</summary>
    Task<IReadOnlyList<GithubComment>> ListCommentsAsync(
        string owner, string repo, long itemNumber, CancellationToken ct);

    /// <summary>Head sha of a pull request.</summary>
    Task<string?> GetPullHeadShaAsync(string owner, string repo, long prNumber, CancellationToken ct);

    /// <summary>Reviews on a PR, newest last.</summary>
    Task<IReadOnlyList<GithubReview>> ListReviewsAsync(
        string owner, string repo, long prNumber, CancellationToken ct);

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

public sealed record GithubComment(long Id, string Author, string Body, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record GithubReview(long Id, long PrNumber, string Author, string Verdict, string Body, DateTime CreatedAtUtc);
