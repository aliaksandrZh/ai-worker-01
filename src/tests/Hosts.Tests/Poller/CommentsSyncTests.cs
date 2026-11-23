using PiAgent.Poller.Streams;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Poller;
using Xunit;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// Comment streams over real postgres with a stubbed GitHub (task 04 plan task-6): row/event sync for
/// conversation and inline review comments, edit updates, work-item resolution holdback (Review Focus 1),
/// and re-poll idempotence.
/// </summary>
public class CommentsSyncTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string CommentT0 = "2026-11-19T10:00:00Z";
    private const string CommentT1 = "2026-11-19T12:00:00Z";

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
    public async Task FirstRun_CreatesCommentRows_AndCreatedEvents()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue);
        PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.IssueCommentJson(999, 12, "conversation comment", CommentT0)));
        var resultIssues = await new IssueCommentsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.ReviewCommentJson(555, 7, "inline nit", CommentT0)));
        var resultReviews = await new PrReviewCommentsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Equal(StreamResultKind.Completed, resultIssues.Kind);
        Assert.Equal(StreamResultKind.Completed, resultReviews.Kind);
        var rows = await db.GitHubComments.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        var conversation = rows.Single(r => r.Kind == GitHubCommentKinds.IssueComment);
        Assert.Equal(999, conversation.GithubCommentId);
        Assert.Equal("bob", conversation.AuthorLogin);
        Assert.Null(conversation.Path);
        var inline = rows.Single(r => r.Kind == GitHubCommentKinds.ReviewComment);
        Assert.Equal(555, inline.GithubCommentId);
        Assert.Equal("src/a.cs", inline.Path);
        Assert.Equal("head-sha-a", inline.CommitSha);
        Assert.Equal(5, inline.DiffPosition);
        Assert.Equal(554, inline.InReplyToCommentId);
        Assert.Equal(77, inline.GithubReviewId); // external ID only — no DB FK

        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Single(events, e => e.EventType == GitHubEventTypes.IssueCommentCreated);
        Assert.Single(events, e => e.EventType == GitHubEventTypes.ReviewCommentCreated);
        Assert.All(events, e => Assert.Equal(GitHubEventSources.RestResourceSync, e.Source));
        Assert.Equal(CommentT0Timestamp,
            TimestampCursor.TryParse((await CursorAsync(db, repo, SyncCursorStreams.IssueComments)).CursorValue)!.Position);
    }

    private static readonly DateTime CommentT0Timestamp = new(2026, 11, 19, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Edit_AppliesNewBody_AndEmitsUpdatedEvent() // task checklist row 2
    {
        var (db, repo) = await NewDbAndRepoAsync();
        PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue);

        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueCommentJson(999, 12, "v1", CommentT0)));
        await new IssueCommentsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueCommentJson(999, 12, "v2 - edited", CommentT1)));
        await new IssueCommentsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        var row = await db.GitHubComments.AsNoTracking().SingleAsync(c => c.GithubCommentId == 999);
        Assert.Equal("v2 - edited", row.Body); // body is always the latest synced version
        Assert.Equal(new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc), row.UpdatedAtGithub);
        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Single(events, e => e.EventType == GitHubEventTypes.IssueCommentCreated);
        Assert.Single(events, e => e.EventType == GitHubEventTypes.IssueCommentUpdated);
    }

    [Fact]
    public async Task CommentForUnknownWorkItem_HoldsCursorBack() // Review Focus 1
    {
        var (db, repo) = await NewDbAndRepoAsync();
        PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue); // issue 55 unknown

        // newest comment (999 → unknown issue 55) must pin the cursor; the older one still syncs
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.IssueCommentJson(999, 55, "who are you", CommentT1),
            PollerTestSupport.IssueCommentJson(998, 12, "hello", CommentT0)));
        await new IssueCommentsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);

        Assert.Single(await db.GitHubComments.AsNoTracking().ToListAsync(), c => c.GithubCommentId == 998);
        var cursor = await CursorAsync(db, repo, SyncCursorStreams.IssueComments);
        Assert.Equal(new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc),
            TimestampCursor.TryParse(cursor.CursorValue)!.Position); // pinned at the boundary, not past it

        // the unknown issue arrives; the same window redelivers both comments
        PollerTestSupport.SeedWorkItem(db, repo, 55, WorkItemTypes.Issue, updatedAt: "2026-11-19T11:00:00Z");
        var handler2 = new PollerTestSupport.StubHandler();
        handler2.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.IssueCommentJson(999, 55, "who are you", CommentT1),
            PollerTestSupport.IssueCommentJson(998, 12, "hello", CommentT0)));
        await new IssueCommentsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler2), CancellationToken.None);

        Assert.Equal(2, (await db.GitHubComments.AsNoTracking().ToListAsync()).Count);
        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count); // created for 998 and 999 — no duplicates
        Assert.All(events, e => Assert.Equal(GitHubEventTypes.IssueCommentCreated, e.EventType));
    }

    [Fact]
    public async Task Repoll_SameData_NoDuplicates()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        PollerTestSupport.SeedWorkItem(db, repo, 12, WorkItemTypes.Issue);
        PollerTestSupport.SeedWorkItem(db, repo, 7, WorkItemTypes.PullRequest);

        for (var pass = 0; pass < 2; pass++)
        {
            var handler = new PollerTestSupport.StubHandler();
            handler.Enqueue(PollerTestSupport.ArrayJson(
                PollerTestSupport.IssueCommentJson(999, 12, "conversation", CommentT0)));
            handler.Enqueue(PollerTestSupport.ArrayJson(
                PollerTestSupport.ReviewCommentJson(555, 7, "inline nit", CommentT0)));
            await new IssueCommentsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);
            await new PrReviewCommentsStream().SyncAsync(PollerTestSupport.NewRun(db, repo, handler), CancellationToken.None);
        }

        Assert.Equal(2, (await db.GitHubComments.AsNoTracking().ToListAsync()).Count);
        var events = await db.GitHubEvents.AsNoTracking().ToListAsync();
        Assert.Equal(2, events.Count); // created events only
        Assert.All(events, e => e.EventType.EndsWith("created", StringComparison.Ordinal));
    }
}
