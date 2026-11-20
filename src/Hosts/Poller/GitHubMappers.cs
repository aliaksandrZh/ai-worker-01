using System.Text.Json;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Poller;

/// <summary>GitHub REST JSON → snapshot mapping for the detect/sync pipeline. Field names follow
/// GitHub's REST responses; optional fields read defensively.</summary>
public static class GitHubMappers
{
    /// <summary>`/issues` listings carry PRs too — they carry a `pull_request` marker object.</summary>
    public static bool IsPullRequestPayload(JsonElement issue) => issue.TryGetProperty("pull_request", out _);

    public static WorkItemSnapshot MapIssue(JsonElement issue) => MapWorkItem(issue, WorkItemTypes.Issue);

    public static WorkItemSnapshot MapPullRequest(JsonElement pull)
    {
        var snapshot = MapWorkItem(pull, WorkItemTypes.PullRequest);
        return snapshot with
        {
            HeadSha = String(pull, "head", "sha"),
            BaseSha = String(pull, "base", "sha"),
            Draft = pull.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True,
            MergedAt = Timestamp(pull, "merged_at"),
            RequestedReviewers = Logins(pull, "requested_reviewers"),
        };
    }

    private static WorkItemSnapshot MapWorkItem(JsonElement element, string type) => new(
        NodeId: String(element, "node_id"),
        Number: element.GetProperty("number").GetInt64(),
        Type: type,
        Title: element.GetProperty("title").GetString() ?? "",
        Body: element.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null,
        State: element.GetProperty("state").GetString() ?? "unknown",
        AuthorLogin: String(element, "user", "login"),
        Assignees: Logins(element, "assignees"),
        Labels: element.TryGetProperty("labels", out var labels)
            ? labels.EnumerateArray().Select(l => l.ValueKind == JsonValueKind.String
                    ? l.GetString() ?? ""
                    : String(l, "name") ?? "")
                .ToList()
            : [],
        RequestedReviewers: [],
        HeadSha: null, BaseSha: null, Draft: null, MergedAt: null,
        ClosedAt: Timestamp(element, "closed_at"),
        UpdatedAt: Timestamp(element, "updated_at")!.Value,
        CreatedAtGithub: Timestamp(element, "created_at")!.Value);

    public static CommentSnapshot MapIssueComment(JsonElement comment) => MapComment(comment, GitHubCommentKinds.IssueComment);

    public static CommentSnapshot MapReviewComment(JsonElement comment) =>
        MapComment(comment, GitHubCommentKinds.ReviewComment) with
        {
            Path = String(comment, "path"),
            CommitSha = String(comment, "commit_id"),
            DiffPosition = Int32(comment, "position"),
            GithubReviewId = Int64(comment, "pull_request_review_id"),
        };

    private static CommentSnapshot MapComment(JsonElement comment, string kind) => new(
        CommentId: comment.GetProperty("id").GetInt64(),
        IssueNumber: NumberFromApiUrl(String(comment, "issue_url")) ?? NumberFromApiUrl(String(comment, "pull_request_url")) ?? 0,
        Kind: kind,
        AuthorLogin: String(comment, "user", "login"),
        Body: comment.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "",
        CreatedAt: Timestamp(comment, "created_at")!.Value,
        UpdatedAt: Timestamp(comment, "updated_at")!.Value,
        InReplyToId: Int64(comment, "in_reply_to_id") ?? Int64(comment, "in_reply_to_comment_id"),
        GithubReviewId: null, Path: null, CommitSha: null, DiffPosition: null);

    public static ReviewSnapshot MapReview(JsonElement review) => new(
        ReviewId: review.GetProperty("id").GetInt64(),
        PrNumber: NumberFromApiUrl(String(review, "pull_request_url")) ?? 0,
        State: review.GetProperty("state").GetString() ?? "unknown",
        AuthorLogin: String(review, "user", "login"),
        Body: review.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() : null,
        CommitSha: String(review, "commit_id"),
        SubmittedAt: Timestamp(review, "submitted_at"));

    /// <summary>One raw issue-timeline event (task 05): event name, stable id (null → fingerprint key
    /// downstream), commit fields for `closed`, the referencing item for `cross-referenced` (with the
    /// source issue's repository_url for the same-repository guard), and the reviewer login for
    /// `review_requested`/`review_request_removed` (team requests have no login and are skipped upstream).</summary>
    public static TimelineEventSnapshot MapTimelineEvent(JsonElement timelineEvent) => new(
        Id: Int64(timelineEvent, "id"),
        EventType: String(timelineEvent, "event"),
        CreatedAt: Timestamp(timelineEvent, "created_at"),
        CommitId: String(timelineEvent, "commit_id"),
        CommitMessage: String(timelineEvent, "commit_message"),
        SourceIssueNumber: Int64(timelineEvent, "source", "issue", "number"),
        SourceIsPullRequest: timelineEvent.TryGetProperty("source", out var source)
            && source.TryGetProperty("issue", out var sourceIssue)
            && sourceIssue.TryGetProperty("pull_request", out _),
        SourceIssueRepositoryUrl: String(timelineEvent, "source", "issue", "repository_url"),
        RequestedReviewerLogin: String(timelineEvent, "requested_reviewer", "login"),
        RequestedTeamName: String(timelineEvent, "requested_team", "name"),
        ActorLogin: String(timelineEvent, "actor", "login"));

    /// <summary>Pull request summary from the ad-hoc `commits/{sha}/pulls` read (task 05 D2) — the
    /// merged PR that introduced a closing commit; merged_at null means referencing/unmerged, skipped.</summary>
    public static IntroducerPullRequestSnapshot MapIntroducerPullRequest(JsonElement pull) => new(
        Number: pull.GetProperty("number").GetInt64(),
        MergedAt: Timestamp(pull, "merged_at"));

    /// <summary>Last '/'-segment of a GitHub API URL when numeric (`.../issues/12` → 12).</summary>
    public static long? NumberFromApiUrl(string? apiUrl)
    {
        if (string.IsNullOrEmpty(apiUrl))
            return null;
        var last = apiUrl[(apiUrl.LastIndexOf('/') + 1)..];
        return long.TryParse(last, out var number) ? number : null;
    }

    /// <summary>`updated_at` of a raw listing item — position bookkeeping must include items a stream skips.</summary>
    public static DateTime? UpdatedAtOf(JsonElement element)
    {
        var raw = String(element, "updated_at");
        return DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    // ---- defensive JSON reads ----

    private static string? String(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static int? Int32(JsonElement element, params string[] path)
    {
        var current = Navigate(element, path);
        return current.HasValue && current.Value.ValueKind == JsonValueKind.Number ? current.Value.GetInt32() : null;
    }

    private static long? Int64(JsonElement element, params string[] path)
    {
        var current = Navigate(element, path);
        return current.HasValue && current.Value.ValueKind == JsonValueKind.Number ? current.Value.GetInt64() : null;
    }

    private static DateTime? Timestamp(JsonElement element, params string[] path)
    {
        var current = Navigate(element, path);
        return current.HasValue && current.Value.ValueKind == JsonValueKind.String
            && DateTime.TryParse(current.Value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
    }

    private static JsonElement? Navigate(JsonElement element, string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }
        return current.ValueKind == JsonValueKind.Object || current.ValueKind == JsonValueKind.String
            || current.ValueKind == JsonValueKind.Number
            ? current
            : null;
    }

    private static IReadOnlyList<string> Logins(JsonElement element, string name) =>
        element.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(l => String(l, "login")).Where(l => l is not null).Cast<string>().ToList()
            : [];
}
