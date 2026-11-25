using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PiAgent.Events;
using PiAgent.Events.InMemory;
using PiAgent.Orchestration;
using PiAgent.Orchestration.State;
using PiAgent.Orchestration.State.InMemory;
using Xunit;

namespace PiAgent.Events.Tests;

/// <summary>
/// The Orchestrator reactor against in-memory state + in-memory log: admission, the
/// Workflow/Task/Run machine, the flow chain, policies, escalation and cancellation
/// (docs/orchestrator.md).
/// </summary>
public sealed class OrchestratorReactorTests
{
    private readonly InMemoryEventStore _log = new();
    private readonly InMemoryOrchestrationState _state = new();
    private readonly Orchestrator _orchestrator;

    public OrchestratorReactorTests()
    {
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

    private static UserCommand Command(string command = "/code", string permission = "write") =>
        new(command, "o", "r", 1, CommentId: 100, Author: "u", permission, Text: command);

    private UserCommand PlanCommand() => Command("/plan");

    private async Task<WorkflowRow> RunCommandAsync(UserCommand cmd)
    {
        await _orchestrator.HandleUserCommandAsync(cmd, CancellationToken.None);
        return await _state.FindWorkflowAsync(new ItemRef(cmd.Owner, cmd.Repo, cmd.ItemNumber), null!, CancellationToken.None)
               ?? throw new InvalidOperationException("workflow missing");
    }

    [Fact]
    public async Task Command_admits_to_workflow_task_and_scheduled_run()
    {
        var workflow = await RunCommandAsync(Command());

        Assert.Equal(WorkflowStatuses.Active, workflow.Status);
        var active = await _state.GetActiveTaskAsync(workflow.Id, null!, CancellationToken.None);
        Assert.Equal(TaskTypes.Implement, Assert.IsType<TaskRow>(active).Type);
        Assert.Contains(_log.Appended, e => e is RunScheduled s && s.Input.Profile == "coder");
    }

    [Fact]
    public async Task Scheduled_input_carries_the_v2_pointers_on_the_planner_path()
    {
        await RunCommandAsync(PlanCommand());

        var scheduled = Assert.Single(_log.Appended.OfType<RunScheduled>());
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

        var scheduled = Assert.Single(_log.Appended.OfType<RunScheduled>());
        Assert.Equal("develop", scheduled.Input.DefaultBranch);
    }

    [Fact]
    public async Task Run_completed_records_the_agent_branch_and_head_sha_as_workflow_pointers()
    {
        var workflow = await RunCommandAsync(Command());

        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted(1, 1, "o", "r", 1, Branch: "ai/task-1", CommitSha: "c0ffee"), CancellationToken.None);

        workflow = await _state.GetWorkflowAsync(workflow.Id, null!, CancellationToken.None);
        Assert.Equal("ai/task-1", workflow.AgentBranch);
        Assert.Equal("c0ffee", workflow.HeadSha);
    }

