using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.Postgres;
using PiAgent.Orchestration;
using PiAgent.Orchestration.State;
using PiAgent.Orchestration.State.Postgres;
using PiAgent.Tests.Postgres;
using Xunit;


namespace PiAgent.Events.Tests;

/// <summary>
/// The Orchestrator reactor against the real Postgres state + log: admission, the
/// Workflow/Task/Run machine, the flow chain, policies, escalation and cancellation
/// (docs/orchestrator.md).
/// </summary>
[Collection("PostgresLog")]
public sealed class OrchestratorReactorTests : IAsyncLifetime
{
    private readonly PostgresFixture _fx;
    private readonly EventQueueStore _log;
    private readonly PostgresOrchestrationState _state;
    private readonly Orchestrator _orchestrator;

    public OrchestratorReactorTests(PostgresFixture fx)
    {
        _fx = fx;
        _log = fx.Log;
        _state = new PostgresOrchestrationState(
            fx.DataSource, fx.Log, NullLogger<PostgresOrchestrationState>.Instance);
        _orchestrator = new Orchestrator(
            _state, _log, Options.Create(new OrchestrationOptions
            {
                PlanApprovalRequired = false,
                ManualAcceptanceRequired = false,
                MaxReviewRounds = 3,
                MaxRunRetries = 2,
            }),
            NullLogger<Orchestrator>.Instance);
    }

    public async Task InitializeAsync()
    {
        await _state.EnsureSchemaAsync(CancellationToken.None);
        await _fx.ResetCoreAsync();
        // the state machine's tables too — a previous test's workflow must not survive
        await using var cmd = _fx.DataSource.CreateCommand(
            "TRUNCATE orch_run, orch_task, orch_workflow RESTART IDENTITY");
        await cmd.ExecuteNonQueryAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // reads go through a real unit of work; the handlers have already committed,
    // so an uncommitted (rolled-back) read transaction sees everything
    private async Task<WorkflowRow> WorkflowAsync(long workflowId)
    {
        await using var uow = await _state.Factory.BeginAsync(CancellationToken.None);
        return await _state.GetWorkflowAsync(workflowId, uow, CancellationToken.None);
    }

    private async Task<TaskRow> TaskAsync(long taskId)
    {
        await using var uow = await _state.Factory.BeginAsync(CancellationToken.None);
        return await _state.GetTaskAsync(taskId, uow, CancellationToken.None);
    }

    private async Task<TaskRow?> ActiveTaskAsync(long workflowId)
    {
        await using var uow = await _state.Factory.BeginAsync(CancellationToken.None);
        return await _state.GetActiveTaskAsync(workflowId, uow, CancellationToken.None);
    }

    private async Task<WorkflowRow?> FindWorkflowScopedAsync(ItemRef item)
    {
        await using var uow = await _state.Factory.BeginAsync(CancellationToken.None);
        return await _state.FindWorkflowAsync(item, uow, CancellationToken.None);
    }

    private async Task<IReadOnlyList<IEvent>> AppendedAsync() => await _fx.AppendedAsync();

    /// <summary>The invariant keeps a queued/processing Task in the slot; tests that drive
    /// the next automatic transition settle it first (the reactor does this via run events).</summary>
    private async Task SettleActiveTaskAsync(long workflowId)
    {
        var active = Assert.IsType<TaskRow>(await ActiveTaskAsync(workflowId));
        await using var uow = await _state.Factory.BeginAsync(CancellationToken.None);
        await _state.SetTaskStatusAsync(active.Id, TaskStatuses.Succeeded, uow, CancellationToken.None);
        await uow.Events.CommitAsync(CancellationToken.None);
    }

    private static UserCommand Command(string command = "/code", string permission = "write") =>
        new(command, "o", "r", 1, CommentId: 100, Author: "u", permission, Text: command);

    private UserCommand PlanCommand() => Command("/plan");

    private async Task<WorkflowRow> RunCommandAsync(UserCommand cmd)
    {
        await _orchestrator.HandleUserCommandAsync(cmd, CancellationToken.None);
        var workflow = await FindWorkflowScopedAsync(new ItemRef(cmd.Owner, cmd.Repo, cmd.ItemNumber));
        return workflow ?? throw new InvalidOperationException("workflow missing");
    }

    [Fact]
    public async Task Command_admits_to_workflow_task_and_scheduled_run()
    {
        var workflow = await RunCommandAsync(Command());

        Assert.Equal(WorkflowStatuses.Active, workflow.Status);
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskTypes.Implement, Assert.IsType<TaskRow>(active).Type);
        Assert.Contains(await AppendedAsync(), e => e is RunScheduled s && s.Input.Profile == "coder");
    }

