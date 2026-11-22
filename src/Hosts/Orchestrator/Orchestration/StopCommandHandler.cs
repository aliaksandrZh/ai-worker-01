using System.Net;
using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The `/stop` control path (06 §8, decisions 29/38/61 — handled immediately, outside the queue).
/// Finds the WorkItem's Workflow and its unfinished Tasks:
/// <list type="bullet">
/// <item>queued/claimed Tasks are cancelled NOW with their Jobs — Task `cancelled` (reason
/// `stop_requested`), Job `cancelled`, trigger command `failed`, never-dispatched `pending` Runs
/// deleted. Cancellation writes are guarded (`WHERE status IN (queued, claimed)`) so a claim that
/// slipped in between the snapshot and the write is never clobbered: the guard miss reclassifies the
/// Task into the active branch instead.</item>
/// <item>starting/running Tasks are cancelled by the graceful runtime stop sequence: the Orchestrator
/// signals the Runtime API (Run → `stopping`) and the Bridge posts the terminal `cancelled` callback;
/// the terminal-Run consumer then marks Run/Task `cancelled` — "after runtime confirmation or
/// reconciliation" (the Runtime API's stop-confirm sweeper is the reconciliation leg). The Workflow
/// stays `active` until that confirmation; nothing is pre-emptively torn down.</item>
/// </list>
/// Exactly one Outbox notification covers active+queued cancellations (decision 29; one comment may
/// cover both), keyed by the `/stop` command row id so replays stay single. No unfinished work → the
/// "nothing to stop" reply (06 §8.8): no row changes at all, Workflow and storage untouched. Storage
/// is never deleted; other Workflows and the WorkItem are never closed.
/// </summary>
public static class StopCommandHandler
{
    public static async Task HandleAsync(
        AppDbContext db,
        AgentCommand command,
        WorkItem workItem,
        Repository repository,
        RuntimeApiClient runtimeApi,
        CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows.AsNoTracking()
            .SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id, cancellationToken);

        // Unfinished-task ids as a bounded claim-free snapshot; all writes below are fresh/guarded.
        var unfinishedIds = workflow is null
            ? []
            : await db.OrchestrationTasks.AsNoTracking()
                .Where(t => t.WorkflowId == workflow.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status))
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

        if (unfinishedIds.Count == 0)
        {
            // 06 §8.8: nothing to stop — one reply, nothing else written (no Workflow, no storage change);
            // the /stop command id is the notification's idempotency key.
            await new OutboxActionStore(db).EnqueueAsync(
                new OutboxEnqueueRequest(repository.Id, workItem.Id, null, workItem.Number,
                    StopReplies.NothingToStop(workItem), command.Id),
                cancellationToken);
            command.Status = AgentCommandStatuses.Processed;
            command.ProcessedAt = DateTime.UtcNow;
            return;
        }

        var now = DateTime.UtcNow;
        var queuedCancellations = 0;
        var active = new List<(OrchestrationTask Task, Run Run)>();

        foreach (var taskId in unfinishedIds)
        {
            // guarded cancel of pre-start work — a claim that committed between the snapshot and here
            // makes the task `starting`/`running` and the guard (rows == 0) reclassifies it as active
            var cancelledRows = await db.OrchestrationTasks
                .Where(t => t.Id == taskId
                    && (t.Status == OrchestrationTaskStatuses.Queued || t.Status == OrchestrationTaskStatuses.Claimed))
                .ExecuteUpdateAsync(set => set
                    .SetProperty(t => t.Status, OrchestrationTaskStatuses.Cancelled)
                    .SetProperty(t => t.FailureReason, AgentCommandRejectionReasons.StopRequested)
                    .SetProperty(t => t.CancelledAt, now), cancellationToken);
            if (cancelledRows == 1)
            {
                await CancelQueuedWorkAsync(db, taskId, now, cancellationToken);
                queuedCancellations += 1;
                continue;
            }

            var task = await db.OrchestrationTasks.AsNoTracking()
                .SingleAsync(t => t.Id == taskId, cancellationToken);
            if (task.Status is not (OrchestrationTaskStatuses.Starting or OrchestrationTaskStatuses.Running))
                continue; // turned terminal between the snapshot and now — nothing to stop

            // the Run's latest attempt must be non-terminal for the signal to mean anything
            var run = await db.Runs
                .Where(r => r.TaskId == task.Id)
                .OrderByDescending(r => r.AttemptNumber)
                .FirstOrDefaultAsync(cancellationToken);
            if (run is null || run.Status is RunStatuses.Succeeded or RunStatuses.Failed
                or RunStatuses.Cancelled or RunStatuses.TimedOut)
                continue; // finished between the snapshot and now — the terminal consumer already owns it

            // The signal call rides this command's transaction (same precedent as the collaborator
            // permission lookup): a transport failure throws → the event is released → the whole
            // /stop retries idempotently (the signal recording is a one-time no-op on replay).
            var response = await runtimeApi.StopRunAsync(run.Id, AgentCommandRejectionReasons.StopRequested, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"runtime API rejected the stop signal for run {run.Id}: {(int)response.StatusCode} (task {task.Id})");
            active.Add((task, run));
        }

        // Workflow status: `waiting_for_human` only when nothing active was signalled — with an active
        // stop the terminal-Run consumer flips it at runtime confirmation (06 §8.5 "after cancellation").
        // The open-work pointer clears regardless of the current status (a queued-only stop in an
        // already-waiting Workflow loses its CurrentTaskPointer too).
        if (workflow is not null && active.Count == 0)
        {
            await db.Workflows
                .Where(w => w.Id == workflow.Id)
                .ExecuteUpdateAsync(set =>
                {
                    set.SetProperty(w => w.CurrentTaskId, (Guid?)null);
                    set.SetProperty(w => w.UpdatedAt, now);
                    if (workflow.Status == WorkflowStatuses.Active)
                        set.SetProperty(w => w.Status, WorkflowStatuses.WaitingForHuman);
                }, cancellationToken);
        }

        var notification = await StopReplies.CancellationProcessed(
            workItem, queuedCancellations, active, db, cancellationToken);
        await new OutboxActionStore(db).EnqueueAsync(
            new OutboxEnqueueRequest(repository.Id, workItem.Id, null, workItem.Number, notification, command.Id),
            cancellationToken);
        command.Status = AgentCommandStatuses.Processed;
        command.ProcessedAt = DateTime.UtcNow;
    }

    /// <summary>Cancels the rest of a cancelled pre-start Task: Job (pending or the claim's processing),
    /// trigger command, and never-dispatched `pending` Run rows (DB cascades their sessions/containers).</summary>
    private static async Task CancelQueuedWorkAsync(AppDbContext db, Guid taskId, DateTime now, CancellationToken cancellationToken)
    {
        await db.Runs
            .Where(r => r.TaskId == taskId && r.Status == RunStatuses.Pending)
            .ExecuteDeleteAsync(cancellationToken);

        await db.OrchestrationJobs
            .Where(j => j.TaskId == taskId
                && (j.Status == OrchestrationJobStatuses.Pending || j.Status == OrchestrationJobStatuses.Processing))
            .ExecuteUpdateAsync(set => set
                .SetProperty(j => j.Status, OrchestrationJobStatuses.Cancelled)
                .SetProperty(j => j.UpdatedAt, now), cancellationToken);

        var commandId = await db.OrchestrationTasks.AsNoTracking()
            .Where(t => t.Id == taskId)
            .Select(t => t.TriggerCommandId)
            .SingleOrDefaultAsync(cancellationToken);
        if (commandId is { } id)
        {
            var command = await db.AgentCommands.SingleAsync(c => c.Id == id, cancellationToken);
            command.Status = AgentCommandStatuses.Failed;
            command.RejectionReason = AgentCommandRejectionReasons.StopRequested;
            command.ProcessedAt = now;
        }
    }
}

