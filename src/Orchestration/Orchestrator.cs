using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Orchestration.State;

namespace PiAgent.Orchestration;

/// <summary>
/// The Orchestrator core (docs/orchestrator.md): a pure event reactor with zero GitHub
/// knowledge. Every reaction is one unit of work — state changes and the events that
/// announce them commit atomically.
/// </summary>
public sealed class Orchestrator(
    IOrchestrationState state,
    IEventLog log,
    IEventReader eventReader,
    IOptions<OrchestrationOptions> options,
    ILogger<Orchestrator> logger)
{
    private OrchestrationOptions Options => options.Value;

    // ---- commands --------------------------------------------------------------

    public async Task HandleUserCommandAsync(UserCommand cmd, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.GetOrCreateWorkflowAsync(new ItemRef(cmd.Owner, cmd.Repo, cmd.ItemNumber), uow, ct);

        if (workflow.Status is not WorkflowStatuses.Active)
        {
            await RejectAsync(cmd.Owner, cmd.Repo, cmd.ItemNumber,
                $"workflow is {workflow.Status}; start a new command on an open item", uow, ct);
        }
        else if (!Permissions.AtLeast(cmd.AuthorPermission, Options.MinCommandPermission))
        {
            await RejectAsync(cmd.Owner, cmd.Repo, cmd.ItemNumber,
                $"@{cmd.Author} lacks the required permission ({Options.MinCommandPermission})", uow, ct);
        }
        else if (CommandRegistry.IsResume(cmd.Command))
        {
            if (await state.GetActiveTaskAsync(workflow.Id, uow, ct)
                is { Status: TaskStatuses.WaitingForHuman } parked)
            {
                logger.LogInformation("command admitted: /resume on {item} by {author} → task {type}",
                    workflow.Item.Key, cmd.Author, parked.Type);
                await ResumeTaskAsync(parked, uow, ct);
            }
            else
            {
                await RejectAsync(cmd.Owner, cmd.Repo, cmd.ItemNumber,
                    "nothing to resume — no task is waiting_for_human", uow, ct);
            }
        }
        else if (await state.GetActiveTaskAsync(workflow.Id, uow, ct) is { } active)
        {
            await RejectAsync(cmd.Owner, cmd.Repo, cmd.ItemNumber,
                $"a {active.Type} task is {active.Status}; the workflow runs one task at a time", uow, ct);
        }
        else if (CommandRegistry.TaskTypeFor(cmd.Command) is not { } taskType)
        {
            await RejectAsync(cmd.Owner, cmd.Repo, cmd.ItemNumber,
                $"unknown command '{cmd.Command}'", uow, ct);
        }
        else if (taskType is TaskTypes.Implement && Options.PlanApprovalRequired && !workflow.PlanApproved)
        {
            await RejectAsync(cmd.Owner, cmd.Repo, cmd.ItemNumber,
                "implementation requires an approved plan — run /plan first", uow, ct);
        }
        else
        {
            logger.LogInformation("command admitted: {command} on {item} by {author} → task {type}",
                cmd.Command, workflow.Item.Key, cmd.Author, taskType);
            await StartTaskAsync(workflow, taskType, cmd.Text, agentBranch: null, uow, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    public async Task HandleUserDecisionAsync(UserDecision decision, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(
            new ItemRef(decision.Owner, decision.Repo, decision.ItemNumber), uow, ct);
        if (workflow is null)
        {
            await uow.Events.CommitAsync(ct);
            return;
        }

        switch (decision.Kind)
        {
            case DecisionKind.PlanApproved:
                // the owner's ruling: approval only UNLOCKS the gate — development starts
                // when the user issues /code themselves
                await state.SetWorkflowPlanAsync(workflow.Id, approved: true, 0, uow, ct);
                logger.LogInformation("workflow {workflow}: plan approved by {author}",
                    workflow.Id, decision.Author);
                break;

            case DecisionKind.PlanChangesRequested or DecisionKind.ReviewChangeScope:
                await RestartPlanAsync(workflow, decision, uow, ct);
                break;

            case DecisionKind.ReviewFixes or DecisionKind.TestsBugFound:
                await StartFixingRoundAsync(workflow, decision, uow, ct);
                break;

            case DecisionKind.ReviewAcceptRisk:
                await ContinueAfterReviewAsync(workflow, uow, ct);
                break;

            case DecisionKind.TestsAccepted:
                await NotifyAsync(workflow, "workflow completed — the pull request awaits human merge", uow, ct);
                break;

            case DecisionKind.TestsRequirementsNotMet:
                await RestartPlanAsync(workflow, decision, uow, ct);
                break;
        }

        await uow.Events.CommitAsync(ct);
    }

    public async Task HandleUserCancelAsync(UserCancel cancel, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(
            new ItemRef(cancel.Owner, cancel.Repo, cancel.ItemNumber), uow, ct);
        if (workflow is not null)
        {
            await StopWorkflowAsync(workflow, uow, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    // ---- run lifecycle ---------------------------------------------------------

    public async Task HandleRunProcessingAsync(RunProcessing evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var run = await state.GetRunAsync(evt.RunId, uow, ct);
        var task = await state.GetTaskAsync(run.TaskId, uow, ct);
        var workflow = await state.GetWorkflowAsync(task.WorkflowId, uow, ct);

        await state.SetRunStatusAsync(evt.RunId, RunStatuses.Processing, uow, ct);
        await state.SetTaskStatusAsync(task.Id, TaskStatuses.Processing, uow, ct);
        await MoveBoardAsync(workflow, "processing", evt.RunId, uow, ct);

        await uow.Events.CommitAsync(ct);
    }

    public async Task HandleRunCompletedAsync(RunCompleted evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var run = await state.GetRunAsync(evt.RunId, uow, ct);
        var task = await state.GetTaskAsync(run.TaskId, uow, ct);
        var workflow = await state.GetWorkflowAsync(task.WorkflowId, uow, ct);

        await state.SetRunStatusAsync(evt.RunId, RunStatuses.Succeeded, uow, ct);
        if (task.Status is TaskStatuses.WaitingForHuman)
        {
            // parked by an escalation — a late completion does not advance the chain
            await uow.Events.CommitAsync(ct);
            return;
        }
        // the implement Task is NOT completed here: its result is the published PR —
        // PrPublished completes it and chains the review (HandlePrPublishedAsync), so the
        // review starts only on the published PR with the real number and this pin
        if (task.Type is not TaskTypes.Implement)
            await state.SetTaskStatusAsync(task.Id, TaskStatuses.Succeeded, uow, ct);

        // the Coder run's git facts are the Workflow's pointers: the agent branch a fix
        // round continues and the head commit a review round pins (docs/run-worker.md)
        if (task.Type is TaskTypes.Implement)
            await state.SetWorkflowPointersAsync(workflow.Id, evt.Branch, evt.CommitSha, uow, ct);

        switch (task.Type)
        {
            case TaskTypes.Plan:
                // the plan comment + approval flow already ran via plan_saved
                break;

            case TaskTypes.Implement:
                if (evt.Branch is null)
                {
                    // no git facts — the head branch to publish is unknown (docs/run-worker.md:
                    // run_completed "settles without pointers" when the facts are absent)
                    await state.SetTaskStatusAsync(task.Id, TaskStatuses.WaitingForHuman, uow, ct);
                    await NotifyAsync(workflow,
                        $"implement run {evt.RunId} completed without git facts (branch) — cannot publish PR",
                        uow, ct);
                    break;
                }
                // the head is the branch the run actually pushed (the run_completed git facts —
                // the same pointers SetWorkflowPointersAsync recorded above); the base comes
                // from the same config the run input's DefaultBranch came from
                await log.AppendAsync(new PublishPr
                {
                    Kind = PublishPrKind.Create,
                    Owner = workflow.Owner,
                    Repo = workflow.Repo,
                    WorkflowId = workflow.Id,
                    ItemNumber = workflow.ItemNumber,
                    IdempotencyKey = $"pr:{workflow.Id}:{run.Input.Profile}:{evt.RunId}",
                    HeadBranch = evt.Branch,
                    BaseBranch = DefaultBranchFor(workflow.Owner, workflow.Repo),
                    Title = $"AI: {run.Input.Command} on #{workflow.ItemNumber}",
                    Body = $"Workflow {workflow.Id}, task {task.Id}",
                    PrNumber = null,
                }, uow.Events, ct);
                await MoveBoardAsync(workflow, "review", evt.RunId, uow, ct);
                break;

            case TaskTypes.Review:
                var submitted = await eventReader.ReadByRunIdAsync<ReviewCommentsSubmitted>(evt.RunId, ct);
                if (submitted.SelectMany(e => e.Comments).ToList() is { Count: > 0 } comments)
                {
                    await log.AppendAsync(new PublishReview
                    {
                        Owner = workflow.Owner,
                        Repo = workflow.Repo,
                        PrNumber = run.Input.PrNumber ?? 0,
                        Verdict = "REQUEST_CHANGES",
                        Body = string.Join("\n\n", comments.Select(c => $"`{c.File}:{c.Line}` ({c.Severity}): {c.Message}")),
                        RunId = evt.RunId,
                        IdempotencyKey = $"review:{evt.RunId}",
                        HeadBranch = workflow.AgentBranch,
                    }, uow.Events, ct);
                    await StartFixingRoundAsync(workflow, null, uow, ct);
                }
                else
                {
                    // review passed — the outcome (approve) is published, testing follows
                    await log.AppendAsync(new PublishReview
                    {
                        Owner = workflow.Owner,
                        Repo = workflow.Repo,
                        PrNumber = run.Input.PrNumber ?? 0,
                        Verdict = "APPROVE",
                        Body = "Review passed",
                        RunId = evt.RunId,
                        IdempotencyKey = $"review:{evt.RunId}",
                        HeadBranch = workflow.AgentBranch,
                    }, uow.Events, ct);
                    await MoveBoardAsync(workflow, "testing", evt.RunId, uow, ct);
                    await CreateNextTaskAsync(workflow, task, TaskTypes.TestDesign, uow, ct);
                }
                break;

            case TaskTypes.TestDesign:
                // testing completed; manual acceptance (if required) is decided via TestsPassed path
                break;
        }

        await uow.Events.CommitAsync(ct);
    }

    public async Task HandleRunFailedAsync(RunFailed evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var run = await state.GetRunAsync(evt.RunId, uow, ct);
        var task = await state.GetTaskAsync(run.TaskId, uow, ct);
        var workflow = await state.GetWorkflowAsync(task.WorkflowId, uow, ct);

        await state.SetRunStatusAsync(evt.RunId, RunStatuses.Failed, uow, ct);
        if (task.Status is TaskStatuses.WaitingForHuman)
        {
            // parked by an escalation — its Run settled late; the parked state stands
            await uow.Events.CommitAsync(ct);
            return;
        }

        var attempts = (await state.ListRunsAsync(task.Id, RunStatuses.Succeeded, uow, ct)).Count
                       + (await state.ListRunsAsync(task.Id, RunStatuses.Failed, uow, ct)).Count
                       + (await state.ListRunsAsync(task.Id, RunStatuses.Cancelled, uow, ct)).Count;
        if (attempts <= Options.MaxRunRetries)
        {
            // auto-retry: a new Run of the same Task (docs/orchestrator.md §4)
            await state.SetTaskStatusAsync(task.Id, TaskStatuses.Queued, uow, ct);
            // auto-retry regenerates the session id: a retry must not inherit (and on a reused
            // tree, resume) the failed run's pi session — one id for the row AND the event
            var retryInput = run.Input with { SessionId = Guid.NewGuid().ToString("N") };
            var newRunId = await state.CreateRunAsync(task.Id, retryInput, uow, ct);
            await log.AppendAsync(new RunScheduled
            {
                RunId = newRunId,
                TaskId = task.Id,
                WorkflowId = workflow.Id,
                Input = retryInput,
                Secrets = SecretsFor(run.Input.Profile),
            }, uow.Events, ct);
            logger.LogWarning("run {run} failed ({cause}); retrying as run {new} (attempt {n})",
                evt.RunId, evt.Cause, newRunId, attempts + 1);
        }
        else
        {
            await state.SetTaskStatusAsync(task.Id, TaskStatuses.WaitingForHuman, uow, ct);
            if (IsImageMissingCause(evt.Cause))
            {
                logger.LogWarning("task {task} parked silently after {attempts} attempts: {cause}",
                    task.Id, attempts, evt.Cause);
            }
            else
            {
                await NotifyAsync(workflow,
                    $"task {task.Type} failed after {attempts} attempts: {evt.Cause}", uow, ct);
            }
        }

        await uow.Events.CommitAsync(ct);
    }

    public async Task HandleRunCancelledAsync(RunCancelled evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var run = await state.GetRunAsync(evt.RunId, uow, ct);
        var task = await state.GetTaskAsync(run.TaskId, uow, ct);

        await state.SetRunStatusAsync(evt.RunId, RunStatuses.Cancelled, uow, ct);
        if (task.Status is not TaskStatuses.WaitingForHuman) // a parked Task survives its stopped Run
            await state.SetTaskStatusAsync(task.Id, TaskStatuses.Cancelled, uow, ct);
        await uow.Events.CommitAsync(ct);
    }

    // ---- flow tool events ------------------------------------------------------

    public async Task HandlePlanSavedAsync(PlanSaved evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(new ItemRef(evt.Owner, evt.Repo, evt.ItemNumber), uow, ct);
        if (workflow is not null)
        {
            await state.SetWorkflowPlanAsync(workflow.Id, approved: !Options.PlanApprovalRequired, 0, uow, ct);
            await log.AppendAsync(new PublishComment
            {
                Kind = PublishCommentKind.Plan,
                Owner = evt.Owner,
                Repo = evt.Repo,
                EntityNumber = evt.ItemNumber,
                IdempotencyKey = $"plan:{evt.WorkflowId}",
                Body = $"## Plan\n\n{evt.Goals}\n\n## Scope\n\n{evt.Scope}\n\n## Acceptance criteria\n\n{evt.AcceptanceCriteria}",
            },
                uow.Events, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    /// <summary>
    /// Deliberately a no-op beyond the ack the delivery loop performs on return: one fixing
    /// round per REVIEWER RUN, not per comment event (docs/incident-2026-11-26-follow-ups.md
    /// Block 3.3b). The comments stay only in these already-recorded events; their aggregate
    /// is acted on once, at run completion (HandleRunCompletedAsync, case Review). Publishing
    /// or starting a fixing round here would revive the dual-verdict problem (per-event
    /// REQUEST_CHANGES vs the run's verdict) and spawn parallel implement chains. The type
    /// stays in OrchestratorAgent's Consumes list so the event is delivered and acked.
    /// </summary>
    public Task HandleReviewCommentsSubmittedAsync(ReviewCommentsSubmitted evt, CancellationToken ct) =>
        Task.CompletedTask;

    public async Task HandleBugReportedAsync(BugReported evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(new ItemRef(evt.Owner, evt.Repo, evt.ItemNumber), uow, ct);
        if (workflow is not null)
        {
            await log.AppendAsync(new PublishComment
            {
                Kind = PublishCommentKind.Bug,
                Owner = evt.Owner,
                Repo = evt.Repo,
                EntityNumber = evt.ItemNumber,
                IdempotencyKey = $"bug:{evt.TaskId}",
                Body = $"## Bug\n\n{evt.Summary}\n\n{string.Join("\n", evt.ReproSteps.Select(s => $"1. {s}"))}\n\n{evt.TestResults}",
            },
                uow.Events, ct);
            await StartFixingRoundAsync(workflow, null, uow, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    public async Task HandleTestsPassedAsync(TestsPassed evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(new ItemRef(evt.Owner, evt.Repo, evt.ItemNumber), uow, ct);
        if (workflow is not null && Options.ManualAcceptanceRequired)
        {
            await NotifyAsync(workflow,
                "tests passed — awaiting manual acceptance (accept / bug found / does not meet requirements)", uow, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    /// <summary>Escalations park the Task and stop the waiting Run (docs/flows tools.md).</summary>
    public async Task HandleEscalationAsync(IEvent evt, string body, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var (item, runId, taskId) = evt switch
        {
            BlockerReported b => (new ItemRef(b.Owner, b.Repo, b.ItemNumber), b.RunId, 0L),
            ClarificationRequested c => (new ItemRef(c.Owner, c.Repo, c.ItemNumber), c.RunId, 0L),
            ReviewDecisionRequested r => (new ItemRef(r.Owner, r.Repo, r.ItemNumber), r.RunId, 0L),
            EnvIssueReported e => (new ItemRef(e.Owner, e.Repo, e.ItemNumber), 0L, e.TaskId),
            CriteriaClarificationRequested c => (new ItemRef(c.Owner, c.Repo, c.ItemNumber), 0L, c.TaskId),
            _ => throw new InvalidOperationException($"not an escalation: {evt.GetType().Name}"),
        };

        var workflow = await state.FindWorkflowAsync(item, uow, ct);
        if (workflow is not null)
        {
            if (taskId > 0)
                await state.SetTaskStatusAsync(taskId, TaskStatuses.WaitingForHuman, uow, ct);
            else
                await ParkActiveTaskAsync(workflow, uow, ct);

            if (runId > 0)
                await RequestStopAsync(runId, uow, ct);

            await NotifyAsync(workflow, body, uow, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    // ---- lifecycle beats -------------------------------------------------------

    public async Task HandleItemClosedAsync(RepositoryClosed evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(new ItemRef(evt.Owner, evt.Repo, evt.Number), uow, ct);
        if (workflow is not null)
        {
            await state.SetWorkflowStatusAsync(workflow.Id, WorkflowStatuses.Closed, uow, ct);
            await StopWorkflowAsync(workflow, uow, ct, closeWorkflow: false);
        }

        await uow.Events.CommitAsync(ct);
    }

    public async Task HandleProjectItemChangedAsync(ProjectItemChanged evt, CancellationToken ct)
    {
        if (evt.LinkedOwner is null || evt.LinkedRepo is null || evt.LinkedItemNumber is null)
            return;

        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(
            new ItemRef(evt.LinkedOwner, evt.LinkedRepo, evt.LinkedItemNumber.Value), uow, ct);
        if (workflow is not null)
        {
            await state.SetWorkflowNodeAsync(workflow.Id, evt.ItemNodeId, uow, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    /// <summary>
    /// The Publisher's PR create/update succeeded — record the real PR number the review
    /// runs post to (docs/incident-2026-11-26-review-item27.md, Case B), complete the
    /// implement Task whose result the PR is, and chain the review task. No workflow
    /// (PR published for an unknown item) or no implement Task in flight — a replayed or
    /// foreign PrPublished: a no-op.
    /// </summary>
    public async Task HandlePrPublishedAsync(PrPublished evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(new ItemRef(evt.Owner, evt.Repo, evt.ItemNumber), uow, ct);
        if (workflow is not null)
        {
            await state.SetWorkflowHeadPrNumberAsync(workflow.Id, evt.PrNumber, uow, ct);
            // fresh pointers for the run input: the snapshot predates the PR number just
            // committed — the same staleness that left the review pin null (the TODO bug)
            workflow = await state.GetWorkflowAsync(workflow.Id, uow, ct);

            // the review chains here, never at run completion: by now the run's git facts
            // (the pinned head) and the real PR number are both committed, so the review
            // run's input carries them without guessing
            if (await state.GetActiveTaskAsync(workflow.Id, uow, ct)
                is { Status: TaskStatuses.Processing, Type: TaskTypes.Implement } implement)
            {
                await state.SetTaskStatusAsync(implement.Id, TaskStatuses.Succeeded, uow, ct);
                await CreateNextTaskAsync(workflow, implement, TaskTypes.Review, uow, ct);
            }
        }

        await uow.Events.CommitAsync(ct);
    }

    /// <summary>
    /// A PR publication died permanently (PublishFailed, announced by the Publisher
    /// before dead-lettering): the implement Task waiting on that PR parks for a human —
    /// processing must never be a terminal state. A fresh command (or /resume) restarts.
    /// </summary>
    public async Task HandlePublishFailedAsync(PublishFailed evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(new ItemRef(evt.Owner, evt.Repo, evt.ItemNumber), uow, ct);
        if (workflow is not null
            && await state.GetActiveTaskAsync(workflow.Id, uow, ct)
                is { Status: TaskStatuses.Processing, Type: TaskTypes.Implement } implement)
        {
            await state.SetTaskStatusAsync(implement.Id, TaskStatuses.WaitingForHuman, uow, ct);
            await NotifyAsync(workflow,
                $"PR publication failed permanently: {evt.Cause} — the implement task needs a human", uow, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    /// <summary>
    /// New commits on the PR pin the revision the next review round reviews
    /// (docs/flows events.md pr_updated): the head sha becomes the Workflow's review pin.
    /// </summary>
    public async Task HandlePrUpdatedAsync(PrUpdated evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(new ItemRef(evt.Owner, evt.Repo, evt.PrNumber), uow, ct);
        if (workflow is not null)
            await state.SetWorkflowPointersAsync(workflow.Id, agentBranch: null, headSha: evt.HeadSha, uow, ct);

        await uow.Events.CommitAsync(ct);
    }

    // ---- internals ---------------------------------------------------------------

    private async Task StartTaskAsync(WorkflowRow workflow, string taskType, string? commandText, string? agentBranch, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (await SlotOccupiedAsync(workflow, uow, ct))
            return;
        var taskId = await state.CreateTaskAsync(workflow.Id, taskType, uow, ct);
        await CreateRunAndScheduleAsync(workflow, taskId, commandText, agentBranch, uow, ct);
    }

    /// <summary>
    /// The one-active-task invariant (docs/incident-2026-11-26-review-item27.md, Case A):
    /// automatic transitions must not start a parallel task chain — a queued or processing
    /// Task occupies the Workflow's single slot.
    /// </summary>
    private async Task<bool> SlotOccupiedAsync(WorkflowRow workflow, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (await state.GetActiveTaskAsync(workflow.Id, uow, ct)
            is not { Status: TaskStatuses.Queued or TaskStatuses.Processing } task)
            return false;
        logger.LogWarning("workflow {workflow}: {type} task is {status}; the workflow runs one task at a time — not starting another",
            workflow.Id, task.Type, task.Status);
        return true;
    }

    /// <summary>
    /// RunInput v2 (docs/run-worker.md "Run input (pointers v2)"): every pointer is filled
    /// from Workflow/Task state at run_scheduled — the PR a review runs against, the agent
    /// branch a fix round continues, the configured default branch, and the review head pin.
    /// </summary>
    private async Task CreateRunAndScheduleAsync(WorkflowRow workflow, long taskId, string? commandText, string? agentBranch, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var task = await state.GetTaskAsync(taskId, uow, ct);
        var input = new RunInput
        {
            Owner = workflow.Owner,
            Repo = workflow.Repo,
            ItemNumber = workflow.ItemNumber,
            PrNumber = task.Type is TaskTypes.Review ? workflow.HeadPrNumber ?? workflow.ItemNumber : null,
            Command = commandText ?? task.Type,
            PlanCommentId = 0,
            Profile = CommandRegistry.ProfileFor(task.Type),
            AgentBranch = task.Type is TaskTypes.Implement ? agentBranch : null,
            DefaultBranch = DefaultBranchFor(workflow.Owner, workflow.Repo),
            ReviewHeadSha = task.Type is TaskTypes.Review ? workflow.HeadSha : null,
        };
        // the globally unique pi session id (docs/run-session-id.md): generated here, persisted on
        // the run row and the run_scheduled event — they must agree
        input = input with { SessionId = Guid.NewGuid().ToString("N") };
        var runId = await state.CreateRunAsync(taskId, input, uow, ct);
        await log.AppendAsync(new RunScheduled
        {
            RunId = runId,
            TaskId = taskId,
            WorkflowId = workflow.Id,
            Input = input,
            Secrets = SecretsFor(input.Profile),
        }, uow.Events, ct);
    }

    /// <summary>The profile's secret NAMES (config plane profile_secrets) for run_scheduled.</summary>
    private IReadOnlyList<string> SecretsFor(string profile) =>
        Options.ProfileSecrets.TryGetValue(profile, out var names)
            ? names
            : [];

    private string DefaultBranchFor(string owner, string repo) =>
        Options.DefaultBranches.TryGetValue($"{owner}/{repo}", out var branch)
            ? branch
            : Options.DefaultBranchFallback;

    private async Task CreateNextTaskAsync(WorkflowRow workflow, TaskRow current, string nextType, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (await SlotOccupiedAsync(workflow, uow, ct))
            return;
        var nextId = await state.CreateTaskAsync(workflow.Id, nextType, uow, ct);
        await CreateRunAndScheduleAsync(workflow, nextId, null, agentBranch: null, uow, ct);
        logger.LogInformation("workflow {workflow}: {current} succeeded → {next} task {task}",
            workflow.Id, current.Type, nextType, nextId);
    }

    private async Task ResumeTaskAsync(TaskRow task, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var workflow = await state.GetWorkflowAsync(task.WorkflowId, uow, ct);
        await state.SetTaskStatusAsync(task.Id, TaskStatuses.Queued, uow, ct);
        // a resumed Task continues where it parked — an Implement Task continues the agent branch
        await CreateRunAndScheduleAsync(workflow, task.Id, null, workflow.AgentBranch, uow, ct);
    }

    private async Task RestartPlanAsync(WorkflowRow workflow, UserDecision decision, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (await state.GetActiveTaskAsync(workflow.Id, uow, ct) is not { Status: TaskStatuses.WaitingForHuman })
        {
            logger.LogInformation("decision {kind} ignored: no parked task", decision.Kind);
            return;
        }

        await state.SetWorkflowPlanAsync(workflow.Id, approved: false, 0, uow, ct);
        await StartTaskAsync(workflow, TaskTypes.Plan, null, agentBranch: null, uow, ct);
    }

    private async Task StartFixingRoundAsync(WorkflowRow workflow, UserDecision? _decision, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (workflow.ReviewRounds + 1 > Options.MaxReviewRounds)
        {
            await ParkActiveTaskAsync(workflow, uow, ct);
            await NotifyAsync(workflow, "review round cap reached — review_loop_exhausted; a fresh command restarts the work", uow, ct);
            return;
        }

        if (await SlotOccupiedAsync(workflow, uow, ct))
            return;

        await state.IncrementReviewRoundsAsync(workflow.Id, uow, ct);
        // a fixing round continues the agent branch the previous Coder run pushed
        await StartTaskAsync(workflow, TaskTypes.Implement, null, workflow.AgentBranch, uow, ct);
    }

    private async Task ContinueAfterReviewAsync(WorkflowRow workflow, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (await state.GetActiveTaskAsync(workflow.Id, uow, ct) is { Status: TaskStatuses.WaitingForHuman } task
            && task.Type is TaskTypes.Review)
        {
            await state.SetTaskStatusAsync(task.Id, TaskStatuses.Succeeded, uow, ct);
            await CreateNextTaskAsync(workflow, task, TaskTypes.TestDesign, uow, ct);
        }
    }

    private async Task ParkActiveTaskAsync(WorkflowRow workflow, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (await state.GetActiveTaskAsync(workflow.Id, uow, ct) is { } task)
        {
            await state.SetTaskStatusAsync(task.Id, TaskStatuses.WaitingForHuman, uow, ct);
        }
    }

    private async Task StopWorkflowAsync(WorkflowRow workflow, IOrchestrationUnitOfWork uow, CancellationToken ct, bool closeWorkflow = true)
    {
        if (closeWorkflow)
            await state.SetWorkflowStatusAsync(workflow.Id, WorkflowStatuses.Cancelled, uow, ct);

        if (await state.GetActiveTaskAsync(workflow.Id, uow, ct) is { } task)
        {
            await state.SetTaskStatusAsync(task.Id, TaskStatuses.Cancelled, uow, ct);
            var inFlight = (await state.ListRunsAsync(task.Id, RunStatuses.Scheduled, uow, ct))
                .Concat(await state.ListRunsAsync(task.Id, RunStatuses.Processing, uow, ct));
            foreach (var run in inFlight)
                await RequestStopAsync(run.Id, uow, ct);
        }
    }

    private async Task RequestStopAsync(long runId, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var run = await state.GetRunAsync(runId, uow, ct);
        var task = await state.GetTaskAsync(run.TaskId, uow, ct);
        var workflow = await state.GetWorkflowAsync(task.WorkflowId, uow, ct);
        await log.AppendAsync(new RunStopRequested { RunId = runId, Owner = workflow.Owner, Repo = workflow.Repo, ItemNumber = workflow.ItemNumber, Reason = "cancelled" }, uow.Events, ct);
    }

    private async Task MoveBoardAsync(WorkflowRow workflow, string column, long runId, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (workflow.NodeId is null)
            return; // best effort: no synced card node id — the AI labels remain the lifecycle signal

        await log.AppendAsync(new PublishBoardMove
        {
            Owner = workflow.Owner,
            ProjectNumber = 0,
            ItemNodeId = workflow.NodeId,
            Status = column,
            RunId = runId,
            IdempotencyKey = $"board:{runId}:{column}",
        }, uow.Events, ct);
    }

    /// <summary>
    /// The RunWorker's quiet-early-exit cause ("image missing for profile X: image"): the run
    /// failed before provisioning, deterministically, and a GitHub comment would only feed the
    /// poller's resume loop (docs/incident-2026-11-26-follow-ups.md Block 2.2.1).
    /// </summary>
    private static bool IsImageMissingCause(string? cause) =>
        cause is not null && cause.StartsWith("image missing", StringComparison.Ordinal);

    private Task NotifyAsync(WorkflowRow workflow, string body, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        log.AppendAsync(new PublishComment
        {
            Kind = PublishCommentKind.Notification,
            Owner = workflow.Owner,
            Repo = workflow.Repo,
            EntityNumber = workflow.ItemNumber,
            IdempotencyKey = $"notification:{workflow.Id}:{body}",
            Body = body,
        }, uow.Events, ct);

    private async Task RejectAsync(string owner, string repo, long itemNumber, string reason, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        logger.LogInformation("command rejected on {owner}/{repo}#{number}: {reason}", owner, repo, itemNumber, reason);
        await log.AppendAsync(new PublishComment
        {
            Kind = PublishCommentKind.Notification,
            Owner = owner,
            Repo = repo,
            EntityNumber = itemNumber,
            IdempotencyKey = $"rejected:{owner}/{repo}#{itemNumber}:{reason}",
            Body = $"command rejected: {reason}",
        }, uow.Events, ct);
    }
}
