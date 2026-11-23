using System.Text.Json.Serialization;

namespace PiAgent.Poller.GitHub;

/// <summary>Mapped GitHub REST resources feeding the detection engine (the Poller streams).
/// `AuthorLogin`/`CreatedAtGithub` extend the snapshot sketch: the entities store them
/// and the stream upserts need them.</summary>
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

// ---- JSONB event payloads ("describing the change"). snake_case property
// names match the repository configuration DTO convention.

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

// ---- timeline + deleted-comment sweep payloads ----

/// <summary>`cross-referenced` timeline event (raw record; feeds `related_to` links). `source_type` is
/// the referencing item's type — links form only from Issue↔PR pairs (typed columns).</summary>
public sealed record TimelineCrossReferencePayload(
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("source_number")] long SourceNumber,
    [property: JsonPropertyName("source_type")] string SourceType);

/// <summary>`closed` timeline event (raw record; feeds `closes` links via commit closing keywords).
/// Null commit fields when the issue was closed manually.</summary>
public sealed record TimelineClosedPayload(
    [property: JsonPropertyName("number")] long Number,
    [property: JsonPropertyName("commit_id")] string? CommitId,
    [property: JsonPropertyName("commit_message")] string? CommitMessage);

/// <summary>Raw timeline record for `connected`/`disconnected` (synced without link
/// semantics — the type lives in the event_type column, the payload names the owner only).</summary>
public sealed record TimelineRecordPayload(
    [property: JsonPropertyName("number")] long Number);

/// <summary>`issue_comment_deleted` / `review_comment_deleted` from the daily sweep;
/// `author` is the stored author of the tombstoned comment.</summary>
public sealed record CommentDeletedEventPayload(
    [property: JsonPropertyName("comment_id")] long CommentId,
    [property: JsonPropertyName("issue_number")] long IssueNumber,
    [property: JsonPropertyName("author")] string? Author);

/// <summary>One pull request summary from the ad-hoc `commits/{sha}/pulls` read:
/// the merged PR that introduced a closing commit.</summary>
public sealed record IntroducerPullRequestSnapshot(long Number, DateTime? MergedAt);

/// <summary>One raw issue-timeline event mapped from the REST payload (task 05). `Id` is the stable
/// external event id when GitHub reports one (null → deterministic fingerprint key downstream);
/// `SourceIssueX` describe the referencing item of a `cross-referenced` event; `RequestedX` the
/// reviewer of `review_requested`/`review_request_removed` events.</summary>
public sealed record TimelineEventSnapshot(
    long? Id,
    string? EventType,
    DateTime? CreatedAt,
    string? CommitId,
    string? CommitMessage,
    long? SourceIssueNumber,
    bool? SourceIsPullRequest,
    string? SourceIssueRepositoryUrl,
    string? RequestedReviewerLogin,
    string? RequestedTeamName,
    string? ActorLogin);
