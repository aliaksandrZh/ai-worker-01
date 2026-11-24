using Microsoft.EntityFrameworkCore;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Context;

using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The claim-time authorization recheck: a queued Task revalidates the
/// collaborator permission (fresh or ≤60 s cached) when claimed — permissions can change between
/// admission and execution. System-created Tasks (no command, e.g. automatic reviews) and
/// tasks without a recorded author are not rechecked here.
/// </summary>
public sealed record ClaimPermissionOutcome(bool Denied, string? RejectionReason, bool Transient, bool SystemConfigError = false);

public static class ClaimPermissionCheck
{
    public static async Task<ClaimPermissionOutcome?> RecheckAsync(
        AppDbContext db,
        CollaboratorPermissionGate gate,
        Repository repository,
        AgentCommand? command,
        CancellationToken cancellationToken)
    {
        if (command?.AuthorLogin is null)
            return null;

        var config = RepoCommandConfigParser.Parse(repository);

        var allowlist = CollaboratorPermissionGate.EvaluateAllowlist(repository.Configuration, command.AuthorLogin);
        if (allowlist == AllowlistVerdict.Denied)
            return new ClaimPermissionOutcome(Denied: true, AgentCommandRejectionReasons.NotInAllowlist, Transient: false);

        var outcome = await gate.CheckAsync(
            repository.Owner, repository.Name, command.AuthorLogin, config.MinimumPermission, cancellationToken);
        return outcome.Kind switch
        {
            PermissionKind.Allowed => null,
            PermissionKind.UserNotFound =>
                new ClaimPermissionOutcome(Denied: true, AgentCommandRejectionReasons.UserNotCollaborator, Transient: false),
            PermissionKind.InsufficientPermission =>
                new ClaimPermissionOutcome(Denied: true, AgentCommandRejectionReasons.InsufficientPermission, Transient: false),
            PermissionKind.SystemConfigError =>
                new ClaimPermissionOutcome(Denied: false, RejectionReason: null, Transient: true, SystemConfigError: true),
            _ => new ClaimPermissionOutcome(Denied: false, RejectionReason: null, Transient: true),
        };
    }
}

/// <summary>
/// Claim-time task cancellation: a queued Task that failed its recheck turns terminal
/// — Task `cancelled` with the recorded reason, Job `cancelled`, command `failed` + reason, Workflow
/// back to `waiting_for_human`, never-dispatched (`pending`) Run rows deleted, and one Outbox notice
/// per the failure-notice table. Reused by the reconciliation/retry paths via the same recorded-reason contract.
/// </summary>
public static class TaskLifecycle
{
    public static async Task CancelTaskAtClaimAsync(
        AppDbContext db,
        OrchestrationTask task,
        OrchestrationJob job,
        Repository repository,
        WorkItem workItem,
        string reason,
        JobQueueMetrics metrics,
        CancellationToken cancellationToken)
    {
        // /stop may have cancelled this claimed Task while its claim was in flight — a fresh
        // read makes the racing cancellation a no-op (no spurious notice, no resurrected rows).
        var currentStatus = await db.OrchestrationTasks.AsNoTracking()
            .Where(t => t.Id == task.Id)
            .Select(t => t.Status)
            .SingleAsync(cancellationToken);
        if (!OrchestrationTaskStatuses.Unfinished.Contains(currentStatus))
            return;

        var now = DateTime.UtcNow;
        await db.Runs
            .Where(r => r.TaskId == task.Id && r.Status == RunStatuses.Pending)
            .ExecuteDeleteAsync(cancellationToken); // DB cascades the session + container rows

        task.Status = OrchestrationTaskStatuses.Cancelled;
        task.FailureReason = reason;
        task.CancelledAt = now;
        job.Status = OrchestrationJobStatuses.Cancelled;
        job.UpdatedAt = now;

        if (task.TriggerCommandId is { } commandId)
        {
            var command = await db.AgentCommands.SingleAsync(c => c.Id == commandId, cancellationToken);
            command.Status = AgentCommandStatuses.Failed;
            command.RejectionReason = reason;
            command.ProcessedAt = now;
        }

        var workflow = await db.Workflows.SingleAsync(w => w.Id == task.WorkflowId, cancellationToken);
        workflow.CurrentTaskId = null;
        workflow.UpdatedAt = now;
        if (workflow.Status == WorkflowStatuses.Active)
            workflow.Status = WorkflowStatuses.WaitingForHuman;

        await db.SaveChangesAsync(cancellationToken);
        await new OutboxActionStore(db).EnqueueAsync(
            new OutboxEnqueueRequest(repository.Id, workItem.Id, null, workItem.Number,
                ClaimCancellationNotices.Text(reason), Guid.NewGuid()), cancellationToken);
        // the queued task finally died — the AI-state labels must not hang on a stale value
        // (the observed hang: AI Scheduled long after the task was cancelled at claim).
        // Ownership of the label state stays with the paths that mean it: a stop (StopRequested)
        // and a root closure (WorkItemClosed) are NOT failures and clear the labels themselves;
        // every other claim-time cancellation is the work ending unsuccessfully → AI Failed
        // (the cancellation notice comment above explains why, /retry or re-run follows).
        if (reason is not (AgentCommandRejectionReasons.StopRequested or AgentCommandRejectionReasons.WorkItemClosed))
            await AiStateLabels.MarkAsync(db, repository.Id, workItem.Id, workItem.Number,
                task.Id, AiStateLabels.Failed, cancellationToken);
        metrics.RecordClaimCancellation();
    }

