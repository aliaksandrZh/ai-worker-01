using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Poller;

/// <summary>Everything one stream pass needs for one repository:</summary>
public sealed record StreamRun(AppDbContext Db, Repository Repository, GitHubReadClient Client, PollerOptions Options);

/// <summary>Stream outcome. Failed = persisted nothing because of a recoverable error (cursor LastError set);
/// NotModified = 304 (nothing changed; cursor unchanged except success timestamps); Completed = normal pass.
/// GitHubRateLimitedException is never converted — it propagates so PollerCycle can pause the cycle (decision 53).</summary>
public enum StreamResultKind
{
    Completed,
    NotModified,
    Failed,
}

public sealed record StreamResult(StreamResultKind Kind, string? Error = null);

/// <summary>One decision-53 endpoint family (issues, pulls, issue_comments, pr_reviews, pr_review_comments),
/// each owning its per-repository sync cursor.</summary>
public interface IRepositoryStream
{
    string StreamName { get; }

    Task<StreamResult> SyncAsync(StreamRun run, CancellationToken cancellationToken);
}