    [Fact]
    public async Task Fix_round_schedules_an_implement_run_on_the_recorded_agent_branch()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted(1, 1, "o", "r", 1, Branch: "ai/task-1", CommitSha: "c0ffee"), CancellationToken.None);

        await _orchestrator.HandleReviewCommentsSubmittedAsync(
            new ReviewCommentsSubmitted("o", "r", 1, PrNumber: 1, RunId: 2, Comments: []), CancellationToken.None);

        var fixRun = _log.Appended.OfType<RunScheduled>().Last();
        Assert.Equal(TaskTypes.Implement, (await _state.GetTaskAsync(fixRun.TaskId, null!, CancellationToken.None)).Type);
        Assert.Equal("ai/task-1", fixRun.Input.AgentBranch);
    }

    [Fact]
    public async Task Pr_updated_pins_the_head_revision_for_the_next_review_run()
    {
        var workflow = await RunCommandAsync(Command("/review"));
        var firstRun = Assert.Single(_log.Appended.OfType<RunScheduled>());
        Assert.Null(firstRun.Input.ReviewHeadSha); // first round: the reviewer reads the PR itself

        // the reviewer's escalation parks the Task; new commits pin the revision; the reply resumes
        await _orchestrator.HandleEscalationAsync(
            new ReviewDecisionRequested("o", "r", 1, RunId: 1, Issue: "i", Context: "c", Options: []),
            "decision needed", CancellationToken.None);
        await _orchestrator.HandlePrUpdatedAsync(new PrUpdated("o", "r", 1, HeadSha: "cafe01"), CancellationToken.None);
        await _orchestrator.HandleUserReplyAsync(
            new UserReply("o", "r", 1, CommentId: 500, Author: "u", Text: "go on"), CancellationToken.None);

        var secondRun = _log.Appended.OfType<RunScheduled>().Last();
        Assert.Equal("cafe01", secondRun.Input.ReviewHeadSha);
        Assert.Equal(TaskTypes.Review, (await _state.GetTaskAsync(secondRun.TaskId, null!, CancellationToken.None)).Type);
    }

    [Fact]
    public async Task Second_command_is_rejected_while_task_occupies_the_slot()
    {
        await RunCommandAsync(Command());
        await _orchestrator.HandleUserCommandAsync(Command(), CancellationToken.None);

        Assert.Contains(_log.Appended, e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("one task at a time"));
    }

    [Fact]
    public async Task Low_permission_author_is_rejected()
    {
        await RunCommandAsync(Command(permission: "read"));

        Assert.Contains(_log.Appended, e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("permission"));
    }

    [Fact]
    public async Task Implement_requires_approved_plan_when_policy_demands_it()
    {
        var orchestrator = new Orchestrator(_state, _log,
            Options.Create(new OrchestrationOptions { PlanApprovalRequired = true }),
            NullLogger<Orchestrator>.Instance);
        await orchestrator.HandleUserCommandAsync(Command(), CancellationToken.None);

        Assert.Contains(_log.Appended, e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("/plan"));
    }

    [Fact]
    public async Task Plan_saved_publishes_the_plan_and_auto_approval_marks_it()
    {
        var workflow = await RunCommandAsync(PlanCommand());
        await _orchestrator.HandlePlanSavedAsync(new PlanSaved(
            "o", "r", 1, workflow.Id, Goals: "g", Scope: "s", AcceptanceCriteria: "a"), CancellationToken.None);

        Assert.Contains(_log.Appended, e =>
            e is PublishComment { Kind: PublishCommentKind.Plan, Body: var b } && b.Contains("Acceptance criteria"));
    }

    [Fact]
    public async Task Implement_completion_publishes_pr_and_chains_review()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted(1, 1, "o", "r", 1), CancellationToken.None);

        Assert.Contains(_log.Appended, e => e is PublishPr { Kind: PublishPrKind.Create });
        var active = await _state.GetActiveTaskAsync(workflow.Id, null!, CancellationToken.None);
        Assert.Equal(TaskTypes.Review, Assert.IsType<TaskRow>(active).Type);
    }

    [Fact]
    public async Task Review_completion_publishes_approval_and_chains_testing()
    {
        var workflow = await RunCommandAsync(Command("/review"));
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted(1, 1, "o", "r", 1), CancellationToken.None);

        Assert.Contains(_log.Appended, e =>
            e is PublishReview { Verdict: "APPROVE" });
        var active = await _state.GetActiveTaskAsync(workflow.Id, null!, CancellationToken.None);
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
        var active = await _state.GetActiveTaskAsync(workflow.Id, null!, CancellationToken.None);
        Assert.Equal(TaskStatuses.WaitingForHuman, Assert.IsType<TaskRow>(active).Status);
        Assert.Contains(_log.Appended, e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("failed"));
    }

    [Fact]
    public async Task Cancel_stops_the_workflow_and_its_runs()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleUserCancelAsync(new UserCancel("o", "r", 1, "u", Reason: "no"), CancellationToken.None);

        workflow = await _state.GetWorkflowAsync(workflow.Id, null!, CancellationToken.None);
        Assert.Equal(WorkflowStatuses.Cancelled, workflow.Status);
        Assert.Contains(_log.Appended, e => e is RunStopRequested);
        var active = await _state.GetActiveTaskAsync(workflow.Id, null!, CancellationToken.None);
        Assert.Null(active); // cancelled task leaves the slot
    }

    [Fact]
    public async Task Review_comments_start_a_fixing_round_until_the_cap()
    {
        var workflow = await RunCommandAsync(Command("/review"));
        for (var round = 1; round <= 3; round++)
        {
            await _orchestrator.HandleReviewCommentsSubmittedAsync(
                new ReviewCommentsSubmitted("o", "r", 1, 1, RunId: round,
                    Comments: [new ReviewFinding("a.cs", 10, "major", "fix")]), CancellationToken.None);
        }

        // 3 rounds consumed (cap = 3): the 3rd created a fixing task that now occupies the slot;
        // a 4th submission hits the cap and parks
        await _orchestrator.HandleReviewCommentsSubmittedAsync(
            new ReviewCommentsSubmitted("o", "r", 1, 1, RunId: 4,
                Comments: [new ReviewFinding("a.cs", 10, "major", "fix")]), CancellationToken.None);

        workflow = await _state.GetWorkflowAsync(workflow.Id, null!, CancellationToken.None);
        Assert.Equal(3, workflow.ReviewRounds);
        Assert.Contains(_log.Appended, e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("review_loop_exhausted"));
        Assert.Equal(4, _log.Appended.OfType<PublishReview>().Count());
    }

    [Fact]
    public async Task Escalation_parks_and_a_reply_resumes_with_a_fresh_run()
    {
        var workflow = await RunCommandAsync(Command());
        var runCountBefore = _log.Appended.OfType<RunScheduled>().Count();

        await _orchestrator.HandleEscalationAsync(
            new BlockerReported("o", "r", 1, RunId: 1, "db missing", "no db", ["provide db"]),
            "blocked", CancellationToken.None);

        var active = await _state.GetActiveTaskAsync(workflow.Id, null!, CancellationToken.None);
        Assert.Equal(TaskStatuses.WaitingForHuman, Assert.IsType<TaskRow>(active).Status);
        Assert.Contains(_log.Appended, e => e is RunStopRequested);

        await _orchestrator.HandleUserReplyAsync(new UserReply("o", "r", 1, 200, "u", "here"), CancellationToken.None);

        active = await _state.GetActiveTaskAsync(workflow.Id, null!, CancellationToken.None);
        Assert.Equal(TaskStatuses.Queued, Assert.IsType<TaskRow>(active).Status);
        Assert.Equal(runCountBefore + 1, _log.Appended.OfType<RunScheduled>().Count());
    }

    [Fact]
    public async Task Item_closes_the_workflow_and_stops_runs()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleItemClosedAsync(new ItemClosed("o", "r", 1), CancellationToken.None);

        workflow = await _state.GetWorkflowAsync(workflow.Id, null!, CancellationToken.None);
        Assert.Equal(WorkflowStatuses.Closed, workflow.Status);
    }
}
