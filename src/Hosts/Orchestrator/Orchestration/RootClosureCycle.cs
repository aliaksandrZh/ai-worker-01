using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>Consumes normalized close events. Closure is root-scoped: related Issue/PR links never
/// participate in this operation. Database history is retained; execution storage is delegated to Runtime API.</summary>
public sealed class RootClosureCycle(IServiceScopeFactory scopeFactory, RuntimeApiClient runtimeApi) : IWorkerCycle
{
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var events = await db.GitHubEvents
            .Where(e => e.EventType == GitHubEventTypes.WorkItemClosed
                && e.ProcessingStatus == GitHubEventStatuses.Pending
                && e.WorkItemId != null)
            .OrderBy(e => e.ReceivedAt)
            .Take(32)
            .ToListAsync(cancellationToken);

        foreach (var githubEvent in events)
        {
            githubEvent.ProcessingStatus = GitHubEventStatuses.Processing;
            await db.SaveChangesAsync(cancellationToken);
            try
            {
                await CloseRootAsync(db, githubEvent.WorkItemId!.Value, cancellationToken);
                githubEvent.ProcessingStatus = GitHubEventStatuses.Processed;
                githubEvent.ProcessedAt = DateTime.UtcNow;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Cleanup is retryable: retain the event as pending rather than losing a root when
                // Runtime API is temporarily unavailable.
                githubEvent.ProcessingStatus = GitHubEventStatuses.Pending;
                githubEvent.ProcessingError = exception.ToString()[..Math.Min(exception.ToString().Length, 2000)];
            }
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task CloseRootAsync(AppDbContext db, Guid workItemId, CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.SingleOrDefaultAsync(w => w.WorkItemId == workItemId, cancellationToken);
        if (workflow is null)
            return; // a closed item with no accepted command has no Workflow/storage to clean
        var workItem = await db.WorkItems.SingleAsync(w => w.Id == workItemId, cancellationToken);
        if (workflow.Status == WorkflowStatuses.Closed && workItem.CleanupStatus == "completed")
            return; // reopen/duplicate close is deliberately idempotent

        var now = DateTime.UtcNow;
        workItem.CleanupStatus = "pending";
        workflow.Status = WorkflowStatuses.Closed;
        workflow.ClosedAt ??= now;
        workflow.UpdatedAt = now;

        var tasks = await db.OrchestrationTasks
            .Where(t => t.WorkflowId == workflow.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status))
            .ToListAsync(cancellationToken);
        foreach (var task in tasks)
        {
            task.Status = OrchestrationTaskStatuses.Cancelled;
            task.CancelledAt = now;
            task.FailureReason = AgentCommandRejectionReasons.WorkItemClosed;
            task.CompletedAt = now;
            await db.OrchestrationJobs.Where(j => j.TaskId == task.Id && j.Status != OrchestrationJobStatuses.Completed)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(j => j.Status, OrchestrationJobStatuses.Cancelled)
                    .SetProperty(j => j.UpdatedAt, now), cancellationToken);
            if (task.TriggerCommandId is { } commandId)
            {
                var command = await db.AgentCommands.SingleAsync(c => c.Id == commandId, cancellationToken);
                command.Status = AgentCommandStatuses.Failed;
                command.RejectionReason = AgentCommandRejectionReasons.WorkItemClosed;
                command.ProcessedAt = now;
            }
        }

        var activeRuns = await db.Runs
            .Where(r => db.OrchestrationTasks.Any(t => t.Id == r.TaskId && t.WorkflowId == workflow.Id)
                && new[] { RunStatuses.Pending, RunStatuses.Starting, RunStatuses.Running, RunStatuses.Stopping }.Contains(r.Status))
            .Select(r => r.Id)
            .ToListAsync(cancellationToken);
        foreach (var runId in activeRuns)
        {
            using var response = await runtimeApi.StopRunAsync(runId, AgentCommandRejectionReasons.WorkItemClosed, cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        // root closure is final: accepted stop requests conclude the Runs immediately (no Bridge
        // callback can follow a removed deployment); the sweeper backstop is unnecessary here.
        if (activeRuns.Count > 0)
            await db.Runs.Where(r => activeRuns.Contains(r.Id) && r.Status != RunStatuses.Cancelled)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, RunStatuses.Cancelled)
                    .SetProperty(r => r.CompletedAt, now)
                    .SetProperty(r => r.ErrorCode, AgentCommandRejectionReasons.WorkItemClosed), cancellationToken);

        workflow.CurrentTaskId = null;
        await db.SaveChangesAsync(cancellationToken);
        // root closure is not a failure either: the AI labels go away with the workflow
        await AiStateLabels.ClearAsync(db, workItem.RepositoryId, workItem.Id, workItem.Number,
            null, cancellationToken);
        using var cleanup = await runtimeApi.CleanupWorkflowAsync(workflow.Id, cancellationToken);
        cleanup.EnsureSuccessStatusCode();
        workItem.CleanupStatus = "completed";
        await db.SaveChangesAsync(cancellationToken);
    }
}
