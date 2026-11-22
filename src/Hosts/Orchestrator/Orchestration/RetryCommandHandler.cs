using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The `/retry` command path (06 §9a, decisions 27/65/70 — ticket 15): re-runs the last failed Task of
/// the WorkItem's Workflow WITHOUT creating a new Task — the same pinned `AgentProfileVersion` (and,
/// for reviews, the same pinned `target_head_sha`), a new OrchestrationJob in the same transaction,
/// then the normal claim flow (`attempt_number + 1`, fresh AgentSession, reused input artifacts and
/// Workspace). Preconditions: open WorkItem, Workflow not failed/closed, no unfinished Task, most
/// recent non-cancelled Task `failed`, pinned profile version enabled and `ready`. Per-task-type
/// conditions (plan input for implement tasks, pinned review SHA equality, permissions) stay with the
/// claim-time rechecks — owner decision 2026-11-22: no bespoke retry admission gates per command type.
/// Rejections record the reason on the retry command, enqueue exactly one Outbox reply, and leave the
/// failed Task `Failed` (decision 70). An accepted retry publishes nothing (06 §13 has no row for it);
/// the task's eventual outcome (result comment / final-failure notification) is the user's feedback.
/// </summary>
public static class RetryCommandHandler
{
    public static async Task HandleAsync(
        AppDbContext db,
        AgentCommand command,
        WorkItem workItem,
        Repository repository,
        CommandMetrics metrics,
        CancellationToken cancellationToken)
    {
        var workflow = await db.Workflows
            .SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id, cancellationToken);

        if (workflow is null)
        {
            await RejectAsync(db, command, workItem, repository,
                (AgentCommandRejectionReasons.TaskNotRetryable,
                    "no agent workflow has been started for this work item"), metrics, cancellationToken);
            return;
        }
        if (workflow.Status == WorkflowStatuses.Failed)
        {
            await RejectAsync(db, command, workItem, repository,
                (AgentCommandRejectionReasons.WorkflowFailed,
                    "the workflow has failed unrecoverably (decision 71)"), metrics, cancellationToken);
            return;
        }
        if (workflow.Status == WorkflowStatuses.Closed || workItem.State != "open")
        {
            await RejectAsync(db, command, workItem, repository,
                (AgentCommandRejectionReasons.WorkItemClosed, "the work item or its workflow is closed"),
                metrics, cancellationToken);
            return;
        }
        if (await db.OrchestrationTasks.AnyAsync(
                t => t.WorkflowId == workflow.Id && OrchestrationTaskStatuses.Unfinished.Contains(t.Status),
                cancellationToken))
        {
            // occupancy (06 §12): same-Workflow commands are not queued behind one another
            await RejectAsync(db, command, workItem, repository,
                (AgentCommandRejectionReasons.UnfinishedTaskExists,
                    "the workflow already holds a queued or running Task"), metrics, cancellationToken);
            return;
        }

        // the retry target: most recent non-cancelled Task (cancelled ones are skipped, 06 §9a); tracked,
        // so the Failed→Queued transition below rides the caller's transaction
        var last = await db.OrchestrationTasks
            .Where(t => t.WorkflowId == workflow.Id && t.Status != OrchestrationTaskStatuses.Cancelled)
            .OrderByDescending(t => t.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (last is null || last.Status != OrchestrationTaskStatuses.Failed)
        {
            await RejectAsync(db, command, workItem, repository,
                (AgentCommandRejectionReasons.TaskNotRetryable,
                    "/retry repeats the work item's most recent non-cancelled task, which must have failed"),
                metrics, cancellationToken);
            return;
        }

        var version = await db.AgentProfileVersions.AsNoTracking()
            .SingleAsync(v => v.Id == last.AgentProfileVersionId, cancellationToken);
        var profile = await db.AgentProfiles.AsNoTracking()
            .SingleAsync(p => p.Id == version.AgentProfileId, cancellationToken);
        if (!profile.IsEnabled || version.State != ProfileVersionStates.Ready)
        {
            await RejectAsync(db, command, workItem, repository,
                (AgentCommandRejectionReasons.AgentProfileNotReady,
                    "the failed task's agent profile version is disabled or its image is not ready"),
                metrics, cancellationToken);
            return;
        }

        // accepted (decision 70, one transaction): Task Failed → Queued, fresh job available now; the
        // workflow re-enters `active` with the retried Task as its open work (03 §1). No notification.
        var now = DateTime.UtcNow;
        last.Status = OrchestrationTaskStatuses.Queued;
        last.CompletedAt = null;
        last.FailureReason = null;
        db.OrchestrationJobs.Add(new OrchestrationJob
        {
            Id = Guid.NewGuid(),
            TaskId = last.Id,
            JobType = OrchestrationJobTypes.RunTask,
            Status = OrchestrationJobStatuses.Pending,
            Priority = 0,
            Attempts = 0,
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        workflow.Status = WorkflowStatuses.Active;
        workflow.CurrentTaskId = last.Id;
        workflow.UpdatedAt = now;
        command.Status = AgentCommandStatuses.Processed;
        command.ProcessedAt = now;
    }

    /// <summary>Rejection on the /retry path: the failed Task stays `Failed` (decision 70), the command
    /// records the reason, one Outbox reply (06 §13 "Command rejected"). A stale /retry racing an
    /// automatic retry's silent requeue of the same Task is the caller's concern only via the benign
    /// double-job edge: the claim worker fails a job whose Task is no longer queued.</summary>
    private static async Task RejectAsync(
        AppDbContext db,
        AgentCommand command,
        WorkItem workItem,
        Repository repository,
        (string Reason, string Detail) rejection,
        CommandMetrics metrics,
        CancellationToken cancellationToken)
    {
        command.Status = AgentCommandStatuses.Rejected;
        command.RejectionReason = rejection.Reason;
        command.ProcessedAt = DateTime.UtcNow;
        metrics.RecordRejected(rejection.Reason);
        await new OutboxActionStore(db).EnqueueAsync(
            new OutboxEnqueueRequest(repository.Id, workItem.Id, null, workItem.Number,
                AgentCommandRejectionReplies.Text(rejection.Reason, rejection.Detail), Guid.NewGuid()),
            cancellationToken);
    }
}