    [Fact]
    public async Task Scheduled_input_carries_the_v2_pointers_on_the_planner_path()
    {
        await RunCommandAsync(PlanCommand());

        var scheduled = Assert.Single((await AppendedAsync()).OfType<RunScheduled>());
        Assert.Null(scheduled.Input.AgentBranch); // planner clones plain
        Assert.Null(scheduled.Input.ReviewHeadSha);
        Assert.Null(scheduled.Input.PrNumber);
        Assert.Equal("main", scheduled.Input.DefaultBranch); // fallback: no config entry
    }

    [Fact]
    public async Task Default_branch_comes_from_the_repositories_config()
    {
        var orchestrator = new Orchestrator(_state, _log,
            Options.Create(new OrchestrationOptions
            {
                PlanApprovalRequired = false,
                DefaultBranches = new Dictionary<string, string> { ["o/r"] = "develop" },
            }),
            NullLogger<Orchestrator>.Instance);
        await orchestrator.HandleUserCommandAsync(Command(), CancellationToken.None);

        var scheduled = Assert.Single((await AppendedAsync()).OfType<RunScheduled>());
        Assert.Equal("develop", scheduled.Input.DefaultBranch);
    }

    [Fact]
    public async Task Run_completed_records_the_agent_branch_and_head_sha_as_workflow_pointers()
    {
        var workflow = await RunCommandAsync(Command());

        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted(1, 1, "o", "r", 1, Branch: "ai/task-1", CommitSha: "c0ffee"), CancellationToken.None);

        workflow = await WorkflowAsync(workflow.Id);
        Assert.Equal("ai/task-1", workflow.AgentBranch);
        Assert.Equal("c0ffee", workflow.HeadSha);

