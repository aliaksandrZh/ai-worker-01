namespace PiAgent.GithubSync;

/// <summary>
/// The GitHub read seam of the sync adapter (docs/github-sync.md): every call is a read;
/// the shape knowledge (paging, since-params) stays inside the implementation.
/// </summary>
public interface IGithubReadApi
{
    /// <summary>Issues/PRs, newest update first (state, title, body, labels, isPr) — the worker
    /// walks down to its own overlap floor; no `since` (inclusive + second-precision: a watermark
    /// pairing refetches forever).</summary>
    Task<IReadOnlyList<GithubItem>> ListItemsAsync(string owner, string repo, CancellationToken ct);

    /// <summary>Repo-wide comments, newest update first (issues and PRs).</summary>
    Task<IReadOnlyList<GithubComment>> ListCommentsAsync(string owner, string repo, CancellationToken ct);

    /// <summary>Head sha of a pull request.</summary>
    Task<string?> GetPullHeadShaAsync(string owner, string repo, long prNumber, CancellationToken ct);

    /// <summary>Reviews on a PR, chronological (the worker applies its own floor).</summary>
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

public sealed record GithubComment(long Id, long ItemNumber, string Author, string Body, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record GithubReview(long Id, long PrNumber, string Author, string Verdict, string Body, DateTime CreatedAtUtc);
