using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
public sealed class RunTerminalConsumer(IServiceScopeFactory scopeFactory, ILogger<RunTerminalConsumer> logger) : IWorkerCycle
{
    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var configurationState = scope.ServiceProvider.GetRequiredService<ConfigurationState>();

        var candidates = await db.OrchestrationTasks
            .Where(t => t.Status == OrchestrationTaskStatuses.Starting || t.Status == OrchestrationTaskStatuses.Running)
            .ToListAsync(cancellationToken);
        if (candidates.Count == 0)
            return;

        foreach (var task in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await TransitionAsync(db, configurationState, task, cancellationToken))
                continue;
        }
    }

    /// <summary>Applies one Run-driven transition; false when the task's Run still runs (nothing to do).</summary>
    private async Task<bool> TransitionAsync(AppDbContext db, ConfigurationState configurationState,
        OrchestrationTask task, CancellationToken cancellationToken)
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
        var workItem = await db.WorkItems.AsNoTracking()
            .SingleAsync(w => w.Id == workflow.WorkItemId, cancellationToken);

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
                // the run completed — the AI work of this task is done (the "asked a human" variant
                // included: the agent did finish its turn); the human's answer is a new task
                await AiStateLabels.MarkAsync(db, workItem.RepositoryId, workItem.Id, workItem.Number,
                    task.Id, AiStateLabels.Finished, cancellationToken);
                // an implement Task succeeding means the pull request is up — the board moves to the
                // review column (projects.yaml `ai_statuses.review`); best-effort, outbox-delivered
                if (task.Type == OrchestrationTaskTypes.Implement)
                    await BoardStatusOutbox.MoveAsync(db, configurationState, workItem.RepositoryId, workItem.Id,
                        workItem.Number, task.Id, BoardStatusOutbox.State.Review, cancellationToken);
                // a review Task succeeding settles the review→fix loop: `completed` moves the card to
                // the testing column; `changes_requested` either spends one more fix round (the card
                // back to In Progress — that transition IS the auto_fix trigger) or parks it: the cap
                // (projects.yaml `review_loop.max_rounds`) reached → the card stays in review, the
                // item is marked AI Failed and a review_loop_exhausted comment waits for a human
                if (task.Type == OrchestrationTaskTypes.Review)
                    await SettleReviewLoopAsync(db, configurationState, task, run, workItem, outcome, cancellationToken);
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
                    logger.LogInformation(
                        "run {runId} failed pre-execution ({errorCode}) — task {taskId} requeued for automatic retry {retry}/{max}",
                        run.Id, run.ErrorCode, task.Id, task.AutomaticRetriesUsed, policy.MaxAutomaticRetries);
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
                // finally failed (the silent-retry path above returns without touching labels —
                // the item stays "in work" while its retry is queued)
                await AiStateLabels.MarkAsync(db, workItem.RepositoryId, workItem.Id, workItem.Number,
                    task.Id, AiStateLabels.Failed, cancellationToken);
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
                // a stop is not a failure: remove all four AI labels (the queued-cancellation
                // path in StopCommandHandler clears as well — both are idempotent, one lands)
                await AiStateLabels.ClearAsync(db, workItem.RepositoryId, workItem.Id, workItem.Number,
                    task.Id, cancellationToken);
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
                await AiStateLabels.MarkAsync(db, workItem.RepositoryId, workItem.Id, workItem.Number,
                    task.Id, AiStateLabels.Failed, cancellationToken);
                await EnqueueFinalFailureNotificationAsync(db, task, run, workflow,
                    preExecution: false, retriesExhausted: false, cancellationToken);
                break;
        }

        // the terminal transition itself — one line per run outcome for the full-flow trace
        logger.LogInformation(
            "run {runId} terminal ({runStatus}): task {taskId} → {taskStatus}{reason}",
            run.Id, run.Status, task.Id, task.Status,
            task.FailureReason is null ? "" : $" ({task.FailureReason})");

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

    /// <summary>
    /// The review→fix loop settlement (review Task succeeded). The loop card is the PR's linked
    /// issue (the board card the human moves); the counter and the cap live on it:
    /// <list type="bullet">
    /// <item>`completed` — the review passed: the card moves to the testing column
    /// (projects.yaml `ai_statuses.testing`);</item>
    /// <item>`changes_requested` with rounds left — the card returns to In Progress and the counter
    /// increments; that In Review → In Progress transition IS the auto_fix trigger (the fix coder
    /// continues the PR branch from the review findings);</item>
    /// <item>`changes_requested` with the cap (`review_loop.max_rounds`, default 3) already reached —
    /// the card STAYS in the review column: AI Failed (replaces the Finished the success path set)
    /// on the PR and the card, and one review_loop_exhausted comment on the card waits for a human
    /// (any manual task-producing command resets the counter and restarts the loop).</item>
    /// </list>
    /// Best-effort like every board move: a card off a configured board just skips the move; the
    /// round counter still increments (the cap is what stops the loop, not the board).
    /// </summary>
    private async Task SettleReviewLoopAsync(
        AppDbContext db, ConfigurationState configurationState, OrchestrationTask task, Run run,
        WorkItem pullRequest, string outcome, CancellationToken cancellationToken)
    {
        var card = await ReviewFlow.LoopCardAsync(db, pullRequest, cancellationToken);
        if (card is null)
            return; // dangling link: nothing to settle the loop on

        if (outcome != RunEventVocabulary.ChangesRequested)
        {
            await BoardStatusOutbox.MoveAsync(db, configurationState, card.RepositoryId, card.Id,
                card.Number, task.Id, BoardStatusOutbox.State.Testing, cancellationToken);
            return;
        }

        var maxRounds = await ReviewFlow.MaxRoundsAsync(db, configurationState,
            card.RepositoryId, card.Number, cancellationToken);
        if (card.ReviewFixRounds >= maxRounds)
        {
            // the cap is reached — park: the card keeps its review column, the loop waits for a human.
            // AI Failed replaces the just-set AI Finished (the work did NOT end well for the loop);
            // the dual write covers the PR and the card (AiStateLabels marks the linked issue too)
            await AiStateLabels.MarkAsync(db, card.RepositoryId, pullRequest.Id, pullRequest.Number,
                task.Id, AiStateLabels.Failed, cancellationToken);
            await new OutboxActionStore(db).EnqueueAsync(
                new OutboxEnqueueRequest(card.RepositoryId, card.Id, task.Id, card.Number,
                    ExhaustedText(task.Id, maxRounds), run.Id), cancellationToken);
            logger.LogInformation(
                "review loop exhausted on card {cardNumber}: {rounds}/{max} fix rounds consumed — parked in review (run {runId}, task {taskId})",
                card.Number, card.ReviewFixRounds, maxRounds, run.Id, task.Id);
            return;
        }

        // one more round: the counter increments and the card returns to In Progress — the
        // transition event is the auto_fix trigger that queues the fix coder
        await db.WorkItems.Where(w => w.Id == card.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.ReviewFixRounds, card.ReviewFixRounds + 1), cancellationToken);
        await BoardStatusOutbox.MoveAsync(db, configurationState, card.RepositoryId, card.Id,
            card.Number, task.Id, BoardStatusOutbox.State.Processing, cancellationToken);
        logger.LogInformation(
            "review requested changes on card {cardNumber}: fix round {round}/{max} starts — the card returns to In Progress (run {runId}, task {taskId})",
            card.Number, card.ReviewFixRounds + 1, maxRounds, run.Id, task.Id);
    }

    /// <summary>The exhaustion comment: what happened, where the findings are, how a human resumes.</summary>
    private static string ExhaustedText(Guid taskId, int maxRounds) =>
        $"Review loop exhausted (code: {ReviewFlow.ExhaustedCode})\n" +
        $"The review has requested changes {maxRounds} time(s) in a row (the automatic fix limit is {maxRounds}). " +
        "The card stays in the review column and the automatic loop stops — the review findings (inline comments on the pull request) need human attention.\n\n" +
        "How to resume: address the findings yourself (or move the card back to In Progress and run /code — a manual command resets the round counter), " +
        "then the loop continues from a fresh review." +
        $"\n(task: {taskId})";

    /// <summary>True when the Run's agent actually began working (a persisted `agent_started` callback,
    /// the event-driven run promotion); the boundary between the automatic-retry window
    /// and manual `/retry`.</summary>

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

    /// <summary>The reviewer's verdict carried in the completed callback's `outcome`: the reviewer
    /// called the request_changes tool (the Bridge observes the execution and reports the call) —
    /// fixes are needed; the absence of the call means the review passed.</summary>
    public const string ChangesRequested = "changes_requested";
}
