using System.Net;

namespace PiAgent.Platform.Http;

/// <summary>A rate-limited GitHub response: 403 with `X-RateLimit-Remaining: 0` (primary) or 429
/// (secondary). Carries `Retry-After` and `X-RateLimit-Reset` for pause scheduling.</summary>
public sealed class GitHubRateLimitedException(
    HttpStatusCode status,
    int? remaining,
    int? retryAfterSeconds,
    DateTimeOffset? resetAt,
    string detail)
    : Exception(detail)
{
    public HttpStatusCode Status { get; } = status;

    public int? Remaining { get; } = remaining;

    public int? RetryAfterSeconds { get; } = retryAfterSeconds;

    public DateTimeOffset? ResetAt { get; } = resetAt;
}

/// <summary>Any other failed GitHub read: non-rate-limit HTTP status (or null status for network/timeout).
/// Messages name the path and status — never the PAT.</summary>
public sealed class GitHubReadException(HttpStatusCode? status, string detail) : Exception(detail)
{
    public HttpStatusCode? Status { get; } = status;
}
