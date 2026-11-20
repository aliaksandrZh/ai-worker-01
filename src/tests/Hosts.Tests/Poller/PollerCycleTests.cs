using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Poller;
using PiAgent.Platform.Workers;
using Xunit;
using PiAgent.Platform.Http;

namespace PiAgent.Hosts.Tests.Poller;

/// <summary>
/// PollerCycle orchestration over real postgres with a stubbed GitHub (task 04 plan task-8): stream
/// ordering, per-repository polling gate, rate-limit pause without cursor advancement and resume,
/// stream failure locality, decision-75 pause (still polling), and PAT-free logging.
/// </summary>
public class PollerCycleTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Pat = "test-pat-never-log";

    private sealed class CapturingLogger : ILogger<PollerCycle>
    {
        public readonly List<string> Lines = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}");
    }

    private (PollerCycle Cycle, PollerState State, CapturingLogger? Logs) NewCycle(
        PollerTestSupport.StubHandler handler, CapturingLogger? logger = null, PollerState? state = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(optionsBuilder => optionsBuilder
            .UseNpgsql(postgres.ConnectionString)
            .UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();
        var stateValue = state ?? new PollerState();
        var cycle = new PollerCycle(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new GitHubReadClient(new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") }, Pat),
            new IRepositoryStream[]
            {
                new IssuesStream(), new PullRequestsStream(), new PrReviewsStream(),
                new IssueCommentsStream(), new PrReviewCommentsStream(),
            },
            Options.Create(PollerTestSupport.TestOptions),
            stateValue,
            (ILogger<PollerCycle>?)logger ?? NullLogger<PollerCycle>.Instance);
        return (cycle, stateValue, logger);
    }

    private async Task<(AppDbContext Db, Repository Repo)> NewDbAndRepoAsync(bool enabled = true)
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var repo = await PollerTestSupport.SeedRepositoryAsync(db);
        repo.IsEnabled = enabled;
        await db.SaveChangesAsync();
        return (db, repo);
    }

    private Task<SyncCursor> CursorAsync(AppDbContext db, Repository repo, string stream) =>
        db.SyncCursors.SingleAsync(c => c.RepositoryId == repo.Id && c.Stream == stream);

    [Fact]
    public async Task RunsStreamsInOrder_WithFreshScope_AndCreatesRows()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        // stream order: issues → pulls → pr_reviews → issue_comments → pr_review_comments (one repo)
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "issue", "2026-11-19T10:00:00Z")));
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.PullJson(7, "pr", "2026-11-19T10:30:00Z")));
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.ReviewJson(77, 7, GitHubReviewStates.Approved, submittedAt: "2026-11-19T11:00:00Z")));
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.IssueCommentJson(999, 12, "comment", "2026-11-19T11:30:00Z")));
        handler.Enqueue(PollerTestSupport.ArrayJson(
            PollerTestSupport.ReviewCommentJson(555, 7, "inline", "2026-11-19T11:45:00Z")));

        var (cycle, state, _) = NewCycle(handler);
        await cycle.RunCycleAsync(CancellationToken.None);

        var requests = handler.Requests.Select(path => path.Split('?')[0]).ToList();
        Assert.Equal("/repos/octo-org/example/issues", requests[0]);
        Assert.Equal("/repos/octo-org/example/pulls", requests[1]);
        Assert.StartsWith("/repos/octo-org/example/pulls/7/reviews", requests[2]);
        Assert.Equal("/repos/octo-org/example/issues/comments", requests[3]);
        Assert.Equal("/repos/octo-org/example/pulls/comments", requests[4]);

        Assert.Equal(2, (await db.WorkItems.AsNoTracking().ToListAsync()).Count);
        Assert.Equal(2, (await db.GitHubComments.AsNoTracking().ToListAsync()).Count);
        Assert.Single(await db.GitHubReviews.AsNoTracking().ToListAsync());
        Assert.True(await db.GitHubEvents.AsNoTracking().AnyAsync(e => e.EventType == GitHubEventTypes.WorkItemCreated));
        Assert.True(await db.GitHubEvents.AsNoTracking().AnyAsync(e => e.EventType == GitHubEventTypes.PullRequestReviewSubmitted));
        // task 04 runs five streams; the `timeline` cursor arrives with task 05 — not gated here
        foreach (var stream in new[]
                 {
                     SyncCursorStreams.Issues, SyncCursorStreams.Pulls, SyncCursorStreams.PrReviews,
                     SyncCursorStreams.IssueComments, SyncCursorStreams.PrReviewComments,
                 })
        {
            var cursor = await CursorAsync(db, repo, stream);
            Assert.NotNull(cursor.LastSuccessAt);
        }
        Assert.NotNull(state.LastSuccessUtc);
    }

    [Fact]
    public async Task RepoGating_SecondTickWithinInterval_SkipsRepository()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "issue", "2026-11-19T10:00:00Z")));

        var (cycle, state, _) = NewCycle(handler);
        await cycle.RunCycleAsync(CancellationToken.None);
        var workItemsAfterFirst = (await db.WorkItems.AsNoTracking().ToListAsync()).Count;
        var requestCount = handler.Requests.Count;

        // immediate second tick: the in-memory gate (60 s interval) must skip the repository
        await cycle.RunCycleAsync(CancellationToken.None);

        Assert.Equal(requestCount, handler.Requests.Count); // zero HTTP calls
        Assert.Equal(workItemsAfterFirst, (await db.WorkItems.AsNoTracking().ToListAsync()).Count);
    }

    [Fact]
    public async Task RateLimit403_PausesCycle_WithoutAdvancingCursors() // Review Focus 2
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "issue", "2026-11-19T10:00:00Z")));
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.PullJson(7, "pr", "2026-11-19T10:30:00Z")));
        var rateLimit = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            Content = new StringContent("""{"message": "API rate limit exceeded"}"""),
        };
        rateLimit.Headers.Add("X-RateLimit-Remaining", "0");
        rateLimit.Headers.Add("Retry-After", "30");
        handler.EnqueueResponse(rateLimit);

        var (cycle, state, _) = NewCycle(handler);
        await cycle.RunCycleAsync(CancellationToken.None);

        // the reviews stream hit the limit: issue/pull requests happened, nothing after
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(2, handler.Requests.Count(path => path.Contains("/pulls")));
        Assert.Null(await db.SyncCursors.FirstOrDefaultAsync(c => c.Stream == SyncCursorStreams.IssueComments)); // never attempted
        var reviewsCursor = await CursorAsync(db, repo, SyncCursorStreams.PrReviews);
        Assert.Null(reviewsCursor.CursorValue); // rate-limited stream: cursor untouched
        Assert.Null(reviewsCursor.LastError);
        Assert.Equal(0, state.RateLimitRemaining);
        Assert.NotNull(state.PauseUntil);
        Assert.True(state.PauseUntil > DateTimeOffset.UtcNow);
        Assert.True(state.ErrorsTotal >= 1);
    }

    [Fact]
    public async Task Resumes_AfterPauseElapses()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        var (cycle, state, _) = NewCycle(handler);

        // gating: a fresh pause blocks the cycle entirely (no HTTP)
        state.PauseUntil = DateTimeOffset.UtcNow.AddHours(1);
        await cycle.RunCycleAsync(CancellationToken.None);
        Assert.Empty(handler.Requests);

        // pause elapsed → the cycle runs and advances
        state.PauseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "issue", "2026-11-19T10:00:00Z")));
        await cycle.RunCycleAsync(CancellationToken.None);
        Assert.False((await db.WorkItems.AsNoTracking().ToListAsync()).Count == 0, "expected the resumed cycle to sync");
        Assert.Single(handler.Requests, path => path.StartsWith("/repos/octo-org/example/issues?"));
    }

    [Fact]
    public async Task StreamFailure_Recorded_OtherStreamsContinue()
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "issue", "2026-11-19T10:00:00Z")));
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.PullJson(7, "pr", "2026-11-19T10:30:00Z")));
        handler.Enqueue("[]"); // pr_reviews: no open reviews
        handler.Enqueue(HttpStatusCode.NotFound, """{"message": "nope"}""");   // issue_comments 404
        handler.Enqueue("[]"); // pr_review_comments fine

        var (cycle, state, _) = NewCycle(handler);
        await cycle.RunCycleAsync(CancellationToken.None);

        var commentsCursor = await CursorAsync(db, repo, SyncCursorStreams.IssueComments);
        Assert.NotNull(commentsCursor.LastAttemptAt);
        Assert.Contains("404", commentsCursor.LastError);
        Assert.True(state.ErrorsTotal >= 1);
        Assert.Equal(2, (await db.WorkItems.AsNoTracking().ToListAsync()).Count); // earlier streams committed
        var reviewCommentsCursor = await CursorAsync(db, repo, SyncCursorStreams.PrReviewComments);
        Assert.NotNull(reviewCommentsCursor.LastSuccessAt); // the next stream still ran
    }

    [Fact]
    public void PauseDelay_PrimaryLimitsHonored_SecondaryBounded()
    {
        // Retry-After honored (up to 1 h)
        Assert.Equal(TimeSpan.FromSeconds(30), PollerCycle.PauseDelay(
            new GitHubRateLimitedException(HttpStatusCode.Forbidden, 0, 30, null, "x"), 0));
        var resetBased = PollerCycle.PauseDelay(
            new GitHubRateLimitedException(HttpStatusCode.TooManyRequests, 0, null,
                DateTimeOffset.UtcNow.AddMinutes(20), "x"), 0);
        Assert.InRange(resetBased, TimeSpan.FromMinutes(19), TimeSpan.FromHours(1));
        Assert.Equal(TimeSpan.FromHours(1), PollerCycle.PauseDelay(
            new GitHubRateLimitedException(HttpStatusCode.Forbidden, 0, 100000, null, "x"), 0)); // clipped
        // guidance-less 429: bounded exponential 15 s ×2ⁿ capped at 5 min
        Assert.Equal(TimeSpan.FromSeconds(15), PollerCycle.PauseDelay(
            new GitHubRateLimitedException(HttpStatusCode.TooManyRequests, 0, null, null, "x"), 0));
        Assert.Equal(TimeSpan.FromMinutes(5), PollerCycle.PauseDelay(
            new GitHubRateLimitedException(HttpStatusCode.TooManyRequests, 0, null, null, "x"), 9));
    }

    [Fact]
    public async Task PausedRepository_StillPolls() // decision 75
    {
        var (db, repo) = await NewDbAndRepoAsync(enabled: false);
        var handler = new PollerTestSupport.StubHandler();
        handler.Enqueue(PollerTestSupport.ArrayJson(PollerTestSupport.IssueJson(12, "issue", "2026-11-19T10:00:00Z")));

        var (cycle, _, _) = NewCycle(handler);
        await cycle.RunCycleAsync(CancellationToken.None);

        Assert.Single(await db.WorkItems.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RateLimitedCycle_LogsNeverContainPat() // Review Focus 5
    {
        var (db, repo) = await NewDbAndRepoAsync();
        var handler = new PollerTestSupport.StubHandler();
        var rateLimit = new HttpResponseMessage(HttpStatusCode.Forbidden)
        {
            // hostile echo: the PAT comes back in the response body and must never reach a log line
            Content = new StringContent($"{{\"message\": \"bad token {Pat}\"}}"),
        };
        rateLimit.Headers.Add("X-RateLimit-Remaining", "0");
        handler.EnqueueResponse(rateLimit);
        var logger = new CapturingLogger();

        var (cycle, _, _) = NewCycle(handler, logger);
        await cycle.RunCycleAsync(CancellationToken.None);

        Assert.NotEmpty(logger.Lines);
        Assert.All(logger.Lines, line => Assert.DoesNotContain(Pat, line));
    }
}
