using Microsoft.EntityFrameworkCore;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Context;

using PiAgent.Infrastructure.Persistence;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The claim-time authorization recheck (02 §21, decision 66): a queued Task revalidates the
/// collaborator permission (fresh or ≤60 s cached) when claimed — permissions can change between
/// admission and execution. System-created Tasks (no command, e.g. ticket 12's automatic reviews) and
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
/// Claim-time task cancellation (04 §2, 06 §12): a queued Task that failed its recheck turns terminal
/// — Task `cancelled` with the recorded reason, Job `cancelled`, command `failed` + reason, Workflow
/// back to `waiting_for_human`, never-dispatched (`pending`) Run rows deleted, and one Outbox notice
/// per the 06 §13 table. Reused by tickets 09/13/15 via the same recorded-reason contract.
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
        metrics.RecordClaimCancellation();
    }

    /// <summary>
    /// Claim-time task failure because the mandatory context cannot fit (ticket 11, decision 63): the
    /// Context Assembler ran BEFORE any Run row existed — nothing to delete (the guard deletes stray
    /// never-dispatched Run rows anyway) — and no snapshot was written (snapshots exist only per
    /// created Run). Task `failed` (final: context_too_large is not a transient infra error), job
    /// `failed` + detail, command `failed` + reason, Workflow back to `waiting_for_human`.
    /// The final-failure Outbox notification stays deferred to ticket 15's retry/notification semantics
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
        metrics.RecordClaimContextTooLarge();
    }
}
