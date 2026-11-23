using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// Consumes the Poller's `project_item_*` events. Current scope is observational ("sync + logs"):
/// every pending project event is claimed, logged with its payload, and settled `processed` — no
/// orchestration state is touched. When project-driven commands arrive later, this cycle is the
/// single dispatch point to replace the log line with.
/// </summary>
public sealed class ProjectEventLogCycle(
    IServiceScopeFactory scopeFactory,
    ILogger<ProjectEventLogCycle> logger) : IWorkerCycle
{
    private static readonly string[] Handled =
    [
        GitHubEventTypes.ProjectItemAdded,
        GitHubEventTypes.ProjectItemUpdated,
        GitHubEventTypes.ProjectItemRemoved,
    ];

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var events = await db.GitHubEvents
            .Where(e => Handled.Contains(e.EventType)
                && e.ProcessingStatus == GitHubEventStatuses.Pending)
            .OrderBy(e => e.ReceivedAt)
            .Take(32)
            .ToListAsync(cancellationToken);

        foreach (var githubEvent in events)
        {
            githubEvent.ProcessingStatus = GitHubEventStatuses.Processing;
            await db.SaveChangesAsync(cancellationToken);
            try
            {
                logger.LogInformation("processing {eventType} ({source}, dedupe {dedupe}): {payload}",
                    githubEvent.EventType, githubEvent.Source, githubEvent.DedupeKey, githubEvent.Payload);
                githubEvent.ProcessingStatus = GitHubEventStatuses.Processed;
                githubEvent.ProcessedAt = DateTime.UtcNow;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                githubEvent.ProcessingStatus = GitHubEventStatuses.Pending;
                githubEvent.ProcessingError = exception.ToString()[..Math.Min(exception.ToString().Length, 2000)];
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
