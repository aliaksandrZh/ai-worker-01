using System.Text.Json.Serialization;

namespace PiAgent.Poller;

/// <summary>Mapped GitHub REST resources feeding the detection engine (task 04 streams).
/// `AuthorLogin`/`CreatedAtGithub` extend the plan's snapshot sketch: the entities store them (02 §3)
/// and the stream upserts need them — ledger ruling at Task 4.</summary>
public sealed record WorkItemSnapshot(
    string? NodeId, long Number, string Type, string Title, string? Body, string State,
    string? AuthorLogin,
    IReadOnlyList<string> Assignees, IReadOnlyList<string> Labels, IReadOnlyList<string> RequestedReviewers,
    string? HeadSha, string? BaseSha, bool? Draft, DateTime? ClosedAt, DateTime? MergedAt, DateTime UpdatedAt,
    DateTime CreatedAtGithub);

/// <summary>A parsed comment; `Kind` set by the mapping call (issue_comment vs review_comment);
/// `IssueNumber` is the owning issue/PR number parsed from the API URL.</summary>
public sealed record CommentSnapshot(long CommentId, long IssueNumber, string Kind, string? AuthorLogin,
    string Body, DateTime CreatedAt, DateTime UpdatedAt, long? InReplyToId, long? GithubReviewId,
    string? Path, string? CommitSha, int? DiffPosition);

/// <summary>A parsed submitted PR review; the Poller looks PRs up by number.</summary>
public sealed record ReviewSnapshot(long ReviewId, long PrNumber, string State, string? AuthorLogin,
    string? Body, string? CommitSha, DateTime? SubmittedAt);

// ---- JSONB event payloads (02 §7: "describing the change"; decision 78 names). snake_case property
// names match the repository configuration DTO convention (close_reason per decision 78).

public sealed record WorkItemCreatedEventPayload(
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("author")] string? Author);

public sealed record WorkItemUpdatedEventPayload(
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("changed")] IReadOnlyDictionary<string, string> Changed,
    [property: JsonPropertyName("body_changed")] bool BodyChanged);

public sealed record WorkItemClosedEventPayload(
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("close_reason")] string? CloseReason);

public sealed record PullRequestSynchronizedEventPayload(
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("from_sha")] string FromSha,
    [property: JsonPropertyName("to_sha")] string ToSha);

public sealed record ReviewRequestEventPayload(
    [property: JsonPropertyName("pr_number")] long PrNumber,
    [property: JsonPropertyName("reviewer")] string Reviewer);

public sealed record CommentCreatedEventPayload(
    [property: JsonPropertyName("comment_id")] long CommentId,
    [property: JsonPropertyName("issue_number")] long IssueNumber,
    [property: JsonPropertyName("author")] string? Author);

public sealed record CommentUpdatedEventPayload(
    [property: JsonPropertyName("comment_id")] long CommentId,
    [property: JsonPropertyName("issue_number")] long IssueNumber,
    [property: JsonPropertyName("author")] string? Author);

public sealed record ReviewSubmittedEventPayload(
    [property: JsonPropertyName("review_id")] long ReviewId,
    [property: JsonPropertyName("pr_number")] long PrNumber,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("author")] string? Author);

public sealed record ReviewDismissedEventPayload(
    [property: JsonPropertyName("review_id")] long ReviewId,
    [property: JsonPropertyName("pr_number")] long PrNumber,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("author")] string? Author);
