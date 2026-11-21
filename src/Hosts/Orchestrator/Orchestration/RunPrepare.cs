using Microsoft.EntityFrameworkCore;

using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Context;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// Run preparation at claim (decision 44, 03 §3/§4): creates the `pending` Run (next attempt number),
/// the one-shot `provisioning` AgentSession (1:1, run_id unique — decision 57) and the
/// `desired=running`/`observed=unknown` ContainerInstance — all inside the caller's change set. On the
/// Task's first attempt it also creates the Workspace row (`provisioning`, 02 §16: reused by retries,
/// never assigned across Tasks).
/// Workflow-scoped serialization (decision 60): refuses to prepare when the Workflow still has a
/// non-terminal Run (`pending|starting|running|stopping`) — the next Run of a Workflow, of any Task
/// type, never starts before the previous Run's terminal callback fixed its state. Returns null when
/// the hook refuses; the claim is then released, a Run is never created for a busy Workflow.
/// The caller assembles the Run's context FIRST (ticket 11: assembly precedes Run creation so that
/// `context_too_large` never leaves Run rows behind) and passes it in; the dispatcher freezes it as the
/// immutable RunContextSnapshot.
/// </summary>
public static class RunPrepare
{
    public static async Task<RunPreparation?> PrepareAsync(
        AppDbContext db, OrchestrationTask task, AgentProfileVersion version, CancellationToken cancellationToken,
        ContextAssembly? assembled = null)
    {
        var activeRuns = await (
            from previous in db.Runs
            join workflowTask in db.OrchestrationTasks on previous.TaskId equals workflowTask.Id
            where workflowTask.WorkflowId == task.WorkflowId
                && (previous.Status == RunStatuses.Pending
                    || previous.Status == RunStatuses.Starting
                    || previous.Status == RunStatuses.Running
                    || previous.Status == RunStatuses.Stopping)
            select previous.Id).CountAsync(cancellationToken);
        if (activeRuns > 0)
            return null;

        var now = DateTime.UtcNow;
        var attemptNumber = ((await db.Runs
            .Where(r => r.TaskId == task.Id)
            .MaxAsync(r => (int?)r.AttemptNumber, cancellationToken)) ?? 0) + 1;

        var run = new Run
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            AttemptNumber = attemptNumber,
            Status = RunStatuses.Pending,
        };
        var session = new AgentSession
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            TaskId = task.Id,
            AgentProfileVersionId = task.AgentProfileVersionId,
            Status = AgentSessionStatuses.Provisioning,
            CreatedAt = now,
        };
        var container = new ContainerInstance
        {
            Id = Guid.NewGuid(),
            AgentSessionId = session.Id,
            RunId = run.Id,
            ContainerImage = version.ContainerImage,
            DesiredState = ContainerDesiredStates.Running,
            ObservedState = ContainerObservedStates.Unknown,
            CreatedAt = now,
        };
        db.Runs.Add(run);
        db.AgentSessions.Add(session);
        db.ContainerInstances.Add(container);

        // Workspace belongs to one Task (02 §16): created on the Task's first Run, reused by retries —
        // the Runtime API provisions the physical directory at /start and writes storage_reference back.
        var workspace = await db.Workspaces.SingleOrDefaultAsync(w => w.TaskId == task.Id, cancellationToken);
        if (workspace is null)
        {
            var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId, cancellationToken);
            var workItem = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workflow.WorkItemId, cancellationToken);
            workspace = new Workspace
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                WorkItemId = workItem.Id,
                RepositoryId = workItem.RepositoryId,
                Status = WorkspaceStatuses.Provisioning,
                CreatedAt = now,
            };
            db.Workspaces.Add(workspace);
        }
        await db.SaveChangesAsync(cancellationToken);
        return new RunPreparation(run, session, container, workspace, task, version, assembled);
    }
}
