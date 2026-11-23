using PiAgent.Poller.GitHub;
using PiAgent.Poller.Streams;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;
using PiAgent.Platform.Http;

namespace PiAgent.Poller;

/// <summary>
/// One IWorkerCycle unit: the Projects v2 pass — every configured project
/// (deploy/config/projects.yaml) is listed when its in-memory next-due gate has elapsed (the same
/// interval as the repository streams). Honors the shared rate-limit pause: a pause set by this or
/// the repository-streams cycle (registered first) skips the pass; a rate limit inside the pass
/// sets the same pause. A failing project records its own project_sync error (last_attempt/
/// last_error) and never touches the other projects. Events are written `pending`; processing
/// lives in the Orchestrator's event cycles.
/// </summary>
public sealed class ProjectItemsCycle(
    IServiceScopeFactory scopeFactory,
    GitHubReadClient client,
    IOptions<ProjectSyncOptions> options,
    IOptions<PollerOptions> pollerOptions,
    PollerState state,
    ILogger<ProjectItemsCycle> logger) : IWorkerCycle
{
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Projects.Count == 0)
            return;
        if (state.PauseUntil is { } pause && pause > DateTimeOffset.UtcNow)
        {
            logger.LogDebug("projects pass paused until {until} (rate limit)", pause);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        foreach (var project in options.Value.Projects)
        {
            var board = $"project:{project.Owner}/{project.Number}";
            if (state.NextDue.GetOrAdd(board, DateTimeOffset.MinValue) > DateTimeOffset.UtcNow)
                continue;
            state.NextDue[board] = DateTimeOffset.UtcNow + pollerOptions.Value.Interval;

            try
            {
                var result = await ProjectItemsSync.SyncAsync(db, client, project, logger, cancellationToken);
                if (result.Kind == StreamResultKind.Failed)
                {
                    state.AddError();
                    logger.LogWarning("projects pass failed: {board}: {error}", board, result.Error);
                }
                else
                {
                    logger.LogDebug("projects pass {board} {kind}", board, result.Kind);
                }
            }
            catch (GitHubRateLimitedException exception)
            {
                state.RateLimitRemaining = exception.Remaining;
                var delay = PollerCycle.PauseDelay(exception, state.ConsecutiveRateLimited++);
                state.PauseUntil = DateTimeOffset.UtcNow + delay;
                logger.LogWarning("poller rate limited: {error} — cycle paused for {delay}", exception.Message, delay);
                return; // aborts the pass and the cycle; nothing advanced
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                state.AddError();
                logger.LogWarning(exception, "projects pass crashed: {board}", board);
            }
        }
    }
}