/// <summary>The `/stop` Outbox texts (06 §8.6/§8.8, decision 29): one comment covers active+queued.</summary>
public static class StopReplies
{
    public static string NothingToStop(WorkItem workItem) =>
        $"/stop: nothing to stop — there is no active or queued agent task for this {(workItem.Type == WorkItemTypes.PullRequest ? "pull request" : "issue")}.";

    public static async Task<string> CancellationProcessed(
        WorkItem workItem, int queuedCancellations,
        List<(OrchestrationTask Task, Run Run)> active, AppDbContext db, CancellationToken cancellationToken)
    {
        var lines = new List<string> { "/stop: cancellation processed." };
        foreach (var (task, run) in active)
        {
            var profileName = await db.AgentProfileVersions.AsNoTracking()
                .Where(v => v.Id == task.AgentProfileVersionId)
                .Join(db.AgentProfiles, v => v.AgentProfileId, p => p.Id, (v, p) => p.Name)
                .SingleOrDefaultAsync(cancellationToken);
            lines.Add($"- Active task {TaskToken(task.Type)} (attempt #{run.AttemptNumber}, {profileName ?? "unknown"}): "
                + "graceful stop dispatched — the task is cancelled once the runtime confirms.");
        }
        if (queuedCancellations > 0)
            lines.Add($"- Queued tasks cancelled with their jobs: {queuedCancellations}.");
        lines.Add("Storage (session files, logs, workspace) is retained. Other work was left untouched; this "
            + $"{(workItem.Type == WorkItemTypes.PullRequest ? "pull request" : "issue")} stays open.");
        return string.Join("\n", lines);
    }

    private static string TaskToken(string taskType) => taskType switch
    {
        OrchestrationTaskTypes.Plan => "/plan",
        OrchestrationTaskTypes.Implement => "/code",
        OrchestrationTaskTypes.Review => "/review",
        OrchestrationTaskTypes.TestDesign => "/test-cases",
        _ => $"/{taskType.Replace('_', '-')}",
    };
}
