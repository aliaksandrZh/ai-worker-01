using PiAgent.Poller.Streams;
using PiAgent.Poller.GitHub;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Poller;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// Snapshot diff → decision-78 normalized detections (02 §7): created/closed/updated with change map,
/// pull_request_synchronized on head drift, review request set-diff per login, deterministic dedupe
/// fingerprints, and the re-detection collision pin (identical before/after → identical dedupe_key).
/// </summary>
public class SnapshotDiffTests
{
    private static WorkItemSnapshot Issue(string title = "t", string state = "open", string? body = "b",
        string[]? labels = null, string[]? assignees = null, string updated = "2026-11-20T10:00:00Z") =>
        new("ISSUE_1", 1, WorkItemTypes.Issue, title, body, state, "alice",
            assignees ?? [], labels ?? [], [], null, null, null, null, null, ParseTime(updated), ParseTime("2026-11-01T08:00:00Z"));

    private static WorkItemSnapshot Pr(string head = "head-sha-1", string[]? reviewers = null, string state = "open",
        bool draft = false, string? mergedAt = null, string title = "pr", string? body = "b",
        string updated = "2026-11-20T10:00:00Z", string[]? labels = null, string[]? assignees = null,
        string? closedAt = null) =>
        new("PR_1", 7, WorkItemTypes.PullRequest, title, body, state, "alice",
            assignees ?? [], labels ?? [], reviewers ?? [], head, "base-sha-1", draft,
            closedAt is null ? null : ParseTime(closedAt),
            mergedAt is null ? null : ParseTime(mergedAt), ParseTime(updated), ParseTime("2026-11-02T08:00:00Z"));

    private static DateTime ParseTime(string value) =>
        DateTime.Parse(value, styles: System.Globalization.DateTimeStyles.AdjustToUniversal);

    [Fact]
    public void FreshIssue_ProducesCreatedOnly()
    {
        var detections = SnapshotDiff.Detect(null, Issue());
        var detection = Assert.Single(detections);
        Assert.Equal(GitHubEventTypes.WorkItemCreated, detection.EventType);
        Assert.Equal(GitHubEventSources.RestResourceSync, detection.Source);
        Assert.Equal("wi:ISSUE_1:created", detection.DedupeKey);
    }

    [Fact]
    public void UnchangedState_ProducesNothing()
    {
        Assert.Empty(SnapshotDiff.Detect(Issue(), Issue()));
    }

    [Fact]
    public void TitleLabelAssigneeChange_SingleUpdatedEvent_WithChangeMap()
    {
        var before = Issue(labels: ["bug"], assignees: ["bob"]);
        var after = Issue(title: "t2", labels: ["bug", "urgent"], assignees: ["carol"]);

        var detection = Assert.Single(SnapshotDiff.Detect(before, after));
        Assert.Equal(GitHubEventTypes.WorkItemUpdated, detection.EventType);
        Assert.StartsWith("wi:ISSUE_1:upd:", detection.DedupeKey);
        var payload = Assert.IsType<WorkItemUpdatedEventPayload>(detection.Payload);
        Assert.Equal(1, payload.Number);
        Assert.False(payload.BodyChanged);
        Assert.Equal("t -> t2", payload.Changed["title"]);
        Assert.Equal("bob -> carol", payload.Changed["assignees"]);
    }

    [Fact]
    public void BodyOnlyChange_FlagButNoBodyCopy()
    {
        var one = SnapshotDiff.Detect(Issue(body: "old"), Issue(body: "b1")).Single();
        var two = SnapshotDiff.Detect(Issue(body: "old"), Issue(body: "b2")).Single();
        Assert.Equal(GitHubEventTypes.WorkItemUpdated, one.EventType);
        var payload = Assert.IsType<WorkItemUpdatedEventPayload>(one.Payload);
        Assert.True(payload.BodyChanged);
        Assert.DoesNotContain("body", payload.Changed.Keys); // never the content; flag only
        Assert.NotEqual(one.DedupeKey, two.DedupeKey);        // body hash is part of the fingerprint
    }

