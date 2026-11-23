using PiAgent.Poller.Streams;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Poller;
using Xunit;
using PiAgent.Platform.Http;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// Deleted-comment daily sweep over real postgres with a stubbed GitHub (task 05, decision 77):
/// tombstone only on a confirmed direct-404, 403/network/rate-limit outcomes never tombstone, the
/// sweep's own run state, non-interference with the six incremental cursors, and the daily due gate.
/// </summary>
public class DeletedCommentSweepTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private async Task<(AppDbContext Db, Repository Repo)> NewDbAndRepoAsync()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var repo = await PollerTestSupport.SeedRepositoryAsync(db);
        return (db, repo);
    }

    private static SyncCursor SeedCursor(AppDbContext db, Repository repo, string stream, string? value)
    {
        var cursor = new SyncCursor
        {
            Id = Guid.NewGuid(),
            RepositoryId = repo.Id,
            Stream = stream,
            CursorValue = value,
            LastSuccessAt = DateTime.UtcNow.AddHours(-1),
            LastAttemptAt = DateTime.UtcNow.AddHours(-1),
            UpdatedAt = DateTime.UtcNow.AddHours(-1),
        };
        db.SyncCursors.Add(cursor);
        db.SaveChanges();
        return cursor;
    }

    private IReadOnlyDictionary<string, string?> IncrementalCursorValues(AppDbContext db, Guid repositoryId) =>
        db.SyncCursors.AsNoTracking()
            .Where(c => c.RepositoryId == repositoryId && c.Stream != SyncCursorStreams.DeletedCommentSweep)
            .ToDictionary(c => c.Stream, c => c.CursorValue);

    [Fact]
    public async Task Confirmed404_Tombstones_AndEmitsDeletedEvent() // checklist 3 first half
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue);
        PollerTestSupport.SeedComment(db, issue, GitHubCommentKinds.IssueComment, 999);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueCommentJson(1111, 12, "newer comment", "2026-11-19T10:00:00Z"))); // 999 not listed
        handler.Enqueue(HttpStatusCode.NotFound, """{"message": "Not Found"}"""); // direct re-check
        var result = await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Completed, result.Kind);
        var row = await db.GitHubComments.AsNoTracking().SingleAsync(c => c.GithubCommentId == 999);
        Assert.True(row.IsDeleted);
        Assert.NotNull(row.DeletedAt);

        var deletedEvent = Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync());
        Assert.Equal(GitHubEventTypes.IssueCommentDeleted, deletedEvent.EventType);
        Assert.Equal(GitHubEventSources.FullReconciliation, deletedEvent.Source);
        Assert.Equal("ic:999:deleted", deletedEvent.DedupeKey);
        Assert.Equal(issue.Id, deletedEvent.WorkItemId);
        using (var payload = JsonDocument.Parse(deletedEvent.Payload))
        {
            Assert.Equal(999, payload.RootElement.GetProperty("comment_id").GetInt64());
            Assert.Equal(12, payload.RootElement.GetProperty("issue_number").GetInt64());
            Assert.Equal("bob", payload.RootElement.GetProperty("author").GetString());
        }

        // the tombstone row survives — exclusion from future Task context is the flag's job (ticket 11)
        // (1111 appears in the listing but was never stored — the sweep only compares stored ids)
        Assert.Single(await db.GitHubComments.AsNoTracking().ToListAsync());
        Assert.Single(await db.GitHubComments.AsNoTracking().Where(c => c.IsDeleted).ToListAsync());

        var sweepCursor = await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.DeletedCommentSweep);
        Assert.NotNull(sweepCursor.LastSuccessAt);
        Assert.Null(sweepCursor.CursorValue); // run state only — no pagination state
        Assert.Null(sweepCursor.LastError);
    }

    [Fact]
    public async Task NonRateLimit403_NeverTombstones_SweepFails() // checklist 3 second half
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue);
        PollerTestSupport.SeedComment(db, issue, GitHubCommentKinds.IssueComment, 999);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson()); // listing: nothing
        handler.Enqueue(HttpStatusCode.Forbidden); // direct re-check: resource NOT confirmed gone
        var result = await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Failed, result.Kind);
        var row = await db.GitHubComments.AsNoTracking().SingleAsync(c => c.GithubCommentId == 999);
        Assert.False(row.IsDeleted);
        Assert.Null(row.DeletedAt);
        Assert.Empty(await db.GitHubEvents.AsNoTracking().ToListAsync());

        var sweepCursor = await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.DeletedCommentSweep);
        Assert.Null(sweepCursor.LastSuccessAt); // success gate stays closed → retried next cycle
        Assert.NotNull(sweepCursor.LastError);
    }

    [Fact]
    public async Task NetworkFailure_NeverTombstones()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue);
        PollerTestSupport.SeedComment(db, issue, GitHubCommentKinds.IssueComment, 999);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson());
        handler.EnqueueThrow(new HttpRequestException("connection reset"));
        var result = await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Failed, result.Kind);
        var row = await db.GitHubComments.AsNoTracking().SingleAsync(c => c.GithubCommentId == 999);
        Assert.False(row.IsDeleted);
        Assert.Empty(await db.GitHubEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task PrimaryRateLimit_Propagates_NothingTombstoned() // decision 53 pattern
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue);
        PollerTestSupport.SeedComment(db, issue, GitHubCommentKinds.IssueComment, 999);

        var rateLimited = new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
        rateLimited.Headers.Add("X-RateLimit-Remaining", "0");
        rateLimited.Headers.Add("Retry-After", "30");

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson());
        handler.EnqueueResponse(rateLimited);
        await Assert.ThrowsAsync<GitHubRateLimitedException>(() =>
            new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None));

        var row = await db.GitHubComments.AsNoTracking().SingleAsync(c => c.GithubCommentId == 999);
        Assert.False(row.IsDeleted); // ambiguous outcome — never a tombstone
        Assert.Empty(await db.GitHubEvents.AsNoTracking().ToListAsync());
        var sweepCursor = await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.DeletedCommentSweep);
        Assert.Null(sweepCursor.LastSuccessAt);
    }

    [Fact]
    public async Task Success_DoesNotTouchIncrementalCursors() // checklist 4
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue);
        var pr = PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);
        var conversation = PollerTestSupport.SeedComment(db, issue, GitHubCommentKinds.IssueComment, 999);
        var inline = PollerTestSupport.SeedComment(db, pr, GitHubCommentKinds.ReviewComment, 555);

        var before = new[] { SyncCursorStreams.Issues, SyncCursorStreams.Pulls, SyncCursorStreams.PrReviews,
            SyncCursorStreams.IssueComments, SyncCursorStreams.PrReviewComments, SyncCursorStreams.Timeline };
        foreach (var stream in before)
            SeedCursor(db, repo, stream, $"pre-existing-{stream}-value");
        var snapshot = IncrementalCursorValues(db, repo.Id);

        var handler = new PollerTestSupport.StubHandler();
        // listing contains both ids → nothing missing → no direct checks
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueCommentJson(999, 12, "conversation", "2026-11-19T10:00:00Z")));
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.ReviewCommentJson(555, 7, "inline", "2026-11-19T10:00:00Z"))); // open PR 7 listing
        var result = await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.NotModified, result.Kind);
        Assert.Equal(snapshot, IncrementalCursorValues(db, repo.Id)); // byte-identical cursor values

        // tombstone pass must stay non-interfering as well
        var sweepCursorRow = await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.DeletedCommentSweep);
        sweepCursorRow.LastSuccessAt = DateTime.UtcNow.AddHours(-25); // due again
        await db.SaveChangesAsync();

        var handler2 = new PollerTestSupport.StubHandler();
        handler2.Enqueue(PollerTestSupport.ArrayJson()); // conversation: neither listed
        handler2.Enqueue(PollerTestSupport.ArrayJson()); // inline: not listed
        handler2.Enqueue(HttpStatusCode.NotFound, """{"message": "Not Found"}""");
        handler2.Enqueue(HttpStatusCode.NotFound, """{"message": "Not Found"}""");
        await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler2), CancellationToken.None);

        Assert.Equal(snapshot, IncrementalCursorValues(db, repo.Id)); // still byte-identical
        Assert.Equal(2, (await db.GitHubComments.AsNoTracking().Where(c => c.IsDeleted).ToListAsync()).Count);
    }

    [Fact]
    public async Task InlineListing_OnlyOpenPullRequests() // decision 77 + D5
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var pr = PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);
        pr.State = "closed"; // merged/closed — no listing, no re-check
        await db.SaveChangesAsync();
        PollerTestSupport.SeedComment(db, pr, GitHubCommentKinds.ReviewComment, 555);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson()); // conversation listing
        var result = await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.NotModified, result.Kind);
        Assert.DoesNotContain(handler.Requests, r => r.Contains("pulls/7/comments"));
        var row = await db.GitHubComments.AsNoTracking().SingleAsync(c => c.GithubCommentId == 555);
        Assert.False(row.IsDeleted); // D5: deletions on closed PRs are never discovered — accepted limitation
    }

    [Fact]
    public async Task InlineListing_OpenPullRequest_WalksPages()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var pr = PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);
        PollerTestSupport.SeedComment(db, pr, GitHubCommentKinds.ReviewComment, 555);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson()); // conversation listing
        handler.Enqueue(PollerTestSupport.ArrayJson(),
            nextUrl: "https://api.github.com/repos/octo-org/example/pulls/7/comments?page=2"); // inline page 1 (no 555)
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.ReviewCommentJson(555, 7, "inline", "2026-11-19T10:00:00Z"))); // inline page 2
        var result = await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.NotModified, result.Kind);
        Assert.False((await db.GitHubComments.AsNoTracking().SingleAsync(c => c.GithubCommentId == 555)).IsDeleted);
        Assert.Equal(3, handler.Requests.Count); // conversation + inline page 1 + inline page 2
    }

    [Fact]
    public async Task ReviewCommentTombstone_UsesReviewCommentDeletedEvent()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var pr = PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);
        PollerTestSupport.SeedComment(db, pr, GitHubCommentKinds.ReviewComment, 555);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson()); // conversation listing
        handler.Enqueue(PollerTestSupport.ArrayJson()); // open-PR inline listing: 555 missing
        handler.Enqueue(HttpStatusCode.NotFound, """{"message": "Not Found"}""");
        await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.True((await db.GitHubComments.AsNoTracking().SingleAsync(c => c.GithubCommentId == 555)).IsDeleted);
        var deletedEvent = Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync());
        Assert.Equal(GitHubEventTypes.ReviewCommentDeleted, deletedEvent.EventType);
        Assert.Equal("rc:555:deleted", deletedEvent.DedupeKey);
        Assert.Contains(handler.Requests, r => r.Contains("/pulls/comments/555")); // direct re-check path
    }

    [Fact]
    public async Task DailyDueGate_SkipsUntilIntervalElapses() // decision 77 daily cadence (D4)
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var issue = PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue);
        PollerTestSupport.SeedComment(db, issue, GitHubCommentKinds.IssueComment, 999);

        // first-ever run: no prior success → the sweep runs immediately and tombstones
        var first = new PollerTestSupport.StubHandler();
        first.Enqueue(PollerTestSupport.ArrayJson()); // listing: 999 missing
        first.Enqueue(HttpStatusCode.NotFound, """{"message": "Not Found"}""");
        var firstResult = await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, first), CancellationToken.None);
        Assert.Equal(StreamResultKind.Completed, firstResult.Kind);
        Assert.True((await db.GitHubComments.AsNoTracking().SingleAsync(c => c.GithubCommentId == 999)).IsDeleted);

        // recent success → skip whole pass, no requests at all
        var recent = new PollerTestSupport.StubHandler();
        var recentResult = await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, recent), CancellationToken.None);
        Assert.Equal(StreamResultKind.NotModified, recentResult.Kind);
        Assert.Empty(recent.Requests);

        var after = await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.DeletedCommentSweep);
        after.LastSuccessAt = DateTime.UtcNow.AddHours(-25); // a day elapses
        await db.SaveChangesAsync();

        // due again: the listing + direct re-check run; the tombstone is idempotent, the event deduped
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson());
        handler.Enqueue(HttpStatusCode.NotFound, """{"message": "Not Found"}""");
        var dueResult = await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);
        Assert.Equal(StreamResultKind.Completed, dueResult.Kind);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync()); // ic:999:deleted — no duplicate
        Assert.True((await db.GitHubComments.AsNoTracking().SingleAsync(c => c.GithubCommentId == 999)).IsDeleted);
    }

    [Fact]
    public async Task FirstRun_CreatesSweepCursorRow()
    {
        var (db, repo) = await NewDbAndRepoAsync();

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson());
        var result = await new DeletedCommentSweepStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.NotModified, result.Kind);
        var sweepCursor = await db.SyncCursors.SingleAsync(c => c.Stream == SyncCursorStreams.DeletedCommentSweep);
        Assert.NotNull(sweepCursor.LastSuccessAt);
    }
}
