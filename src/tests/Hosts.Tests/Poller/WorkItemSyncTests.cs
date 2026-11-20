using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Poller;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// WorkItem streams over real postgres with a stubbed GitHub (task 04 plan task-5): bootstrap +
/// incremental behavior, per-page transactional cursor advance, fingerprint dedupe on overlap/re-poll,
/// restart recovery, and the failing-save contract (cursor unadvanced, zero partial rows).
/// </summary>
public class WorkItemSyncTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string IssueT0 = "2026-11-19T10:00:00Z";
    private const string PrT0 = "2026-11-19T10:30:00Z";
    private const string T1 = "2026-11-19T15:00:00Z";

    private async Task<(AppDbContext Db, Repository Repo)> NewDbAndRepoAsync()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var repo = await PollerTestSupport.SeedRepositoryAsync(db);
        return (db, repo);
    }

    private Task<SyncCursor> CursorAsync(AppDbContext db, Repository repo, string stream) =>
        db.SyncCursors.SingleAsync(c => c.RepositoryId == repo.Id && c.Stream == stream);

    [Fact]
    public async Task IssuesStream_FirstRun_CreatesWorkItemsAndWorkItemCreatedEvents()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.IssueJson(1, "first", IssueT0),
            PollerTestSupport.IssueJson(2, "second", PrT0)), nextUrl: "https://api.github.com/repos/octo-org/example/issues?per_page=100&page=2");
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.IssueJson(3, "third", T1)));

        var result = await new IssuesStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Completed, result.Kind);
        Assert.Equal(2, handler.Requests.Count); // walked both pages
        var rows = await db.WorkItems.AsNoTracking().ToListAsync();
        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.Equal(WorkItemTypes.Issue, row.Type));
        Assert.Equal(["alice", "alice", "alice"], rows.OrderBy(r => r.Number).Select(r => r.AuthorLogin));
        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Equal(3, events.Count);
        Assert.All(events, e =>
        {
            Assert.Equal(GitHubEventTypes.WorkItemCreated, e.EventType);
            Assert.Equal(GitHubEventSources.RestResourceSync, e.Source);
            Assert.Equal(GitHubEventStatuses.Pending, e.ProcessingStatus);
            Assert.NotNull(e.WorkItemId);
            Assert.NotNull(e.DedupeKey);
        });
        var cursor = await CursorAsync(db, repo, SyncCursorStreams.Issues);
        var position = TimestampCursor.TryParse(cursor.CursorValue);
        Assert.Equal(new DateTime(2026, 11, 19, 15, 0, 0, DateTimeKind.Utc), position!.Position);
        Assert.NotNull(cursor.LastSuccessAt);
        Assert.Null(cursor.LastError);
    }

    [Fact]
    public async Task FailingSave_CursorUnadvanced_NoPartialRows() // Review Focus 4
    {
        var (db, repo) = await NewDbAndRepoAsync();
        db.SyncCursors.Add(new SyncCursor
        {
            Id = Guid.NewGuid(),
            RepositoryId = repo.Id,
            Stream = SyncCursorStreams.Issues,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(1, "first", IssueT0)));

        await using var failingDb = postgres.NewDbContext(new ThrowingOnceSaveChangesInterceptor());
        var run = PollerTestSupport.NewRun(failingDb, repo, handler);
        var result = await new IssuesStream().SyncAsync(run, CancellationToken.None);

        Assert.Equal(StreamResultKind.Failed, result.Kind);
        Assert.Contains("simulated save failure", result.Error);
        var cursor = await CursorAsync(failingDb, repo, SyncCursorStreams.Issues);
        Assert.Null(cursor.CursorValue); // unadvanced
        Assert.Null(cursor.LastSuccessAt);
        Assert.NotNull(cursor.LastAttemptAt);
        Assert.Contains("simulated save failure", cursor.LastError);
        Assert.Empty(await db.WorkItems.AsNoTracking().ToListAsync());     // no partial rows
        Assert.Empty(await db.GitHubEvents.AsNoTracking().ToListAsync());
    }

    private sealed class ThrowingOnceSaveChangesInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        private int _calls;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
                throw new IOException("simulated save failure");
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task IssuesStream_SkipsPullRequestPayloads()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson($$"""
            {
              "node_id": "PR_5", "number": 5, "title": "a pr in the issues listing", "state": "open",
              "user": {"login": "alice"}, "labels": [], "assignees": [],
              "created_at": "2026-11-01T08:00:00Z", "updated_at": "{{PrT0}}",
              "pull_request": {"url": "https://api.github.com/repos/octo-org/example/pulls/5"}
            }
            """));

        var result = await new IssuesStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Completed, result.Kind);
        Assert.Empty(await db.WorkItems.AsNoTracking().ToListAsync());
        Assert.Empty(await db.GitHubEvents.AsNoTracking().ToListAsync());
        var cursor = await CursorAsync(db, repo, SyncCursorStreams.Issues);
        Assert.Equal(new DateTime(2026, 11, 19, 10, 30, 0, DateTimeKind.Utc),
            TimestampCursor.TryParse(cursor.CursorValue)!.Position); // position still advances past skipped items
    }

    [Fact]
    public async Task PullRequestsStream_FirstRun_CreatesPrRows_CompleteFields()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.PullJson(7, "feature pr", PrT0, reviewers: ["pi-bot"]),
            PollerTestSupport.PullJson(9, "merged pr", T1, state: "closed",
                mergedAt: "2026-11-19T14:00:00Z", closedAt: "2026-11-19T14:00:00Z")));

        var result = await new PullRequestsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Completed, result.Kind);
        var rows = (await db.WorkItems.AsNoTracking().ToListAsync()).OrderBy(r => r.Number).ToList();
        Assert.Equal([7, 9], rows.Select(r => r.Number));
        var open = rows[0];
        Assert.Equal(WorkItemTypes.PullRequest, open.Type);
        Assert.Equal("head-sha-a", open.HeadSha);
        Assert.Equal("base-sha-a", open.BaseSha);
        Assert.False(open.Draft!.Value);
        Assert.Equal(["pi-bot"], System.Text.Json.JsonSerializer.Deserialize<string[]>(open.RequestedReviewers!)!);
        Assert.Null(open.MergedAt);
        var merged = rows[1];
        Assert.Equal(new DateTime(2026, 11, 19, 14, 0, 0, DateTimeKind.Utc), merged.MergedAt);
        Assert.Equal(new DateTime(2026, 11, 19, 14, 0, 0, DateTimeKind.Utc), merged.ClosedAtGithub);
        Assert.NotNull(merged.ClosedAtLocal);

        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Equal(4, events.Count); // 2 × created + closed(merged) + review_requested(pi-bot on first sight)
        Assert.Equal(2, events.Count(e => e.EventType == GitHubEventTypes.WorkItemCreated));
        var closed = events.Single(e => e.EventType == GitHubEventTypes.WorkItemClosed);
        using var payload = System.Text.Json.JsonDocument.Parse(closed.Payload);
        Assert.Equal("merged", payload.RootElement.GetProperty("close_reason").GetString()); // decision 40
        var requested = events.Single(e => e.EventType == GitHubEventTypes.ReviewRequested);
        Assert.Equal(GitHubEventSources.SnapshotDiff, requested.Source);
    }

    [Fact]
    public async Task SecondRun_SinceWindow_ChangesProduceUpdatedAndClosedAndSynchronizedAndRequestEvents()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "issue", IssueT0)));
        await new IssuesStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.PullJson(7, "feature pr", PrT0,
            reviewers: ["pi-bot"])));
        await new PullRequestsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        // second cycle: title edit on the issue; the PR merges, moves its head, drops pi-bot and adds carol
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "renamed issue", T1)));
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.PullJson(7, "feature pr", T1,
            state: "closed", headSha: "head-sha-b", reviewers: ["carol"], closedAt: T1, mergedAt: T1)));
        await new IssuesStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);
        await new PullRequestsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count(e => e.EventType == GitHubEventTypes.WorkItemCreated));
        var updated = events.Single(e => e.EventType == GitHubEventTypes.WorkItemUpdated);
        using var updatedPayload = System.Text.Json.JsonDocument.Parse(updated.Payload);
        Assert.Equal(12, updatedPayload.RootElement.GetProperty("number").GetInt64());
        var closed = events.Single(e => e.EventType == GitHubEventTypes.WorkItemClosed);
        using var closedPayload = System.Text.Json.JsonDocument.Parse(closed.Payload);
        Assert.Equal(7, closedPayload.RootElement.GetProperty("number").GetInt64());
        Assert.Equal("merged", closedPayload.RootElement.GetProperty("close_reason").GetString());
        var synchronizedEvent = events.Single(e => e.EventType == GitHubEventTypes.PullRequestSynchronized);
        Assert.Equal(GitHubEventSources.RestResourceSync, synchronizedEvent.Source);
        using var syncPayload = System.Text.Json.JsonDocument.Parse(synchronizedEvent.Payload);
        Assert.Equal("head-sha-a", syncPayload.RootElement.GetProperty("from_sha").GetString());
        Assert.Equal("head-sha-b", syncPayload.RootElement.GetProperty("to_sha").GetString());
        Assert.Equal(2, events.Count(e => e.EventType == GitHubEventTypes.ReviewRequested)); // pi-bot first sight + carol added
        var requested = events.Where(e => e.EventType == GitHubEventTypes.ReviewRequested)
            .OrderBy(e => e.OccurredAtGithub).Last();
        using var requestedPayload = System.Text.Json.JsonDocument.Parse(requested.Payload);
        Assert.Equal("carol", requestedPayload.RootElement.GetProperty("reviewer").GetString());
        Assert.Equal(GitHubEventSources.SnapshotDiff, requested.Source);
        var removed = events.Single(e => e.EventType == GitHubEventTypes.ReviewRequestRemoved);
        Assert.Equal(GitHubEventSources.SnapshotDiff, removed.Source);

        var pr = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Number == 7);
        Assert.Equal("head-sha-b", pr.HeadSha); // head updated on the row
        Assert.Equal("closed", pr.State);
        Assert.NotNull(pr.ClosedAtLocal);
        Assert.Equal(["carol"], System.Text.Json.JsonSerializer.Deserialize<string[]>(pr.RequestedReviewers!)!);
    }

    [Fact]
    public async Task Repoll_SameData_NoNewRowsOrEvents() // Review Focus 3
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "issue", IssueT0)));
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.PullJson(7, "feature pr", PrT0, reviewers: ["pi-bot"])));
        await new IssuesStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);
        await new PullRequestsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        // re-poll the exact same data (the 5-minute overlap window redelivers identical items)
        var handler2 = new PollerTestSupport.StubHandler();
        handler2.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "issue", IssueT0)));
        handler2.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.PullJson(7, "feature pr", PrT0, reviewers: ["pi-bot"])));
        await new IssuesStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler2), CancellationToken.None);
        await new PullRequestsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler2), CancellationToken.None);

        Assert.Equal(2, (await db.WorkItems.AsNoTracking().ToListAsync()).Count);
        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Equal(3, events.Count); // created ×2 + review_requested(pi-bot) — no duplicates
        Assert.Equal(2, events.Count(e => e.EventType == GitHubEventTypes.WorkItemCreated));
        Assert.Single(events, e => e.EventType == GitHubEventTypes.ReviewRequested);
    }

    [Fact]
    public async Task RestartRecovery_NewCycleInstance_NoDuplicates()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.PullJson(7, "feature pr", PrT0, reviewers: ["pi-bot"])));
        await new PullRequestsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);
        var cursorBefore = await CursorAsync(db, repo, SyncCursorStreams.Pulls);

        db.Dispose(); // simulate restart: fresh context + fresh stream instances, same DB

        var db2 = postgres.NewDbContext();
        var handler2 = new PollerTestSupport.StubHandler();
        handler2.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.PullJson(7, "feature pr", PrT0, reviewers: ["pi-bot"])));
        await new PullRequestsStream().SyncAsync(PollerTestSupport.NewRun(db2, repo, handler2), CancellationToken.None);

        Assert.Single(await db2.WorkItems.AsNoTracking().ToListAsync());
        var events = await db2.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count); // created + review_requested(pi-bot) — restart added nothing
        var cursorAfter = await CursorAsync(db2, repo, SyncCursorStreams.Pulls);
        Assert.Equal(cursorBefore.CursorValue, cursorAfter.CursorValue);
    }
}