        // the PR publication carries the branch the run actually pushed, not a synthesized name
        var publish = Assert.Single((await AppendedAsync()).OfType<PublishPr>());
        Assert.Equal("ai/task-1", publish.HeadBranch);
        Assert.Equal("main", publish.BaseBranch); // fallback: no config entry
    }

    [Fact]
    public async Task Run_completed_without_git_facts_parks_the_task_and_skips_the_pr_publication()
    {
        var workflow = await RunCommandAsync(Command());

        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted(1, 1, "o", "r", 1, Branch: null, CommitSha: null), CancellationToken.None);

        Assert.DoesNotContain(await AppendedAsync(), e => e is PublishPr);
        var task = await TaskAsync(1);
        Assert.Equal(TaskStatuses.WaitingForHuman, task.Status);
        Assert.Contains(await AppendedAsync(), e => e is PublishComment); // the notify comment
    }

    [Fact]
    public async Task Fix_round_schedules_an_implement_run_on_the_recorded_agent_branch()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted(1, 1, "o", "r", 1, Branch: "ai/task-1", CommitSha: "c0ffee"), CancellationToken.None);
        await SettleActiveTaskAsync(workflow.Id); // the chained Review task frees the slot

        await _orchestrator.HandleReviewCommentsSubmittedAsync(
            new ReviewCommentsSubmitted("o", "r", 1, PrNumber: 1, RunId: 2, Comments: []), CancellationToken.None);

        var fixRun = (await AppendedAsync()).OfType<RunScheduled>().Last();
        Assert.Equal(TaskTypes.Implement, (await TaskAsync(fixRun.TaskId)).Type);
        Assert.Equal("ai/task-1", fixRun.Input.AgentBranch);
    }

    [Fact]
    public async Task Pr_updated_pins_the_head_revision_for_the_next_review_run()
    {
        var workflow = await RunCommandAsync(Command("/review"));
        var firstRun = Assert.Single((await AppendedAsync()).OfType<RunScheduled>());
        Assert.Null(firstRun.Input.ReviewHeadSha); // first round: the reviewer reads the PR itself

        // the reviewer's escalation parks the Task; new commits pin the revision; the reply resumes
        await _orchestrator.HandleEscalationAsync(
            new ReviewDecisionRequested("o", "r", 1, RunId: 1, Issue: "i", Context: "c", Options: []),
            "decision needed", CancellationToken.None);
        await _orchestrator.HandlePrUpdatedAsync(new PrUpdated("o", "r", 1, HeadSha: "cafe01"), CancellationToken.None);
        await _orchestrator.HandleUserReplyAsync(
            new UserReply("o", "r", 1, CommentId: 500, Author: "u", Text: "go on"), CancellationToken.None);

        var secondRun = (await AppendedAsync()).OfType<RunScheduled>().Last();
        Assert.Equal("cafe01", secondRun.Input.ReviewHeadSha);
        Assert.Equal(TaskTypes.Review, (await TaskAsync(secondRun.TaskId)).Type);
    }

    [Fact]
    public async Task Clarification_escalation_parks_the_task_and_stops_the_run()
    {
        var workflow = await RunCommandAsync(PlanCommand());

        await _orchestrator.HandleEscalationAsync(
            new ClarificationRequested("o", "r", 1, RunId: 1, Question: "which scope?", Context: null),
            "clarification needed", CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e => e is RunStopRequested);
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.WaitingForHuman, Assert.IsType<TaskRow>(active).Status);
    }

    [Fact]
    public async Task Second_command_is_rejected_while_task_occupies_the_slot()
    {
        await RunCommandAsync(Command());
        await _orchestrator.HandleUserCommandAsync(Command(), CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("one task at a time"));
    }

    [Fact]
    public async Task Low_permission_author_is_rejected()
    {
        await RunCommandAsync(Command(permission: "read"));

        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("permission"));
    }

    [Fact]
    public async Task Implement_requires_approved_plan_when_policy_demands_it()
    {
        var orchestrator = new Orchestrator(_state, _log,
            Options.Create(new OrchestrationOptions { PlanApprovalRequired = true }),
            NullLogger<Orchestrator>.Instance);
        await orchestrator.HandleUserCommandAsync(Command(), CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("/plan"));
    }

    [Fact]
    public async Task Plan_saved_publishes_the_plan_and_auto_approval_marks_it()
    {
        var workflow = await RunCommandAsync(PlanCommand());
        await _orchestrator.HandlePlanSavedAsync(new PlanSaved(
            "o", "r", 1, workflow.Id, Goals: "g", Scope: "s", AcceptanceCriteria: "a", Steps: JsonSerializer.SerializeToElement(new[] { "step" })), CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Plan, Body: var b } && b.Contains("Acceptance criteria"));
    }

    [Fact]
    public async Task Implement_completion_publishes_pr_and_chains_review()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted(1, 1, "o", "r", 1, Branch: "agent/1-fix", CommitSha: "c0ffee"), CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e => e is PublishPr { Kind: PublishPrKind.Create });
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskTypes.Review, Assert.IsType<TaskRow>(active).Type);
    }

    [Fact]
    public async Task Review_completion_publishes_approval_and_chains_testing()
    {
        var workflow = await RunCommandAsync(Command("/review"));
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted(1, 1, "o", "r", 1), CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e =>
            e is PublishReview { Verdict: "APPROVE" });
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskTypes.TestDesign, Assert.IsType<TaskRow>(active).Type);
    }

    [Fact]
    public async Task Run_failure_retries_then_parks_with_notification()
    {
        var workflow = await RunCommandAsync(Command());

        await _orchestrator.HandleRunFailedAsync(new RunFailed(1, 1, "o", "r", 1, Cause: "boom"), CancellationToken.None);
        await _orchestrator.HandleRunFailedAsync(new RunFailed(2, 1, "o", "r", 1, Cause: "boom"), CancellationToken.None);

        // 2 auto-retries done → third failure parks
        await _orchestrator.HandleRunFailedAsync(new RunFailed(3, 1, "o", "r", 1, Cause: "boom"), CancellationToken.None);
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.WaitingForHuman, Assert.IsType<TaskRow>(active).Status);
        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("failed"));
    }

    [Fact]
    public async Task Cancel_stops_the_workflow_and_its_runs()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleUserCancelAsync(new UserCancel("o", "r", 1, "u", Reason: "no"), CancellationToken.None);

        workflow = await WorkflowAsync(workflow.Id);
        Assert.Equal(WorkflowStatuses.Cancelled, workflow.Status);
        Assert.Contains(await AppendedAsync(), e => e is RunStopRequested);
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Null(active); // cancelled task leaves the slot
    }

    [Fact]
    public async Task Review_comments_start_a_fixing_round_until_the_cap()
    {
        var workflow = await RunCommandAsync(Command("/review"));
        for (var round = 1; round <= 3; round++)
        {
            await SettleActiveTaskAsync(workflow.Id); // the previous round's task frees the slot
            await _orchestrator.HandleReviewCommentsSubmittedAsync(
                new ReviewCommentsSubmitted("o", "r", 1, 1, RunId: round,
                    Comments: [new ReviewFinding("a.cs", 10, "major", "fix")]), CancellationToken.None);
        }

        // 3 rounds consumed (cap = 3): the 3rd created a fixing task that now occupies the slot;
        // a 4th submission hits the cap and parks
        await _orchestrator.HandleReviewCommentsSubmittedAsync(
            new ReviewCommentsSubmitted("o", "r", 1, 1, RunId: 4,
                Comments: [new ReviewFinding("a.cs", 10, "major", "fix")]), CancellationToken.None);

        workflow = await WorkflowAsync(workflow.Id);
        Assert.Equal(3, workflow.ReviewRounds);
        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("review_loop_exhausted"));
        Assert.Equal(4, (await AppendedAsync()).OfType<PublishReview>().Count());
    }

    [Fact]
    public async Task A_completed_run_does_not_chain_a_second_task_while_one_is_active()
    {
        var workflow = await RunCommandAsync(Command());
        var evt = new RunCompleted(1, 1, "o", "r", 1, Branch: "ai/task-1", CommitSha: "c0ffee");
        await _orchestrator.HandleRunCompletedAsync(evt, CancellationToken.None);
        var runsAfterFirst = (await AppendedAsync()).OfType<RunScheduled>().Count();

        // a replayed (or racing) completion must not start a parallel chain: the Review
        // task it chained occupies the slot
        await _orchestrator.HandleRunCompletedAsync(evt, CancellationToken.None);

        Assert.Equal(runsAfterFirst, (await AppendedAsync()).OfType<RunScheduled>().Count());
        Assert.Equal(TaskTypes.Review, Assert.IsType<TaskRow>(await ActiveTaskAsync(workflow.Id)).Type);
    }

    [Fact]
    public async Task Review_comments_with_an_active_task_start_no_fixing_round()
    {
        var workflow = await RunCommandAsync(Command()); // the Implement task occupies the slot
        var runsBefore = (await AppendedAsync()).OfType<RunScheduled>().Count();

        await _orchestrator.HandleReviewCommentsSubmittedAsync(
            new ReviewCommentsSubmitted("o", "r", 1, PrNumber: 1, RunId: 2, Comments: []), CancellationToken.None);

        workflow = await WorkflowAsync(workflow.Id);
        Assert.Equal(0, workflow.ReviewRounds); // the round was not consumed
        Assert.Equal(runsBefore, (await AppendedAsync()).OfType<RunScheduled>().Count());
        Assert.Equal(TaskTypes.Implement, Assert.IsType<TaskRow>(await ActiveTaskAsync(workflow.Id)).Type);
    }

    [Fact]
    public async Task A_parked_task_still_resumes_via_a_reply()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleEscalationAsync(
            new BlockerReported("o", "r", 1, RunId: 1, "db missing", "no db", ["provide db"]),
            "blocked", CancellationToken.None);
        var runsBefore = (await AppendedAsync()).OfType<RunScheduled>().Count();

        await _orchestrator.HandleUserReplyAsync(
            new UserReply("o", "r", 1, CommentId: 200, Author: "u", Text: "here"), CancellationToken.None);

        Assert.Equal(runsBefore + 1, (await AppendedAsync()).OfType<RunScheduled>().Count());
        Assert.Equal(TaskStatuses.Queued, Assert.IsType<TaskRow>(await ActiveTaskAsync(workflow.Id)).Status);
    }

    [Fact]
    public async Task Escalation_parks_and_a_reply_resumes_with_a_fresh_run()
    {
        var workflow = await RunCommandAsync(Command());
        var runCountBefore = (await AppendedAsync()).OfType<RunScheduled>().Count();

        await _orchestrator.HandleEscalationAsync(
            new BlockerReported("o", "r", 1, RunId: 1, "db missing", "no db", ["provide db"]),
            "blocked", CancellationToken.None);

        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.WaitingForHuman, Assert.IsType<TaskRow>(active).Status);
        Assert.Contains(await AppendedAsync(), e => e is RunStopRequested);

        await _orchestrator.HandleUserReplyAsync(new UserReply("o", "r", 1, 200, "u", "here"), CancellationToken.None);

        active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.Queued, Assert.IsType<TaskRow>(active).Status);
        Assert.Equal(runCountBefore + 1, (await AppendedAsync()).OfType<RunScheduled>().Count());
    }

    [Fact]
    public async Task Item_closes_the_workflow_and_stops_runs()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleItemClosedAsync(new ItemClosed("o", "r", 1), CancellationToken.None);

        workflow = await WorkflowAsync(workflow.Id);
        Assert.Equal(WorkflowStatuses.Closed, workflow.Status);
    }
}
