using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator.Orchestration;

/// <summary>
/// The Orchestrator-side consumer of terminal Runs (the Orchestrator owns every Task/Workflow
/// transition; agents report facts — the Runtime API persisted them as RunEvents). Per tick:
/// <list type="bullet">
/// <item>Task `starting` whose Run reached `running` → Task `running` (the agent started executing).</item>
/// <item>Terminal Run → Task terminal: `succeeded` (+Workflow `waiting_for_human`, command `processed`),
/// `blocked` when the persisted `completed` callback carried `outcome = needs_human_input`,
/// `failed`. A pre-execution failure (no `agent_started` event) of a transient infrastructure kind with
/// automatic retry budget left requeues the Task SILENTLY: `Failed → Queued`,
/// fresh OrchestrationJob after the pinned backoff, attempt+1 via the normal claim flow; the Workflow
/// keeps the Task as its open work. Otherwise the failure is final: command `failed`, Workflow
/// `waiting_for_human`, desired container state `stopped`, and one Outbox failure notification per the
/// same failure-notification shape — a publication note for `github_publication_failed`, the
/// exhausted-retries closing when silent retries were involved, the plain resubmit text otherwise
/// (one for every finally-failed Task, `TimedOut` included).
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

        bool requeued = false; // a silent automatic retry keeps the Task as the Workflow's open work

        switch (run.Status)
        {
            case RunStatuses.Succeeded:
                var outcome = await CompletedOutcomeAsync(db, run, cancellationToken);
                if (outcome == RunEventVocabulary.NeedsHumanInput)
                {
                    task.Status = OrchestrationTaskStatuses.Blocked; // agent checkpointed + asked a human
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
            {
                // Execution began when the agent started (the event-driven `agent_started` callback);
                // without it the Run failed pre-execution — the only window automatic retry applies in.
                var preExecution = !await ExecutionStartedAsync(db, run.Id, cancellationToken);
                var policy = RetryPolicies.Parse(task.RetryPolicy);
                if (preExecution
                    && RunErrorCodes.TransientPreExecution.Contains(run.ErrorCode)
                    && task.AutomaticRetriesUsed < policy.MaxAutomaticRetries) // task-lifetime budget
                {
                    task.AutomaticRetriesUsed += 1;
                    RequeueTaskForAutomaticRetry(db, task, workflow, policy, task.AutomaticRetriesUsed, now);
                    requeued = true; // silent: no notification, no command/workflow terminal writes
                    break;
                }
                task.Status = OrchestrationTaskStatuses.Failed;
                task.FailureReason = run.ErrorCode ?? "runtime_failure";
                task.CompletedAt = now;
                if (command is not null)
                {
                    command.Status = AgentCommandStatuses.Failed;
                    command.RejectionReason = task.FailureReason;
                    command.ProcessedAt = now;
                }
                // every finally-failed Task notifies once — the exhausted-retries closing
                // applies when silent automatic retries were involved; publication failures keep their own.
                var retriesExhausted = preExecution && RunErrorCodes.TransientPreExecution.Contains(run.ErrorCode)
                    && task.AutomaticRetriesUsed > 0;
                await EnqueueFinalFailureNotificationAsync(db, task, run, workflow, preExecution, retriesExhausted, cancellationToken);
                break;
            }

            case RunStatuses.Cancelled:
                task.Status = OrchestrationTaskStatuses.Cancelled;
                task.CancelledAt = now;
                // the only Run canceller is /stop — the reason rides the
                // run's stop signal, not the trigger command's own rejection.
                task.FailureReason = AgentCommandRejectionReasons.StopRequested;
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
                // TimedOut never auto-retries (execution began) and still notifies.
                await EnqueueFinalFailureNotificationAsync(db, task, run, workflow,
                    preExecution: false, retriesExhausted: false, cancellationToken);
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

        // container desired state: stopped on Task completion — the Runtime API already stopped
        // it (also for a requeued attempt: the failed attempt's container is dead)
        var containers = await db.ContainerInstances
            .Where(c => c.RunId == run.Id)
            .ToListAsync(cancellationToken);
        foreach (var container in containers)
            container.DesiredState = ContainerDesiredStates.Stopped;

        // a requeued automatic retry keeps the Task as the Workflow's open work (staying Active
        // while its Task is queued again); only terminal paths hand the Workflow back
        if (!requeued)
        {
            if (workflow.CurrentTaskId == task.Id)
                workflow.CurrentTaskId = null;
            if (workflow.Status == WorkflowStatuses.Active)
                workflow.Status = WorkflowStatuses.WaitingForHuman;
        }
        workflow.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>True when the Run's agent actually began working (a persisted `agent_started` callback,
    /// the event-driven run promotion); the boundary between the automatic-retry window
    /// and manual `/retry`.</summary>
    private static async Task<bool> ExecutionStartedAsync(AppDbContext db, Guid runId, CancellationToken cancellationToken) =>
        await db.RunEvents.AsNoTracking()
            .AnyAsync(e => e.RunId == runId && e.Type == RunEventTypes.AgentStarted, cancellationToken);

    /// <summary>Automatic retry: the Task silently re-enters the normal claim flow —
    /// `Failed → Queued`, terminal markers cleared, a fresh OrchestrationJob schedules the new attempt
    /// after the pinned exponential backoff. The claim flow revalidates preconditions and creates the
    /// Run/AgentSession at attempt_number+1; the Workflow keeps the Task as its open work.</summary>
    private static void RequeueTaskForAutomaticRetry(
        AppDbContext db, OrchestrationTask task, Workflow workflow,
        RetryPolicyValues policy, int retriesUsed, DateTime now)
    {
        task.Status = OrchestrationTaskStatuses.Queued;
        task.CompletedAt = null;
        task.FailureReason = null;
        // initial backoff doubled per already-spent retry (defaults give one retry at 30 s)
        var backoff = TimeSpan.FromSeconds(policy.InitialBackoffSeconds * Math.Pow(2, retriesUsed - 1));
        db.OrchestrationJobs.Add(new OrchestrationJob
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            JobType = OrchestrationJobTypes.RunTask,
            Status = OrchestrationJobStatuses.Pending,
            Priority = 0,
            Attempts = 0,
            AvailableAt = now + backoff,
            CreatedAt = now,
            UpdatedAt = now,
        });
        workflow.UpdatedAt = now;
    }

    /// <summary>The final-failure notification: command, agent, attempt, stage and the
    /// error with a diagnostic id — never re-publishes the agent's result; the artifacts stay. One per
    /// finally-failed Task: the consumer transitions each Task exactly once and the enqueue rides the
    /// same change set. The Run id is the idempotency key.</summary>
    private static async Task EnqueueFinalFailureNotificationAsync(
        AppDbContext db, OrchestrationTask task, Run run, Workflow workflow,
        bool preExecution, bool retriesExhausted, CancellationToken cancellationToken)
    {
        var workItem = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workflow.WorkItemId, cancellationToken);
        var profileName = await db.AgentProfileVersions.AsNoTracking()
            .Where(v => v.Id == task.AgentProfileVersionId)
            .Join(db.AgentProfiles, v => v.AgentProfileId, p => p.Id, (v, p) => p.Name)
            .SingleOrDefaultAsync(cancellationToken);
        var commandToken = task.Type switch
        {
            OrchestrationTaskTypes.Plan => "/plan",
            OrchestrationTaskTypes.Implement => "/code",
            OrchestrationTaskTypes.Review => "/review",
            OrchestrationTaskTypes.TestDesign => "/test-cases",
            _ => $"/{task.Type.Replace('_', '-')}",
        };
        var stage = run.ErrorCode switch
        {
            RunErrorCodes.GithubPublicationFailed => "Required GitHub publication",
            RunErrorCodes.StartupTimeout => "Startup (never became ready)",
            RunErrorCodes.IdleTimeout => "Idle reconciliation (confirmed hang)",
            RunErrorCodes.RequiredPermissionDenied => "Required permission denied",
            _ => preExecution ? "Container startup" : "Execution",
        };
        var code = run.ErrorCode ?? (run.Status == RunStatuses.TimedOut ? "max_run_duration" : "runtime_failure");
        var closing = run.ErrorCode == RunErrorCodes.GithubPublicationFailed
            ? "The produced artifacts are kept. The Workflow is waiting for human input.\n" +
              "Check for an already-posted result comment, resolve the underlying issue and submit the command again (or use /retry)."
            : (retriesExhausted ? "Automatic retries exhausted. " : "") +
              "The Workflow is waiting for human input.\n" +
              "Fix the underlying issue and submit the command again (or use /retry).";
        var text =
            $"Task failed: {commandToken}\n" +
            $"Agent: {profileName ?? "unknown"} | Attempt: #{run.AttemptNumber} | Stage: {stage}\n" +
            $"Failure: {code} — {run.ErrorMessage ?? "see the run's logs"} (id: run_{run.Id})\n\n" +
            closing;
        await new OutboxActionStore(db).EnqueueAsync(
            new OutboxEnqueueRequest(workItem.RepositoryId, workItem.Id, task.Id, workItem.Number, text, run.Id),
            cancellationToken);
    }

    /// <summary>Reads the `outcome` from the run's persisted `completed` callback (the full body is
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