    [Fact]
    public void OpenToClosed_ClosedEvent_WithReopenAsUpdated()
    {
        var after = Issue(state: "closed");
        var detection = Assert.Single(SnapshotDiff.Detect(Issue(), after));
        Assert.Equal(GitHubEventTypes.WorkItemClosed, detection.EventType);
        var payload = Assert.IsType<WorkItemClosedEventPayload>(detection.Payload);
        Assert.Null(payload.CloseReason);
        // closed → open (restart-safe re-observation) is an updated event, never a second close
        var reopen = Assert.Single(SnapshotDiff.Detect(after, Issue()));
        Assert.Equal(GitHubEventTypes.WorkItemUpdated, reopen.EventType);
    }

    [Fact]
    public void MergedPr_ClosedEvent_WithCloseReasonMerged()
    {
        var after = Pr(state: "closed", mergedAt: "2026-11-20T09:00:00Z", closedAt: "2026-11-20T09:00:00Z");
        var detections = SnapshotDiff.Detect(Pr(), after);
        var closed = detections.Single(d => d.EventType == GitHubEventTypes.WorkItemClosed);
        Assert.Equal("merged", Assert.IsType<WorkItemClosedEventPayload>(closed.Payload).CloseReason);
        Assert.Equal($"wi:PR_1:closed:{new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()}",
            closed.DedupeKey);
    }

    [Fact]
    public void HeadShaChange_EmitsSynchronized()
    {
        var detection = Assert.Single(
            SnapshotDiff.Detect(Pr(), Pr(head: "head-sha-2", updated: "2026-11-20T11:00:00Z")),
            d => d.EventType == GitHubEventTypes.PullRequestSynchronized);
        var payload = Assert.IsType<PullRequestSynchronizedEventPayload>(detection.Payload);
        Assert.Equal(7, payload.Number);
        Assert.Equal("head-sha-1", payload.FromSha);
        Assert.Equal("head-sha-2", payload.ToSha);
        Assert.Equal(GitHubEventSources.RestResourceSync, detection.Source);
        Assert.Equal("prs:PR_1:head-sha-2", detection.DedupeKey);
    }

    [Fact]
    public void ReviewerSetDiff_EmitsRequestedPerLogin_FromSnapshotDiffSource()
    {
        var detections = SnapshotDiff.Detect(Pr(reviewers: ["pi-bot"]), Pr(reviewers: ["pi-bot", "carol"]));
        var requested = detections.Single(d => d.EventType == GitHubEventTypes.ReviewRequested);
        Assert.Equal(GitHubEventSources.SnapshotDiff, requested.Source);
        Assert.Equal("rr:PR_1:carol:req", requested.DedupeKey);
        Assert.Equal("carol", Assert.IsType<ReviewRequestEventPayload>(requested.Payload).Reviewer);

        var removed = SnapshotDiff.Detect(Pr(reviewers: ["pi-bot", "carol"]), Pr(reviewers: ["pi-bot"]))
            .Single(d => d.EventType == GitHubEventTypes.ReviewRequestRemoved);
        Assert.Equal("rr:PR_1:carol:rm", removed.DedupeKey);
        Assert.Equal("carol", Assert.IsType<ReviewRequestEventPayload>(removed.Payload).Reviewer);
    }

    [Fact]
    public void RedetectionOfIdenticalDiff_ReusesSameDedupeKey() // Review Focus 6 pin
    {
        var before = Pr(reviewers: ["pi-bot", "carol"]);
        var after = Pr(reviewers: ["pi-bot"]);
        var first = SnapshotDiff.Detect(before, after).Single(d => d.EventType == GitHubEventTypes.ReviewRequestRemoved);
        var second = SnapshotDiff.Detect(before, after).Single(d => d.EventType == GitHubEventTypes.ReviewRequestRemoved);
        Assert.Equal(first.DedupeKey, second.DedupeKey);
    }

    [Fact]
    public void SameFieldEditedWithinSecond_ProducesDistinctFingerprints()
    {
        var before = Issue(title: "a");
        var one = SnapshotDiff.Detect(before, Issue(title: "b")).Single();
        var two = SnapshotDiff.Detect(before, Issue(title: "c")).Single();
        Assert.NotEqual(one.DedupeKey, two.DedupeKey);
    }
}
