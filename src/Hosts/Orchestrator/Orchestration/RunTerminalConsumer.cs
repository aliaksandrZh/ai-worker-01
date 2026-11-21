using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.Orchestrator.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The Orchestrator-side consumer of terminal Runs (03 §2: the Orchestrator owns every Task/Workflow
/// transition; agents report facts — the Runtime API persisted them as RunEvents). Per tick:
/// <list type="bullet">
/// <item>Task `starting` whose Run reached `running` → Task `running` (the agent started executing).</item>
/// <item>Terminal Run → Task terminal: `succeeded` (+Workflow `waiting_for_human`, command `processed`),
/// `blocked` when the persisted `completed` callback carried `outcome = needs_human_input` (decision 72),
/// `failed` (`run.error_code` as the recorded reason, job `failed`). Desired container state becomes
/// `stopped` — the Runtime API already stopped the container at terminal delivery (decision 75:
/// the slot releases at terminal state + confirmed stop).</item>
/// </list>
/// Task-failure notifications (06 §13) land with ticket 15's retry semantics — deliberately not here.
/// </summary>
public sealed class RunTerminalConsumer(IServiceScopeFactory scopeFactory) : IWorkerCycle
{
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var candidates = await db.OrchestrationTasks
            .Where(t => t.Status == OrchestrationTaskStatuses.Starting || t.Status == OrchestrationTaskStatuses.Running)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
            return;

        foreach (var task in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TransitionAsync(db, task, cancellationToken))
                continue;
        }
    }

    /// <summary>Applies one Run-driven transition; false when the task's Run still runs (nothing to do).</summary>
    private async Task<bool> TransitionAsync(AppDbContext db, OrchestrationTask task, CancellationToken cancellationToken)
    {
        var run = await db.Runs
            .Where(r => r.TaskId == task.Id)
            .OrderByDescending(r => r.AttemptNumber)
            .FirstOrDefaultAsync(cancellationToken);
        if (run is null)
            return false;
        var now = DateTime.UtcNow;

        if (run.Status == RunStatuses.Pending || run.Status == RunStatuses.Starting)
            return true; // dispatch in flight
        if (run.Status == RunStatuses.Running)
        {
            task.Status = OrchestrationTaskStatuses.Running;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        if (run.Status is not (RunStatuses.Succeeded or RunStatuses.Failed or RunStatuses.Cancelled or RunStatuses.TimedOut))
            return true;

        var command = task.TriggerCommandId is { } commandId
            ? await db.AgentCommands.SingleOrDefaultAsync(c => c.Id == commandId, cancellationToken)
            : null;
        var workflow = await db.Workflows.SingleAsync(w => w.Id == task.WorkflowId, cancellationToken);

        switch (run.Status)
        {
            case RunStatuses.Succeeded:
                var outcome = await CompletedOutcomeAsync(db, run, cancellationToken);
                if (outcome == RunEventVocabulary.NeedsHumanInput)
                {
                    task.Status = OrchestrationTaskStatuses.Blocked; // decision 72: agent checkpointed + asked a human
                }
                else
                {
                    task.Status = OrchestrationTaskStatuses.Succeeded;
                }
                task.CompletedAt = now;
                if (command is not null)
                {
                    command.Status = AgentCommandStatuses.Processed;
                    command.ProcessedAt = now;
                }
                break;

            case RunStatuses.Failed:
                task.Status = OrchestrationTaskStatuses.Failed;
                task.FailureReason = run.ErrorCode ?? "runtime_failure";
                task.CompletedAt = now;
                if (command is not null)
                {
                    command.Status = AgentCommandStatuses.Failed;
                    command.RejectionReason = task.FailureReason;
                    command.ProcessedAt = now;
                }
                break;

            case RunStatuses.Cancelled:
                task.Status = OrchestrationTaskStatuses.Cancelled;
                task.CancelledAt = now;
                if (command is not null)
                {
                    command.Status = AgentCommandStatuses.Failed;
                    command.RejectionReason = "run_cancelled";
                    command.ProcessedAt = now;
                }
                break;

            case RunStatuses.TimedOut:
                task.Status = OrchestrationTaskStatuses.Failed;
                task.FailureReason = "max_run_duration";
                task.CompletedAt = now;
                if (command is not null)
                {
                    command.Status = AgentCommandStatuses.Failed;
                    command.RejectionReason = "run_timed_out";
                    command.ProcessedAt = now;
                }
                break;
        }

        var job = await db.OrchestrationJobs
            .Where(j => j.TaskId == task.Id
                && (j.Status == OrchestrationJobStatuses.Pending || j.Status == OrchestrationJobStatuses.Processing))
            .ToListAsync(cancellationToken);
        foreach (var openJob in job)
        {
            openJob.Status = run.Status == RunStatuses.Cancelled ? OrchestrationJobStatuses.Cancelled : OrchestrationJobStatuses.Completed;
            openJob.UpdatedAt = now;
            if (run.Status == RunStatuses.Failed)
                openJob.LastError = run.ErrorCode ?? run.ErrorMessage;
        }

        // container desired state (03 §5): stopped on Task completion — the Runtime API already stopped it
        var containers = await db.ContainerInstances
            .Where(c => c.RunId == run.Id)
            .ToListAsync(cancellationToken);
        foreach (var container in containers)
            container.DesiredState = ContainerDesiredStates.Stopped;

        if (workflow.CurrentTaskId == task.Id)
            workflow.CurrentTaskId = null;
        if (workflow.Status == WorkflowStatuses.Active)
            workflow.Status = WorkflowStatuses.WaitingForHuman;
        workflow.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>Reads the `outcome` from the run's persisted `completed` callback (04 §4: the full body is
    /// a RunEvent payload before acknowledgement). Default `completed`.</summary>
    public static async Task<string> CompletedOutcomeAsync(AppDbContext db, Run run, CancellationToken cancellationToken)
    {
        var payload = await db.RunEvents.AsNoTracking()
            .Where(e => e.RunId == run.Id && e.Type == RunEventTypes.Completed)
            .OrderByDescending(e => e.ReceivedAt)
            .Select(e => e.Payload)
            .FirstOrDefaultAsync(cancellationToken);
        if (payload is null)
            return "completed";
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.TryGetProperty("outcome", out var outcome) && outcome.ValueKind == JsonValueKind.String
            ? outcome.GetString()!
            : "completed";
    }
}

/// <summary>Vocabulary read from persisted payloads (kept next to RunEventTypes usage).</summary>
public static class RunEventVocabulary
{
    public const string NeedsHumanInput = "needs_human_input";
}
