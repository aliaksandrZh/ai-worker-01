using PiAgent.Events;

namespace PiAgent.Publisher;

/// <summary>
/// The GitHub write adapter seam (docs/publisher.md): one implementation in production
/// (REST/GraphQL), a fake in tests — two adapters justify the seam. Every method is one
/// atomic mutation; multi-step flows (option-id resolution) stay inside the adapter.
/// </summary>
public interface IGithubWriteApi
{
    /// <summary>Posts a comment on an issue or PR; returns the comment id.</summary>
    Task<long> CreateCommentAsync(PublishComment evt, CancellationToken ct);

    /// <summary>Creates (or updates, when the PR already exists) the pull request.</summary>
    Task<long> CreateOrUpdatePrAsync(PublishPr evt, CancellationToken ct);

    /// <summary>Submits the review outcome: approve / request changes (+ body).</summary>
    Task<long> SubmitReviewAsync(PublishReview evt, CancellationToken ct);

    /// <summary>Moves a Projects v2 card to the named status column.</summary>
    Task MoveBoardItemAsync(PublishBoardMove evt, CancellationToken ct);
}

/// <summary>
/// Error classification (docs/publisher.md): retryable failures throw
/// <see cref="GithubTransientException"/> (EventLog redelivers); permanent ones throw
/// <see cref="GithubPermanentException"/> (the consumer poisons straight to dead-letter).
/// </summary>
public sealed class GithubTransientException(string message, int? retryAfterSeconds = null)
    : Exception(message)
{
    public int? RetryAfterSeconds { get; } = retryAfterSeconds;
}

public sealed class GithubPermanentException(string message) : Exception(message);
