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
            await StartTaskAsync(workflow, taskType, cmd.Text, uow, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    /// <summary>
    /// A non-command comment on an item whose Task is parked is the User's reply
    /// (commands were derived by the Poller) — resumes the parked Task with a fresh Run.
    /// </summary>
    public Task HandleCommentChangedAsync(CommentChanged comment, CancellationToken ct)
    {
        var reply = new UserReply(comment.Owner, comment.Repo, comment.ItemNumber,
            comment.CommentId, comment.Author, comment.Body);
        return HandleUserReplyAsync(reply, ct);
    }

    /// <summary>A reply resumes the parked Task with a fresh Run.</summary>
    public async Task HandleUserReplyAsync(UserReply reply, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(new ItemRef(reply.Owner, reply.Repo, reply.ItemNumber), uow, ct);
        if (workflow is null)
        {
            await uow.Events.CommitAsync(ct);
            return;
        }

        if (await state.GetActiveTaskAsync(workflow.Id, uow, ct) is { Status: TaskStatuses.WaitingForHuman } task)
        {
            await ResumeTaskAsync(task, uow, ct);
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
                await state.SetWorkflowPlanAsync(workflow.Id, approved: true, 0, uow, ct);
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
        await state.SetTaskStatusAsync(task.Id, TaskStatuses.Succeeded, uow, ct);

        switch (task.Type)
        {
            case TaskTypes.Plan:
                // the plan comment + approval flow already ran via plan_saved
                break;

            case TaskTypes.Implement:
                await log.AppendAsync(new PublishPr(
                    PublishPrKind.Create, workflow.Owner, workflow.Repo, workflow.Id,
                    IdempotencyKey: $"pr:{workflow.Id}:{run.Input.Profile}:{evt.RunId}",
                    HeadBranch: $"ai/task-{task.Id}", BaseBranch: "main",
                    Title: $"AI: {run.Input.Command} on #{workflow.ItemNumber}",
                    Body: $"Workflow {workflow.Id}, task {task.Id}", PrNumber: null), uow.Events, ct);
                await MoveBoardAsync(workflow, "review", evt.RunId, uow, ct);
                await CreateNextTaskAsync(workflow, task, TaskTypes.Review, uow, ct);
                break;

            case TaskTypes.Review:
                // review passed — the outcome (approve) is published, testing follows
                await log.AppendAsync(new PublishReview(
                    workflow.Owner, workflow.Repo, run.Input.PrNumber ?? 0,
                    Verdict: "APPROVE", Body: "Review passed", evt.RunId,
                    IdempotencyKey: $"review:{evt.RunId}"), uow.Events, ct);
                await MoveBoardAsync(workflow, "testing", evt.RunId, uow, ct);
                await CreateNextTaskAsync(workflow, task, TaskTypes.TestDesign, uow, ct);
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
            var newRunId = await state.CreateRunAsync(task.Id, run.Input, uow, ct);
            await log.AppendAsync(new RunScheduled(newRunId, task.Id, workflow.Id, run.Input), uow.Events, ct);
            logger.LogWarning("run {run} failed ({cause}); retrying as run {new} (attempt {n})",
                evt.RunId, evt.Cause, newRunId, attempts + 1);
        }
        else
        {
            await state.SetTaskStatusAsync(task.Id, TaskStatuses.WaitingForHuman, uow, ct);
            await NotifyAsync(workflow,
                $"task {task.Type} failed after {attempts} attempts: {evt.Cause}", uow, ct);
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
            await log.AppendAsync(new PublishComment(
                PublishCommentKind.Plan, evt.Owner, evt.Repo, evt.ItemNumber,
                IdempotencyKey: $"plan:{evt.WorkflowId}",
                Body: $"## Plan\n\n{evt.Goals}\n\n## Scope\n\n{evt.Scope}\n\n## Acceptance criteria\n\n{evt.AcceptanceCriteria}"),
                uow.Events, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    public async Task HandleReviewCommentsSubmittedAsync(ReviewCommentsSubmitted evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(new ItemRef(evt.Owner, evt.Repo, evt.ItemNumber), uow, ct);
        if (workflow is not null)
        {
            await log.AppendAsync(new PublishReview(
                evt.Owner, evt.Repo, evt.PrNumber, Verdict: "REQUEST_CHANGES",
                Body: string.Join("\n\n", evt.Comments.Select(c => $"`{c.File}:{c.Line}` ({c.Severity}): {c.Message}")),
                evt.RunId, IdempotencyKey: $"review:{evt.RunId}"), uow.Events, ct);
            await StartFixingRoundAsync(workflow, null, uow, ct);
        }

        await uow.Events.CommitAsync(ct);
    }

    public async Task HandleBugReportedAsync(BugReported evt, CancellationToken ct)
    {
        await using var uow = await state.Factory.BeginAsync(ct);
        var workflow = await state.FindWorkflowAsync(new ItemRef(evt.Owner, evt.Repo, evt.ItemNumber), uow, ct);
        if (workflow is not null)
        {
            await log.AppendAsync(new PublishComment(
                PublishCommentKind.Bug, evt.Owner, evt.Repo, evt.ItemNumber,
                IdempotencyKey: $"bug:{evt.TaskId}",
                Body: $"## Bug\n\n{evt.Summary}\n\n{string.Join("\n", evt.ReproSteps.Select(s => $"1. {s}"))}\n\n{evt.TestResults}"),
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

    public async Task HandleItemClosedAsync(ItemClosed evt, CancellationToken ct)
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

    // ---- internals ---------------------------------------------------------------

    private async Task StartTaskAsync(WorkflowRow workflow, string taskType, string? commandText, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var taskId = await state.CreateTaskAsync(workflow.Id, taskType, uow, ct);
        await CreateRunAndScheduleAsync(workflow, taskId, commandText, uow, ct);
    }

    private async Task CreateRunAndScheduleAsync(WorkflowRow workflow, long taskId, string? commandText, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var task = await state.GetTaskAsync(taskId, uow, ct);
        var input = new RunInput(workflow.Owner, workflow.Repo, workflow.ItemNumber,
            PrNumber: task.Type is TaskTypes.Review ? workflow.ItemNumber : null,
            Command: commandText ?? task.Type, PlanCommentId: 0,
            Profile: CommandRegistry.ProfileFor(task.Type));
        var runId = await state.CreateRunAsync(taskId, input, uow, ct);
        await log.AppendAsync(new RunScheduled(runId, taskId, workflow.Id, input), uow.Events, ct);
    }

    private async Task CreateNextTaskAsync(WorkflowRow workflow, TaskRow current, string nextType, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var nextId = await state.CreateTaskAsync(workflow.Id, nextType, uow, ct);
        await CreateRunAndScheduleAsync(workflow, nextId, null, uow, ct);
        logger.LogInformation("workflow {workflow}: {current} succeeded → {next} task {task}",
            workflow.Id, current.Type, nextType, nextId);
    }

    private async Task ResumeTaskAsync(TaskRow task, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        var workflow = await state.GetWorkflowAsync(task.WorkflowId, uow, ct);
        await state.SetTaskStatusAsync(task.Id, TaskStatuses.Queued, uow, ct);
        await CreateRunAndScheduleAsync(workflow, task.Id, null, uow, ct);
    }

    private async Task RestartPlanAsync(WorkflowRow workflow, UserDecision decision, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (await state.GetActiveTaskAsync(workflow.Id, uow, ct) is not { Status: TaskStatuses.WaitingForHuman })
        {
            logger.LogInformation("decision {kind} ignored: no parked task", decision.Kind);
            return;
        }

        await state.SetWorkflowPlanAsync(workflow.Id, approved: false, 0, uow, ct);
        await StartTaskAsync(workflow, TaskTypes.Plan, null, uow, ct);
    }

    private async Task StartFixingRoundAsync(WorkflowRow workflow, UserDecision? _decision, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (workflow.ReviewRounds + 1 > Options.MaxReviewRounds)
        {
            await ParkActiveTaskAsync(workflow, uow, ct);
            await NotifyAsync(workflow, "review round cap reached — review_loop_exhausted; a fresh command restarts the work", uow, ct);
            return;
        }

        await state.IncrementReviewRoundsAsync(workflow.Id, uow, ct);
        await StartTaskAsync(workflow, TaskTypes.Implement, null, uow, ct);
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
        await log.AppendAsync(new RunStopRequested(runId, workflow.Owner, workflow.Repo, workflow.ItemNumber, "cancelled"), uow.Events, ct);
    }

    private async Task MoveBoardAsync(WorkflowRow workflow, string column, long runId, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        if (workflow.NodeId is null)
            return; // best effort: no synced card node id — the AI labels remain the lifecycle signal

        await log.AppendAsync(new PublishBoardMove(
            workflow.Owner, 0, workflow.NodeId, column, runId,
            IdempotencyKey: $"board:{runId}:{column}"), uow.Events, ct);
    }

    private Task NotifyAsync(WorkflowRow workflow, string body, IOrchestrationUnitOfWork uow, CancellationToken ct) =>
        log.AppendAsync(new PublishComment(
            PublishCommentKind.Notification, workflow.Owner, workflow.Repo, workflow.ItemNumber,
            IdempotencyKey: $"notification:{workflow.Id}:{body}",
            Body: body), uow.Events, ct);

    private async Task RejectAsync(string owner, string repo, long itemNumber, string reason, IOrchestrationUnitOfWork uow, CancellationToken ct)
    {
        logger.LogInformation("command rejected on {owner}/{repo}#{number}: {reason}", owner, repo, itemNumber, reason);
        await log.AppendAsync(new PublishComment(
            PublishCommentKind.Notification, owner, repo, itemNumber,
            IdempotencyKey: $"rejected:{owner}/{repo}#{itemNumber}:{reason}",
            Body: $"command rejected: {reason}"), uow.Events, ct);
    }
}