    /// <summary>
    /// Claim-time task failure because the mandatory context cannot fit: the
    /// Context Assembler ran BEFORE any Run row existed — nothing to delete (the guard deletes stray
    /// never-dispatched Run rows anyway) — and no snapshot was written (snapshots exist only per
    /// created Run). Task `failed` (final: context_too_large is not a transient infra error), job
    /// `failed` + detail, command `failed` + reason, Workflow back to `waiting_for_human`.
    /// The final-failure Outbox notification stays deferred to the retry/notification semantics
    /// (same deliberate deferral as RunTerminalConsumer's run-side failures — recorded, not yet posted).
    /// </summary>
    public static async Task FailTaskContextTooLargeAsync(
        AppDbContext db,
        OrchestrationTask task,
        OrchestrationJob job,
        Repository repository,
        WorkItem workItem,
        string detail,
        JobQueueMetrics metrics,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await db.Runs
            .Where(r => r.TaskId == task.Id && r.Status == RunStatuses.Pending)
            .ExecuteDeleteAsync(cancellationToken); // safety: assembly precedes RunPrepare — none exist

        task.Status = OrchestrationTaskStatuses.Failed;
        task.FailureReason = ContextAssemblyResult.FailureCode;
        task.CompletedAt = now;
        job.Status = OrchestrationJobStatuses.Failed;
        job.LastError = detail.Length > 2000 ? detail[..2000] : detail;
        job.UpdatedAt = now;

        if (task.TriggerCommandId is { } commandId)
        {
            var command = await db.AgentCommands.SingleAsync(c => c.Id == commandId, cancellationToken);
            command.Status = AgentCommandStatuses.Failed;
            command.RejectionReason = ContextAssemblyResult.FailureCode;
            command.ProcessedAt = now;
        }

        var workflow = await db.Workflows.SingleAsync(w => w.Id == task.WorkflowId, cancellationToken);
        workflow.CurrentTaskId = null;
        workflow.UpdatedAt = now;
        if (workflow.Status == WorkflowStatuses.Active)
            workflow.Status = WorkflowStatuses.WaitingForHuman;

        await db.SaveChangesAsync(cancellationToken);
        // final claim-time failure (context_too_large never retries) — the AI-state labels
        // follow the same rule as every final failure: AI Failed
        await AiStateLabels.MarkAsync(db, repository.Id, workItem.Id, workItem.Number,
            task.Id, AiStateLabels.Failed, cancellationToken);
        metrics.RecordClaimContextTooLarge();
    }
}
