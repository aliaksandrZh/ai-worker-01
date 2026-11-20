using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;
using PiAgent.Platform.Http;

namespace PiAgent.Poller;

/// <summary>
/// One IWorkerCycle tick (decision 4/53): every enabled-or-paused repository (decision 75 — pause stops
/// task-producing commands, never polling) is polled when its in-memory next-due gate has elapsed,
/// streams in fixed order `issues → pulls → pr_reviews → issue_comments → pr_review_comments →
/// timeline → deleted_comment_sweep` (the last runs on its own daily last-success gate, decision 77). A failing
/// stream records its own cursor error and the remaining streams still run; a rate-limited one aborts the
/// repository AND the whole cycle (nothing else attempted, cursors untouched) with an in-memory pause
/// honoring Retry-After/X-RateLimit-Reset — primary limits — or bounded exponential backoff for 429s
/// without guidance. Events are written `pending`; the cycle never processes them.
/// </summary>
public sealed class PollerCycle(
    IServiceScopeFactory scopeFactory,
    GitHubReadClient client,
    IEnumerable<IRepositoryStream> streams,
    IOptions<PollerOptions> options,
    PollerState state,
    ILogger<PollerCycle> logger) : IWorkerCycle
{
    private static readonly string[] Order =
    [
        SyncCursorStreams.Issues,
        SyncCursorStreams.Pulls,
        SyncCursorStreams.PrReviews,
        SyncCursorStreams.IssueComments,
        SyncCursorStreams.PrReviewComments,
        SyncCursorStreams.Timeline,
        SyncCursorStreams.DeletedCommentSweep,
    ];

    private static readonly TimeSpan MaxPause = TimeSpan.FromHours(1);
    private static readonly TimeSpan SecondaryBase = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan SecondaryCap = TimeSpan.FromMinutes(5);

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (state.PauseUntil is { } pause && pause > DateTimeOffset.UtcNow)
        {
            logger.LogDebug("poller paused until {until} (rate limit)", pause);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var rateLimited = false;
        var anySuccess = false;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repositories = await db.Repositories.AsNoTracking()
                .OrderBy(r => r.FullName)
                .ToListAsync(cancellationToken);

            foreach (var repository in repositories)
            {
                // in-memory poll gate: skipped until due; set on every attempt regardless of outcome
                if (state.NextDue.GetOrAdd(repository.FullName, DateTimeOffset.MinValue) > DateTimeOffset.UtcNow)
                    continue;
                state.NextDue[repository.FullName] = DateTimeOffset.UtcNow + options.Value.Interval;

                foreach (var stream in streams.OrderBy(s => Array.IndexOf(Order, s.StreamName)))
                {
                    try
                    {
                        var result = await stream.SyncAsync(new StreamRun(db, repository, client, options.Value), cancellationToken);
                        if (result.Kind == StreamResultKind.Failed)
                        {
                            state.AddError();
                            logger.LogWarning("poller stream failed: {repo}/{stream}: {error}",
                                repository.FullName, stream.StreamName, result.Error);
                        }
                        else
                        {
                            anySuccess = true;
                            logger.LogDebug("poller {repo}/{stream} {kind}", repository.FullName, stream.StreamName, result.Kind);
                        }
                    }
                    catch (GitHubRateLimitedException exception)
                    {
                        rateLimited = true;
                        state.AddError();
                        state.RateLimitRemaining = exception.Remaining;
                        var delay = PauseDelay(exception, state.ConsecutiveRateLimited++);
                        state.PauseUntil = DateTimeOffset.UtcNow + delay;
                        logger.LogWarning("poller rate limited: {error} — cycle paused for {delay}", exception.Message, delay);
                        return; // aborts this repository AND the whole cycle; cursors stay untouched
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        state.AddError();
                        logger.LogWarning(exception, "poller stream crashed: {repo}/{stream}",
                            repository.FullName, stream.StreamName);
                    }
                }
            }

            if (anySuccess && !rateLimited)
            {
                state.LastSuccessUtc = DateTime.UtcNow;
                state.ConsecutiveRateLimited = 0;
            }
        }
        finally
        {
            state.LastCycleDuration = stopwatch.Elapsed;
        }
    }

    /// <summary>Pause sizing (decision 53): honor Retry-After / X-RateLimit-Reset for primary limits
    /// (up to 1 h so the cycle stays responsive); bounded exponential backoff (15 s ×2ⁿ, 5-min cap)
    /// for guidance-less 429s (secondary limits). Pure policy — public for tests.</summary>
    public static TimeSpan PauseDelay(GitHubRateLimitedException exception, int consecutiveRateLimited)
    {
        var now = DateTimeOffset.UtcNow;
        if (exception.RetryAfterSeconds is { } retryAfter)
            return TimeSpan.FromSeconds(Math.Min(retryAfter, MaxPause.TotalSeconds));
        if (exception.ResetAt is { } reset && reset > now)
            return TimeSpan.FromSeconds(Math.Min((reset - now).TotalSeconds, MaxPause.TotalSeconds));
        var backoff = TimeSpan.FromSeconds(SecondaryBase.TotalSeconds * Math.Pow(2, Math.Min(consecutiveRateLimited, 10)));
        return backoff > SecondaryCap ? SecondaryCap : backoff;
    }
}
