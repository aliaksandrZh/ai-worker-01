using PiAgent.Poller.GitHub;
using System.Security.Cryptography;
using System.Text;
using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Poller.Streams;

/// <summary>One detected change: a normalized event with its JSONB payload and the
/// deterministic dedupe fingerprint. Source ∈ rest_resource_sync | snapshot_diff.</summary>
public sealed record Detection(string EventType, string Source, string DedupeKey, object Payload, DateTime OccurredAt);

/// <summary>
/// Pure detection engine over WorkItem snapshots (the detection table). Emits:
/// created / updated (change map, body as a flag only) / closed (`close_reason = merged` for merged PRs) /
/// pull_request_synchronized on head drift / review_requested &amp; review_request_removed
/// as requested_reviewer set diffs (source snapshot_diff). Fingerprint scheme:
/// `wi:{node}:upd:{unix}:{sha8(pairs)}` — identical re-detection yields the identical key (overlap and
/// restart dedupe), an edit within the same second yields a distinct one. First observation pins the
/// head (drift alone emits synchronization; pinning happens on Task creation downstream).
/// </summary>
public static class SnapshotDiff
{
    public static IReadOnlyList<Detection> Detect(WorkItemSnapshot? before, WorkItemSnapshot after)
    {
        var node = after.NodeId ?? $"num:{after.Number}";
        var detections = new List<Detection>();

        if (before is null)
        {
            detections.Add(new Detection(GitHubEventTypes.WorkItemCreated, GitHubEventSources.RestResourceSync,
                $"wi:{node}:created",
                new WorkItemCreatedEventPayload(after.Number, after.Type, after.Title, after.AuthorLogin),
                after.UpdatedAt));
            if (after.State == "closed")
                detections.Add(Closed(after, node));
            HeadDrift(null, after, node, detections);
            Reviewers(null, after, node, detections);
            return detections;
        }

        var (changed, bodyChanged) = FieldChanges(before, after);

        if (after.State == "closed" && before.State != "closed")
        {
            detections.Add(Closed(after, node));
            // the closed event owns closure artifacts; the update keeps other fields
            changed.Remove("closed_at");
            changed.Remove("merged_at");
            changed.Remove("state");
        }

        if (changed.Count > 0 || bodyChanged)
        {
            var pairs = new List<string>(changed.Select(kv => $"{kv.Key}={kv.Value}"));
            if (bodyChanged)
                pairs.Add($"body={Sha8(after.Body ?? "")}");
            detections.Add(new Detection(GitHubEventTypes.WorkItemUpdated, GitHubEventSources.RestResourceSync,
                $"wi:{node}:upd:{Unix(after.UpdatedAt)}:{Sha8(string.Join(";", pairs))}",
                new WorkItemUpdatedEventPayload(after.Number, changed, bodyChanged),
                after.UpdatedAt));
        }

        HeadDrift(before.HeadSha, after, node, detections);
        Reviewers(before.RequestedReviewers, after, node, detections);
        return detections;
    }

    /// <summary>First observation (before==null) pins the head without a synchronized event; only drift emits.</summary>
    private static void HeadDrift(string? beforeHead, WorkItemSnapshot after, string node, List<Detection> detections)
    {
        if (beforeHead is null
            || after.Type != WorkItemTypes.PullRequest
            || after.HeadSha is null
            || string.Equals(beforeHead, after.HeadSha, StringComparison.Ordinal))
            return;
        detections.Add(new Detection(GitHubEventTypes.PullRequestSynchronized, GitHubEventSources.RestResourceSync,
            $"prs:{node}:{after.HeadSha}",
            new PullRequestSynchronizedEventPayload(after.Number, beforeHead, after.HeadSha),
            after.UpdatedAt));
    }

    private static void Reviewers(IReadOnlyList<string>? beforeRequested, WorkItemSnapshot after, string node,
        List<Detection> detections)
    {
        var previously = beforeRequested ?? [];
        foreach (var reviewer in after.RequestedReviewers.Except(previously))
            detections.Add(new Detection(GitHubEventTypes.ReviewRequested, GitHubEventSources.SnapshotDiff,
                $"rr:{node}:{reviewer}:req",
                new ReviewRequestEventPayload(after.Number, reviewer), after.UpdatedAt));
        foreach (var reviewer in previously.Except(after.RequestedReviewers))
            detections.Add(new Detection(GitHubEventTypes.ReviewRequestRemoved, GitHubEventSources.SnapshotDiff,
                $"rr:{node}:{reviewer}:rm",
                new ReviewRequestEventPayload(after.Number, reviewer), after.UpdatedAt));
    }

    /// <summary>Synced-field change map; `body` becomes the `body_changed` flag only (never content copied).
    /// `state` appears only for reopen (open→closed keeps its dedicated closed event).</summary>
    private static (Dictionary<string, string> Changed, bool BodyChanged) FieldChanges(
        WorkItemSnapshot before, WorkItemSnapshot after)
    {
        var changed = new Dictionary<string, string>();
        if (!string.Equals(before.Title, after.Title, StringComparison.Ordinal))
            changed["title"] = $"{before.Title} -> {after.Title}";
        if (!string.Equals(before.Body, after.Body, StringComparison.Ordinal))
            changed["body_changed"] = "";
        if (!ListEqual(before.Labels, after.Labels))
            changed["labels"] = $"{Join(before.Labels)} -> {Join(after.Labels)}";
        if (!ListEqual(before.Assignees, after.Assignees))
            changed["assignees"] = $"{Join(before.Assignees)} -> {Join(after.Assignees)}";
        if (before.Draft != after.Draft)
            changed["draft"] = $"{before.Draft} -> {after.Draft}";
        if (before.MergedAt != after.MergedAt && after.MergedAt is not null)
            changed["merged_at"] = $"{before.MergedAt:O} -> {after.MergedAt:O}";
        if (after.State == "open" && before.State != "open")
            changed["state"] = $"{before.State} -> {after.State}";
        if (after.State == "closed" && before.State == "closed" && before.ClosedAt != after.ClosedAt)
            changed["closed_at"] = $"{before.ClosedAt:O} -> {after.ClosedAt:O}";
        var bodyChanged = changed.Remove("body_changed");
        return (changed, bodyChanged);
    }

    private static Detection Closed(WorkItemSnapshot after, string node)
    {
        var closeAt = after.ClosedAt ?? after.UpdatedAt;
        return new Detection(GitHubEventTypes.WorkItemClosed, GitHubEventSources.RestResourceSync,
            $"wi:{node}:closed:{Unix(closeAt)}",
            new WorkItemClosedEventPayload(after.Number, CloseReason(after)),
            closeAt);
    }

    /// <summary>A merged PR's close carries `close_reason = merged` via merged_at.</summary>
    private static string? CloseReason(WorkItemSnapshot after) => after.MergedAt is null ? null : "merged";

    private static bool ListEqual(IReadOnlyList<string>? left, IReadOnlyList<string>? right) =>
        (left ?? []).OrderBy(x => x, StringComparer.Ordinal)
            .SequenceEqual((right ?? []).OrderBy(x => x, StringComparer.Ordinal), StringComparer.Ordinal);

    private static string Join(IReadOnlyList<string>? values) =>
        string.Join(",", (values ?? []).OrderBy(x => x, StringComparer.Ordinal));

    private static long Unix(DateTime value) => new DateTimeOffset(value, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>First 8 hex chars of the SHA-256 — the deterministic fingerprint suffix.</summary>
    private static string Sha8(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }
}
