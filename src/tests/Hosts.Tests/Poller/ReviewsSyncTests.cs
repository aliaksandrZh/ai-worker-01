using PiAgent.Poller.Streams;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Poller;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// pr_reviews stream over real postgres with a stubbed GitHub (task 04 plan task-7): per-PR conditional
/// requests (304 skips), submitted/dismissed transition events, pending first-seen without events,
/// etag persistence in the cursor JSON, and the closed-PR fetch set.
/// </summary>
public class ReviewsSyncTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string SubmittedT0 = "2026-11-19T10:00:00Z";

    private async Task<(AppDbContext Db, Repository Repo, WorkItem Pr7, WorkItem Pr9)> NewDbAndRepoAsync()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var repo = await PollerTestSupport.SeedRepositoryAsync(db);
        var pr7 = PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest, updatedAt: "2026-11-12T08:00:00Z");
        var pr9 = PollerTestSupport.SeedWorkItem(db, repo, 9, WorkItemTypes.PullRequest, updatedAt: "2026-11-13T08:00:00Z");
        return (db, repo, pr7, pr9);
    }

    private static StreamRun NewRun(AppDbContext db, Repository repo, PollerTestSupport.StubHandler handler) =>
        PollerTestSupport.NewRun(db, repo, handler);

    [Fact]
    public async Task FirstRun_SubmittedReviews_CreateRowsAndSubmittedEvents()
    {
        var (db, repo, _, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.ReviewJson(77, 7, GitHubReviewStates.Approved, submittedAt: SubmittedT0)), etag: "w7");
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.ReviewJson(88, 9, GitHubReviewStates.ChangesRequested, submittedAt: SubmittedT0)), etag: "w9");

        // review pages come per open PR, ordered by number → PR 7 first, PR 9 second
        var result = await new PrReviewsStream().SyncAsync(NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Completed, result.Kind);
        var rows = await db.GitHubReviews.AsNoTracking().OrderBy(r => r.GithubReviewId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(GitHubReviewStates.Approved, rows[0].State);
        Assert.Equal("pi-bot", rows[0].AuthorLogin);
        Assert.Equal(SubmittedT0Timestamp, rows[0].SubmittedAtGithub);
        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal(GitHubEventTypes.PullRequestReviewSubmitted, e.EventType));
        Assert.All(events, e => Assert.Equal(GitHubEventSources.RestResourceSync, e.Source));
        var cursor = await db.SyncCursors.SingleAsync(c => c.RepositoryId == repo.Id && c.Stream == SyncCursorStreams.PrReviews);
        var parsed = PrReviewCursor.TryParse(cursor.CursorValue);
        Assert.Equal("w7", parsed!.Etags[7]);
        Assert.Equal("w9", parsed.Etags[9]);
    }

    private static readonly DateTime SubmittedT0Timestamp = new(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PendingThenSubmitted_EmitsSubmittedOnce()
    {
        var (db, repo, _, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.ReviewJson(77, 7, GitHubReviewStates.Pending)));
        await new PrReviewsStream().SyncAsync(NewRun(db, repo, handler), CancellationToken.None);
        Assert.Equal(GitHubReviewStates.Pending,
            (await db.GitHubReviews.AsNoTracking().SingleAsync(r => r.GithubReviewId == 77)).State);
        Assert.Empty(await db.GitHubEvents.AsNoTracking().ToListAsync()); // pending: no event

        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.ReviewJson(77, 7, GitHubReviewStates.Approved, submittedAt: SubmittedT0)));
        await new PrReviewsStream().SyncAsync(NewRun(db, repo, handler), CancellationToken.None);
        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Single(events, e => e.EventType == GitHubEventTypes.PullRequestReviewSubmitted);

        // same data again — fingerprint keeps the re-run silent
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.ReviewJson(77, 7, GitHubReviewStates.Approved, submittedAt: SubmittedT0)));
        await new PrReviewsStream().SyncAsync(NewRun(db, repo, handler), CancellationToken.None);
        Assert.Single(await db.GitHubEvents.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Dismissed_EmitsDismissed()
    {
        var (db, repo, _, _) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.ReviewJson(77, 7, GitHubReviewStates.Approved, submittedAt: SubmittedT0)));
        await new PrReviewsStream().SyncAsync(NewRun(db, repo, handler), CancellationToken.None);

        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.ReviewJson(77, 7, GitHubReviewStates.Dismissed)));
        await new PrReviewsStream().SyncAsync(NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(GitHubReviewStates.Dismissed,
            (await db.GitHubReviews.AsNoTracking().SingleAsync(r => r.GithubReviewId == 77)).State);
        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Single(events, e => e.EventType == GitHubEventTypes.PullRequestReviewDismissed);
    }

    [Fact]
    public async Task NotModified_SkipsPrWithoutConsumingAnything() // 304 costs nothing
    {
        var (db, repo, _, pr9) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.ReviewJson(77, 7, GitHubReviewStates.Approved, submittedAt: SubmittedT0)), etag: "w5");
        await new PrReviewsStream().SyncAsync(NewRun(db, repo, handler), CancellationToken.None);
        pr9.State = "closed"; // isolate: only PR 7 remains in the fetch set
        await db.SaveChangesAsync();
        var rowsBefore = await db.GitHubReviews.AsNoTracking().CountAsync();
        var eventsBefore = await db.GitHubEvents.AsNoTracking().CountAsync();

        var handler2 = new PollerTestSupport.StubHandler();
        handler2.Enqueue304("w5");
        var result = await new PrReviewsStream().SyncAsync(NewRun(db, repo, handler2), CancellationToken.None);

        Assert.Equal(StreamResultKind.NotModified, result.Kind);
        Assert.Equal("\"w5\"", handler2.IfNoneMatch.Single()); // If-None-Match carried the stored etag
        Assert.Single(handler2.Requests);                      // exactly one request per PR, no resend
        Assert.Equal(rowsBefore, await db.GitHubReviews.AsNoTracking().CountAsync());
        Assert.Equal(eventsBefore, await db.GitHubEvents.AsNoTracking().CountAsync());
        var cursor = await db.SyncCursors.SingleAsync(c => c.RepositoryId == repo.Id && c.Stream == SyncCursorStreams.PrReviews);
        Assert.Equal("w5", PrReviewCursor.TryParse(cursor.CursorValue)!.Etags[7]);
    }

    [Fact]
    public async Task ClosedPrAndIssues_NotRefetched()
    {
        var (db, repo, pr7, _) = await NewDbAndRepoAsync();
        pr7.State = "closed"; // merged/closed PRs keep their pre-closure reviews
        await db.SaveChangesAsync();
        PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue); // issues never fetch reviews

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.ReviewJson(88, 9, GitHubReviewStates.Commented, submittedAt: SubmittedT0)));
        await new PrReviewsStream().SyncAsync(NewRun(db, repo, handler), CancellationToken.None);

        Assert.Single(handler.Requests, path => path.Contains("/pulls/9/reviews"));
        Assert.DoesNotContain(handler.Requests, path => path.Contains("/pulls/7/reviews"));
        Assert.DoesNotContain(handler.Requests, path => path.Contains("12"));
    }
}
