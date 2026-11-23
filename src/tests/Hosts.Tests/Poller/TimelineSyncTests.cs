using PiAgent.Poller.Streams;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Poller;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// `timeline` stream over real postgres with a stubbed GitHub (task 05): raw timeline records, the two
/// WorkItemLink derivations (related_to / closes with STRICT closing keywords, D2 option A), the
/// branch-naming negative (checklist 2), holdback on unresolved link counterparts, position/visit-gate
/// bookkeeping, and repoll idempotence via `tl:{event_id}` dedupe keys.
/// </summary>
public class TimelineSyncTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static readonly DateTime IssueUpdatedAt = new(2026, 11, 19, 16, 0, 0, DateTimeKind.Utc);

    private async Task<(AppDbContext Db, Repository Repo)> NewDbAndRepoAsync()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var repo = await PollerTestSupport.SeedRepositoryAsync(db);
        return (db, repo);
    }

    private static WorkItem SeedIssue(AppDbContext db, Repository repo, long number, string updatedAt = "2026-11-19T16:00:00Z") =>
        PollerTestSupport.SeedWorkItem(db, repo, number, WorkItemTypes.Issue, updatedAt);

    private static SyncCursor SeedCursor(AppDbContext db, Repository repo, string stream, string? value = null)
    {
        var cursor = new SyncCursor { Id = Guid.NewGuid(), RepositoryId = repo.Id, Stream = stream, CursorValue = value, UpdatedAt = DateTime.UtcNow };
        db.SyncCursors.Add(cursor);
        db.SaveChanges();
        return cursor;
    }

    [Fact]
    public async Task CrossReference_CreatesRelatedToLink_RawRecord_AndAdvancesCursor() // checklist 1
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = SeedIssue(db, repo, 3); // number 3 < PR 7: the issue's timeline is visited first
        var pr = PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.CrossReferencedJson(9001, sourceNumber: 7)));
        var result = await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Completed, result.Kind);
        Assert.Single(handler.Requests, r => r.Contains("issues/3/timeline"));

        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        var raw = Assert.Single(events);
        Assert.Equal(GitHubTimelineEvents.CrossReferenced, raw.EventType);
        Assert.Equal(GitHubEventSources.TimelineApi, raw.Source);
        Assert.Equal("tl:9001", raw.DedupeKey); // stable GitHub event id
        Assert.True(raw.WorkItemId == issue.Id, $"raw={raw.WorkItemId} issue={issue.Id} pr={pr.Id}");
        using (var payload = JsonDocument.Parse(raw.Payload))
        {
            Assert.Equal(3, payload.RootElement.GetProperty("number").GetInt64());
            Assert.Equal(7, payload.RootElement.GetProperty("source_number").GetInt64());
            Assert.Equal("pull_request", payload.RootElement.GetProperty("source_type").GetString());
        }

        var link = Assert.Single(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        Assert.Equal(issue.Id, link.IssueWorkItemId); // typed role columns (D1): issue side / PR side
        Assert.Equal(pr.Id, link.PrWorkItemId);
        Assert.Equal(WorkItemLinkRelations.RelatedTo, link.RelationType);
        Assert.Equal(WorkItemLinkSources.GithubCrossReference, link.Source);

        var cursorData = TimelineCursor.TryParse((await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.Timeline)).CursorValue);
        Assert.Equal(9001, cursorData!.Items[3].Position);
        Assert.Equal(IssueUpdatedAt, cursorData.Items[3].LastVisit); // gate armed against the item's updated_at (D3)

        // repoll: dedupe key + written entry → no second row, no second request
        var handler2 = new PollerTestSupport.StubHandler();
        handler2.Enqueue(PollerTestSupport.TimelineJson(PollerTestSupport.CrossReferencedJson(9001, sourceNumber: 7)));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler2), CancellationToken.None);
        Assert.Empty(handler2.Requests); // visit gate closed — nothing re-fetched
        Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync());
        Assert.Single(await db.WorkItemLinks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CrossReference_UnknownPr_HoldsBack_ThenResolves() // boundary pattern, checklist 1
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = SeedIssue(db, repo, 3); // PR 7 unknown to the store; visits run 3 → 7

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(PollerTestSupport.CrossReferencedJson(9001, sourceNumber: 7)));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync()); // raw record persists
        Assert.Empty(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        var heldCursor = await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.Timeline);
        Assert.Null(heldCursor.CursorValue); // entry frozen: visit gate + position stay open

        // PR 7 arrives; the same window resolves the link
        PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);
        var handler2 = new PollerTestSupport.StubHandler();
        handler2.Enqueue(PollerTestSupport.TimelineJson(PollerTestSupport.CrossReferencedJson(9001, sourceNumber: 7)));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler2), CancellationToken.None);

        var link = Assert.Single(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        Assert.Equal(WorkItemLinkRelations.RelatedTo, link.RelationType);
        Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync()); // dedupe — no duplicate raw row
        var cursorData = TimelineCursor.TryParse((await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.Timeline)).CursorValue);
        Assert.Equal(9001, cursorData!.Items[3].Position); // now advances past the event
    }

    [Fact]
    public async Task CrossReference_FromOtherRepository_NoLink() // decision 51 scope
    {
        var (db, repo) = await NewDbAndRepoAsync();
        SeedIssue(db, repo, 3); // visits run 3 → 7
        PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.CrossReferencedJson(9001, sourceNumber: 7, sourceIsPr: true, repo: "other-org/other")));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync()); // raw record survives
        Assert.Empty(await db.WorkItemLinks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CrossReference_IssueReferencesIssue_NoLink() // D1: V1 links are Issue↔PR only
    {
        var (db, repo) = await NewDbAndRepoAsync();
        SeedIssue(db, repo, 12);
        PollerTestSupport.SeedWorkItem(db, repo, 9, WorkItemTypes.Issue);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.CrossReferencedJson(9001, sourceNumber: 9, sourceIsPr: false)));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Empty(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task MirrorCrossReference_PullRequestOwner_CreatesSameLink() // PR↔issue mirror writes the same pair
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = SeedIssue(db, repo, 12);
        var pr = PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest, updatedAt: "2026-11-19T17:00:00Z");

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.CrossReferencedJson(9100, sourceNumber: 12, sourceIsPr: false)));
        var result = await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Completed, result.Kind);
        Assert.Contains(handler.Requests, r => r.Contains("issues/7/timeline"));
        var link = Assert.Single(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        Assert.Equal(issue.Id, link.IssueWorkItemId);
        Assert.Equal(pr.Id, link.PrWorkItemId);
        Assert.Equal(WorkItemLinkRelations.RelatedTo, link.RelationType);
    }

    [Fact]
    public async Task ClosedWithClosingKeywords_CreatesClosesLink() // checklist 1, D2 option A + ad-hoc read
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = SeedIssue(db, repo, 3); // visits run 3 → 7
        var pr = PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ClosedTimelineJson(9002, commitId: "fix-sha-1", commitMessage: "Fix the crash\\n\\nfixes #3")));
        handler.Enqueue(PollerTestSupport.IntroducersJson((7, "2026-11-19T13:00:00Z"))); // ad-hoc commits/{sha}/pulls
        var result = await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Completed, result.Kind);
        Assert.Contains(handler.Requests, r => r.Contains("commits/fix-sha-1/pulls"));
        Assert.Contains(handler.Requests, r => r.Contains("issues/3/timeline"));

        var rawClosed = await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.EventType == GitHubTimelineEvents.Closed);
        Assert.Equal("tl:9002", rawClosed.DedupeKey);
        Assert.Equal(issue.Id, rawClosed.WorkItemId);

        var link = Assert.Single(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        Assert.Equal(issue.Id, link.IssueWorkItemId);
        Assert.Equal(pr.Id, link.PrWorkItemId);
        Assert.Equal(WorkItemLinkRelations.Closes, link.RelationType);
        Assert.Equal(WorkItemLinkSources.GithubClosingReference, link.Source);
    }

    [Fact]
    public async Task ClosedWithoutClosingKeywords_NoLink_NoAdHocLookup() // strict keyword rule (D2 option A)
    {
        var (db, repo) = await NewDbAndRepoAsync();
        SeedIssue(db, repo, 12);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ClosedTimelineJson(9003, commitId: "plain-sha", commitMessage: "Update the docs")));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync(), e => e.EventType == GitHubTimelineEvents.Closed);
        Assert.Empty(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(handler.Requests, r => r.Contains("commits/")); // no keywords → no ad-hoc read
        var cursorData = TimelineCursor.TryParse((await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.Timeline)).CursorValue);
        Assert.Equal(9003, cursorData!.Items[12].Position); // resolvable → position advances
    }

    [Fact]
    public async Task ClosedViaDirectKeywordCommit_NoPr_NoLink() // decision 51: Issue↔PR links only
    {
        var (db, repo) = await NewDbAndRepoAsync();
        SeedIssue(db, repo, 12);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ClosedTimelineJson(9004, commitId: "direct-sha", commitMessage: "fixes #12")));
        handler.Enqueue(PollerTestSupport.IntroducersJson()); // commits/{sha}/pulls → no associated PR
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Empty(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync()); // raw record only
    }

    [Fact]
    public async Task ClosedIntroducerUnknown_HoldsBack_ThenResolves()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        SeedIssue(db, repo, 3); // merged PR 7 not synced yet; visits run 3 → 7

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ClosedTimelineJson(9005, commitId: "fix-sha-2", commitMessage: "closes #12")));
        handler.Enqueue(PollerTestSupport.IntroducersJson((7, "2026-11-19T13:00:00Z")));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Empty(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        Assert.Null((await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.Timeline)).CursorValue);

        PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);
        var handler2 = new PollerTestSupport.StubHandler();
        handler2.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ClosedTimelineJson(9005, commitId: "fix-sha-2", commitMessage: "closes #12")));
        handler2.Enqueue(PollerTestSupport.IntroducersJson((7, "2026-11-19T13:00:00Z")));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler2), CancellationToken.None);

        Assert.Equal(WorkItemLinkRelations.Closes, Assert.Single(await db.WorkItemLinks.AsNoTracking().ToListAsync()).RelationType);
        Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync()); // dedupe idempotent over the holdback
    }

    [Fact]
    public async Task ClosedTransientLookupFails_FailedStream_ThenResolves() // 5xx never means "no PR"
    {
        var (db, repo) = await NewDbAndRepoAsync();
        SeedIssue(db, repo, 3); // visits run 3 → 7
        var pr = PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ClosedTimelineJson(9006, commitId: "fix-sha-3", commitMessage: "fixes #3")));
        handler.Enqueue(HttpStatusCode.InternalServerError, """{"message": "boom"}""");
        var result = await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Failed, result.Kind);
        Assert.Empty(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        Assert.Null((await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.Timeline)).CursorValue);

        var handler2 = new PollerTestSupport.StubHandler();
        handler2.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ClosedTimelineJson(9006, commitId: "fix-sha-3", commitMessage: "fixes #3")));
        handler2.Enqueue(PollerTestSupport.IntroducersJson((7, "2026-11-19T13:00:00Z")));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler2), CancellationToken.None);
        Assert.Single(await db.WorkItemLinks.AsNoTracking().ToListAsync()).PrWorkItemId.Equals(pr.Id);
    }

    [Fact]
    public async Task PullRequestOwner_ClosedEvent_NeverCreatesLink() // a PR's own closure is not a closes source
    {
        var (db, repo) = await NewDbAndRepoAsync();
        SeedIssue(db, repo, 12);
        PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest, updatedAt: "2026-11-19T17:00:00Z");

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ClosedTimelineJson(9007, commitId: "head-sha-a", commitMessage: "fixes #12")));
        handler.Enqueue(PollerTestSupport.IntroducersJson((7, "2026-11-19T13:00:00Z"))); // never consulted
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Empty(await db.WorkItemLinks.AsNoTracking().ToListAsync());
        Assert.DoesNotContain(handler.Requests, r => r.Contains("commits/"));
        var raw = Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync());
        Assert.Equal(GitHubTimelineEvents.Closed, raw.EventType);
    }

    [Fact]
    public async Task ConnectDisconnect_RawRecords_NoLinks() // checklist 8 — decision 79
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = SeedIssue(db, repo, 12);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.TimelineEventJson(9010, GitHubTimelineEvents.Connected),
            PollerTestSupport.TimelineEventJson(9011, GitHubTimelineEvents.Disconnected)));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(GitHubEventSources.TimelineApi, e.Source);
            Assert.Equal(issue.Id, e.WorkItemId);
        });
        Assert.Contains(events, e => e.EventType == GitHubTimelineEvents.Connected);
        Assert.Contains(events, e => e.EventType == GitHubTimelineEvents.Disconnected);
        Assert.Empty(await db.WorkItemLinks.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ReviewRequestEvents_ProducedFromTimeline() // checklist 5 — decision 31 event source
    {
        var (db, repo) = await NewDbAndRepoAsync();
        PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ReviewRequestTimelineJson(9020, removed: false),
            PollerTestSupport.ReviewRequestTimelineJson(9021, removed: true)));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        var events = await db.GitHubEvents.AsNoTracking().OrderBy(e => e.EventType).ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(GitHubEventSources.TimelineApi, e.Source));
        Assert.Equal(GitHubEventTypes.ReviewRequested, events[0].EventType);
        Assert.Equal(GitHubEventTypes.ReviewRequestRemoved, events[1].EventType);
        using (var payload = JsonDocument.Parse(events[0].Payload))
        {
            Assert.Equal(7, payload.RootElement.GetProperty("pr_number").GetInt64());
            Assert.Equal("pi-bot", payload.RootElement.GetProperty("reviewer").GetString());
        }
    }

    [Fact]
    public async Task TeamOnlyReviewRequest_Skipped_PositionStillAdvances()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ReviewRequestTimelineJson(9022, removed: false, reviewer: null, team: "core")));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Empty(await db.GitHubEvents.AsNoTracking().ToListAsync());
        var cursorData = TimelineCursor.TryParse((await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.Timeline)).CursorValue);
        Assert.Equal(9022, cursorData!.Items[7].Position); // position advanced → no refetch loop
    }

    [Fact]
    public async Task OutOfScopeEvents_AdvancePositionWithoutPersisting()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        SeedIssue(db, repo, 12);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.TimelineEventJson(9030, "commented"),
            PollerTestSupport.TimelineEventJson(9031, "labeled")));
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Empty(await db.GitHubEvents.AsNoTracking().ToListAsync());
        var cursorData = TimelineCursor.TryParse((await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.Timeline)).CursorValue);
        Assert.Equal(9031, cursorData!.Items[12].Position);
    }

    [Fact]
    public async Task UnchangedItem_VisitedOnce_ThenSkipped() // D3 gate
    {
        var (db, repo) = await NewDbAndRepoAsync();
        SeedIssue(db, repo, 12);

        var first = new PollerTestSupport.StubHandler(); // empty timeline page — fallback returns "[]"
        var result1 = await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, first), CancellationToken.None);
        Assert.Equal(StreamResultKind.Completed, result1.Kind);
        Assert.Single(first.Requests, r => r.Contains("issues/12/timeline"));

        var second = new PollerTestSupport.StubHandler();
        var result2 = await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, second), CancellationToken.None);
        Assert.Equal(StreamResultKind.NotModified, result2.Kind);
        Assert.Empty(second.Requests); // no request until updated_at moves

        // a body edit bumps updated_at → the item is visited again
        var row = await db.WorkItems.SingleAsync(w => w.Number == 12);
        row.UpdatedAtGithub = new DateTime(2026, 11, 19, 18, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();

        var third = new PollerTestSupport.StubHandler();
        var result3 = await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, third), CancellationToken.None);
        Assert.Equal(StreamResultKind.Completed, result3.Kind);
        Assert.Single(third.Requests, r => r.Contains("issues/12/timeline"));
    }

    [Fact]
    public async Task WalkStopsAtStoredPosition_OldPagesNotRefetched() // asc order + event-id position
    {
        var (db, repo) = await NewDbAndRepoAsync();
        SeedIssue(db, repo, 12);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.TimelineEventJson(9001, GitHubTimelineEvents.Connected, created: "2026-11-19T10:00:00Z"),
            PollerTestSupport.TimelineEventJson(9002, GitHubTimelineEvents.Disconnected, created: "2026-11-19T11:00:00Z")),
            nextUrl: "https://api.github.com/repos/octo-org/example/issues/12/timeline?page=2");
        handler.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.ClosedTimelineJson(9003))); // second page
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count); // both pages walked once
        Assert.Equal(9003, TimelineCursor.TryParse(
            (await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.Timeline)).CursorValue)!.Items[12].Position);

        // item changes again: page 1 is fully behind the position → skipped, only the frontier page refetches
        var row = await db.WorkItems.SingleAsync(w => w.Number == 12);
        row.UpdatedAtGithub = new DateTime(2026, 11, 19, 19, 0, 0, DateTimeKind.Utc);
        await db.SaveChangesAsync();
        var handler2 = new PollerTestSupport.StubHandler();
        handler2.Enqueue(PollerTestSupport.TimelineJson(
            PollerTestSupport.TimelineEventJson(9001, GitHubTimelineEvents.Connected, created: "2026-11-19T10:00:00Z"),
            PollerTestSupport.TimelineEventJson(9002, GitHubTimelineEvents.Disconnected, created: "2026-11-19T11:00:00Z")),
            nextUrl: "https://api.github.com/repos/octo-org/example/issues/12/timeline?page=2"); // page 1 unchanged, fully behind
        handler2.Enqueue(PollerTestSupport.TimelineJson(PollerTestSupport.ClosedTimelineJson(9004))); // frontier page
        await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler2), CancellationToken.None);

        Assert.Equal(2, handler2.Requests.Count); // page 1 re-checked (one request), page 2 walked
        Assert.Equal(9004, TimelineCursor.TryParse(
            (await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.Timeline)).CursorValue)!.Items[12].Position);
    }

    [Fact]
    public async Task BranchNamingAlone_NeverCreatesLink() // checklist 2 — negative across the sync streams
    {
        var (db, repo) = await NewDbAndRepoAsync();

        // PR named by convention (agent/issue-12) with NO explicit reference to the issue anywhere:
        // no cross-referenced event, no closed-with-keywords commit on the issue timeline
        var prPayload = """
        {
          "node_id": "PR_7", "number": 7, "title": "named by convention", "body": "no reference at all",
          "state": "closed", "user": {"login": "alice"}, "labels": [], "assignees": [],
          "head": {"ref": "agent/issue-12", "sha": "head-sha-agent-12"}, "base": {"sha": "base-sha-a"},
          "draft": false, "merged": false, "merged_at": null,
          "created_at": "2026-11-02T08:00:00Z", "updated_at": "2026-11-19T16:00:00Z", "closed_at": "2026-11-19T16:00:00Z"
        }
        """;

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "root issue", "2026-11-19T16:00:00Z")));
        handler.Enqueue(PollerTestSupport.ArrayJson(prPayload));
        await new IssuesStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);
        await new PullRequestsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);
        Assert.Equal(2, (await db.WorkItems.AsNoTracking().ToListAsync()).Count); // both items synced

        var timelineHandler = new PollerTestSupport.StubHandler(); // timelines: no link-creating events at all
        var result = await new TimelineStream().SyncAsync(PollerTestSupport.NewRun(db, repo, timelineHandler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Completed, result.Kind);
        Assert.Empty(await db.WorkItemLinks.AsNoTracking().ToListAsync()); // the executable negative
        var timelineEvents = await db.GitHubEvents.AsNoTracking()
            .Where(e => e.Source == GitHubEventSources.TimelineApi).ToListAsync();
        Assert.Empty(timelineEvents); // timelines carried nothing — naming alone produced no link source
    }
}
