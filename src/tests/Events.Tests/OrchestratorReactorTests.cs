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
            _state, _log, _log, Options.Create(new OrchestrationOptions
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
        await using var uow = await _state.Factory.BeginAsync(null, CancellationToken.None);
        return await _state.GetWorkflowAsync(workflowId, uow, CancellationToken.None);
    }

    private async Task<TaskRow> TaskAsync(long taskId)
    {
        await using var uow = await _state.Factory.BeginAsync(null, CancellationToken.None);
        return await _state.GetTaskAsync(taskId, uow, CancellationToken.None);
    }

    private async Task<TaskRow?> ActiveTaskAsync(long workflowId)
    {
        await using var uow = await _state.Factory.BeginAsync(null, CancellationToken.None);
        return await _state.GetActiveTaskAsync(workflowId, uow, CancellationToken.None);
    }

    private async Task<WorkflowRow?> FindWorkflowScopedAsync(ItemRef item)
    {
        await using var uow = await _state.Factory.BeginAsync(null, CancellationToken.None);
        return await _state.FindWorkflowAsync(item, uow, CancellationToken.None);
    }

    private async Task<IReadOnlyList<IEvent>> AppendedAsync() => await _fx.AppendedAsync();

    private static UserCommand Command(string command = "/code", string permission = "write") =>
        new UserCommand { Command = command, Owner = "o", Repo = "r", ItemNumber = 1,
            CommentId = 100, Author = "u", AuthorPermission = permission, Text = command };

    private UserCommand PlanCommand() => Command("/plan");

    private async Task<WorkflowRow> RunCommandAsync(UserCommand cmd)
    {
        await _orchestrator.HandleUserCommandAsync(cmd, 0, CancellationToken.None);
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
        var orchestrator = new Orchestrator(_state, _log, _log,
            Options.Create(new OrchestrationOptions
            {
                PlanApprovalRequired = false,
                DefaultBranches = new Dictionary<string, string> { ["o/r"] = "develop" },
            }),
            NullLogger<Orchestrator>.Instance);
        await orchestrator.HandleUserCommandAsync(Command(), 0, CancellationToken.None);

        var scheduled = Assert.Single((await AppendedAsync()).OfType<RunScheduled>());
        Assert.Equal("develop", scheduled.Input.DefaultBranch);
    }

    [Fact]
    public async Task Run_completed_records_the_agent_branch_and_head_sha_as_workflow_pointers()
    {
        var workflow = await RunCommandAsync(Command());

        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Branch = "ai/task-1", CommitSha = "c0ffee" }, 0, CancellationToken.None);

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
            new RunCompleted { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Branch = null, CommitSha = null }, 0, CancellationToken.None);

        Assert.DoesNotContain(await AppendedAsync(), e => e is PublishPr);
        var task = await TaskAsync(1);
        Assert.Equal(TaskStatuses.WaitingForHuman, task.Status);
        Assert.Contains(await AppendedAsync(), e => e is PublishComment); // the notify comment
    }

    [Fact]
    public async Task Fix_round_schedules_an_implement_run_on_the_recorded_agent_branch()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleRunProcessingAsync(new RunProcessing { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Branch = "ai/task-1", CommitSha = "c0ffee" }, 0, CancellationToken.None);
        await _orchestrator.HandlePrPublishedAsync(new PrPublished { Owner = "o", Repo = "r", ItemNumber = 1, PrNumber = 28 }, 0, CancellationToken.None);

        await _log.AppendAsync(new ReviewCommentsSubmitted
        {
            Owner = "o",
            Repo = "r",
            ItemNumber = 1,
            PrNumber = 28,
            RunId = 2,
            Comments = [new ReviewFinding("a.cs", 10, "major", "fix")],
        }, CancellationToken.None);
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted { RunId = 2, TaskId = 2, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);

        var fixRun = (await AppendedAsync()).OfType<RunScheduled>().Last();
        Assert.Equal(TaskTypes.Implement, (await TaskAsync(fixRun.TaskId ?? 0)).Type);
        Assert.Equal("ai/task-1", fixRun.Input.AgentBranch); // the branch a fix round continues
    }

    [Fact]
    public async Task A_reviewer_runs_comment_events_aggregate_into_one_review_and_one_fixing_round()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleRunProcessingAsync(new RunProcessing { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Branch = "ai/task-1", CommitSha = "c0ffee" }, 0, CancellationToken.None);
        await _orchestrator.HandlePrPublishedAsync(new PrPublished { Owner = "o", Repo = "r", ItemNumber = 1, PrNumber = 28 }, 0, CancellationToken.None);
        var runsAfterReviewChained = (await AppendedAsync()).OfType<RunScheduled>().Count();

        await _log.AppendAsync(new ReviewCommentsSubmitted
        {
            Owner = "o",
            Repo = "r",
            ItemNumber = 1,
            PrNumber = 1,
            RunId = 2,
            Comments = [new ReviewFinding("a.cs", 10, "major", "first")],
        }, CancellationToken.None);
        await _log.AppendAsync(new ReviewCommentsSubmitted
        {
            Owner = "o",
            Repo = "r",
            ItemNumber = 1,
            PrNumber = 1,
            RunId = 2,
            Comments = [new ReviewFinding("b.cs", 20, "minor", "second")],
        }, CancellationToken.None);

        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted { RunId = 2, TaskId = 2, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);

        // ONE verdict carrying both events' comments — no per-event publish, no APPROVE
        var review = Assert.Single((await AppendedAsync()).OfType<PublishReview>());
        Assert.Equal("REQUEST_CHANGES", review.Verdict);
        Assert.Contains("first", review.Body);
        Assert.Contains("second", review.Body);

        // exactly ONE fixing round: one new scheduled run, an Implement task, one round spent
        var newRuns = (await AppendedAsync()).OfType<RunScheduled>().Skip(runsAfterReviewChained).ToList();
        var fixRun = Assert.Single(newRuns);
        Assert.Equal(TaskTypes.Implement, (await TaskAsync(fixRun.TaskId ?? 0)).Type);
        Assert.Equal("ai/task-1", fixRun.Input.AgentBranch);
        Assert.Equal(1, (await WorkflowAsync(workflow.Id)).ReviewRounds);
    }

    [Fact]
    public async Task Pr_updated_pins_the_head_revision_for_the_next_review_run()
    {
        var workflow = await RunCommandAsync(Command("/review"));
        var firstRun = Assert.Single((await AppendedAsync()).OfType<RunScheduled>());
        Assert.Null(firstRun.Input.ReviewHeadSha); // first round: the reviewer reads the PR itself

        // the reviewer's escalation parks the Task; new commits pin the revision; the reply resumes
        await _orchestrator.HandleEscalationAsync(
            new ReviewDecisionRequested { Owner = "o", Repo = "r", ItemNumber = 1, RunId = 1, Issue = "i", Context = "c", Options = [] },
            "decision needed", 0, CancellationToken.None);
        await _orchestrator.HandlePrUpdatedAsync(new PrUpdated { Owner = "o", Repo = "r", PrNumber = 1, HeadSha = "cafe01" }, 0, CancellationToken.None);
        await _orchestrator.HandleUserCommandAsync(Command("/resume"), 0, CancellationToken.None);

        var secondRun = (await AppendedAsync()).OfType<RunScheduled>().Last();
        Assert.Equal("cafe01", secondRun.Input.ReviewHeadSha);
        Assert.Equal(TaskTypes.Review, (await TaskAsync(secondRun.TaskId ?? 0)).Type);
    }

    [Fact]
    public async Task Pr_published_records_the_real_pr_number_on_the_workflow()
    {
        var workflow = await RunCommandAsync(Command());

        // no implement run completed yet — the publication completes nothing, it only pins
        await _orchestrator.HandlePrPublishedAsync(
            new PrPublished { Owner = "o", Repo = "r", ItemNumber = 1, PrNumber = 28 }, 0, CancellationToken.None);

        workflow = await WorkflowAsync(workflow.Id);
        Assert.Equal(28, workflow.HeadPrNumber);
        Assert.Single((await AppendedAsync()).OfType<RunScheduled>()); // no review chained
    }

    [Fact]
    public async Task Pr_published_completes_the_implement_task_and_chains_the_review()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleRunProcessingAsync(new RunProcessing { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Branch = "agent/1-fix", CommitSha = "c0ffee" }, 0, CancellationToken.None);

        await _orchestrator.HandlePrPublishedAsync(
            new PrPublished { Owner = "o", Repo = "r", ItemNumber = 1, PrNumber = 28 }, 0, CancellationToken.None);

        // the run's git facts pin the review head, the publication supplies the real PR
        // number — the review input carries both (the TODO's entry:56 failure had neither)
        var reviewRun = (await AppendedAsync()).OfType<RunScheduled>().Last();
        Assert.Equal(TaskTypes.Review, (await TaskAsync(reviewRun.TaskId ?? 0)).Type);
        Assert.Equal(28, reviewRun.Input.PrNumber);
        Assert.Equal("c0ffee", reviewRun.Input.ReviewHeadSha);
        Assert.Equal(TaskStatuses.Succeeded, (await TaskAsync(1)).Status);

        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted { RunId = 2, TaskId = reviewRun.TaskId, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);
        Assert.Contains((await AppendedAsync()).OfType<PublishReview>(),
            r => r.PrNumber == 28 && r.HeadBranch == "agent/1-fix");
    }

    [Fact]
    public async Task Review_run_falls_back_to_the_item_number_when_no_pr_is_recorded()
    {
        await RunCommandAsync(Command("/review"));

        var scheduled = Assert.Single((await AppendedAsync()).OfType<RunScheduled>());
        Assert.Equal(1, scheduled.Input.PrNumber); // ItemNumber: nothing published yet
    }

    [Fact]
    public async Task Clarification_escalation_parks_the_task_and_stops_the_run()
    {
        var workflow = await RunCommandAsync(PlanCommand());

        await _orchestrator.HandleEscalationAsync(
            new ClarificationRequested { Owner = "o", Repo = "r", ItemNumber = 1, RunId = 1, Question = "which scope?", Context = null },
            "clarification needed", 0, CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e => e is RunStopRequested);
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.WaitingForHuman, Assert.IsType<TaskRow>(active).Status);
    }

    [Fact]
    public async Task Second_command_is_rejected_while_task_occupies_the_slot()
    {
        await RunCommandAsync(Command());
        await _orchestrator.HandleUserCommandAsync(Command(), 0, CancellationToken.None);

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
        var orchestrator = new Orchestrator(_state, _log, _log,
            Options.Create(new OrchestrationOptions { PlanApprovalRequired = true }),
            NullLogger<Orchestrator>.Instance);
        await orchestrator.HandleUserCommandAsync(Command(), 0, CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("/plan"));
    }

    [Fact]
    public async Task Plan_saved_publishes_the_plan_and_auto_approval_marks_it()
    {
        var workflow = await RunCommandAsync(PlanCommand());
        await _orchestrator.HandlePlanSavedAsync(new PlanSaved
        {
            Owner = "o",
            Repo = "r",
            ItemNumber = 1,
            WorkflowId = workflow.Id,
            Goals = "g",
            Scope = "s",
            AcceptanceCriteria = "a",
            Steps = JsonSerializer.SerializeToElement(new[] { "step" }),
        }, 0, CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Plan, Body: var b } && b.Contains("Acceptance criteria"));
    }

    [Fact]
    public async Task Implement_completion_publishes_the_pr_and_holds_the_slot_until_publication()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleRunProcessingAsync(new RunProcessing { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Branch = "agent/1-fix", CommitSha = "c0ffee" }, 0, CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e => e is PublishPr { Kind: PublishPrKind.Create });
        // the PR is the implement Task's result: no review run until PrPublished, and the
        // implement Task keeps occupying the slot through the publication
        Assert.Single((await AppendedAsync()).OfType<RunScheduled>());
        var active = await ActiveTaskAsync(workflow.Id);
        var implement = Assert.IsType<TaskRow>(active);
        Assert.Equal(TaskTypes.Implement, implement.Type);
        Assert.Equal(TaskStatuses.Processing, implement.Status);
    }

    [Fact]
    public async Task A_permanent_pr_publication_failure_parks_the_implement_task()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleRunProcessingAsync(new RunProcessing { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Branch = "agent/1-fix", CommitSha = "c0ffee" }, 0, CancellationToken.None);

        await _orchestrator.HandlePublishFailedAsync(
            new PublishFailed { Owner = "o", Repo = "r", ItemNumber = 1, IdempotencyKey = "pr:1:coder:1", Cause = "gone" }, 0, CancellationToken.None);

        // processing is never a terminal state: the stranded Task parks for a human
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.WaitingForHuman, Assert.IsType<TaskRow>(active).Status);
        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("publication failed"));
    }

    [Fact]
    public async Task Review_completion_publishes_approval_and_chains_testing()
    {
        var workflow = await RunCommandAsync(Command("/review"));
        await _orchestrator.HandleRunCompletedAsync(
            new RunCompleted { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e =>
            e is PublishReview { Verdict: "APPROVE" });
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskTypes.TestDesign, Assert.IsType<TaskRow>(active).Type);
    }

    [Fact]
    public async Task Run_failure_retries_then_parks_with_notification()
    {
        var workflow = await RunCommandAsync(Command());

        await _orchestrator.HandleRunFailedAsync(new RunFailed { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Cause = "boom" }, 0, CancellationToken.None);
        await _orchestrator.HandleRunFailedAsync(new RunFailed { RunId = 2, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Cause = "boom" }, 0, CancellationToken.None);

        await _orchestrator.HandleRunFailedAsync(new RunFailed { RunId = 3, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Cause = "boom" }, 0, CancellationToken.None);
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.WaitingForHuman, Assert.IsType<TaskRow>(active).Status);
        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("failed"));
    }

    [Fact]
    public async Task Image_missing_failures_retry_silently_then_park_without_a_notification()
    {
        var workflow = await RunCommandAsync(Command());
        var cause = "image missing for profile coder: agent-coder:latest";

        await _orchestrator.HandleRunFailedAsync(new RunFailed { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Cause = cause }, 0, CancellationToken.None);
        await _orchestrator.HandleRunFailedAsync(new RunFailed { RunId = 2, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Cause = cause }, 0, CancellationToken.None);

        await _orchestrator.HandleRunFailedAsync(new RunFailed { RunId = 3, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Cause = cause }, 0, CancellationToken.None);
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.WaitingForHuman, Assert.IsType<TaskRow>(active).Status);
        Assert.DoesNotContain(await AppendedAsync(), e => e is PublishComment);
    }

    [Fact]
    public async Task A_silently_parked_task_resumes_via_the_resume_command()
    {
        var workflow = await RunCommandAsync(Command());
        var cause = "image missing for profile coder: agent-coder:latest";
        await _orchestrator.HandleRunFailedAsync(new RunFailed { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Cause = cause }, 0, CancellationToken.None);
        await _orchestrator.HandleRunFailedAsync(new RunFailed { RunId = 2, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Cause = cause }, 0, CancellationToken.None);
        await _orchestrator.HandleRunFailedAsync(new RunFailed { RunId = 3, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Cause = cause }, 0, CancellationToken.None);
        var runsBefore = (await AppendedAsync()).OfType<RunScheduled>().Count();

        await _orchestrator.HandleUserCommandAsync(Command("/resume"), 0, CancellationToken.None);

        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.Queued, Assert.IsType<TaskRow>(active).Status);
        Assert.Equal(runsBefore + 1, (await AppendedAsync()).OfType<RunScheduled>().Count());
    }

    [Fact]
    public async Task Cancel_stops_the_workflow_and_its_runs()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleUserCancelAsync(new UserCancel { Owner = "o", Repo = "r", ItemNumber = 1, Author = "u", Reason = "no" }, 0, CancellationToken.None);

        workflow = await WorkflowAsync(workflow.Id);
        Assert.Equal(WorkflowStatuses.Cancelled, workflow.Status);
        Assert.Contains(await AppendedAsync(), e => e is RunStopRequested);
        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Null(active); // cancelled task leaves the slot
    }

    [Fact]
    public async Task Review_comment_runs_start_a_fixing_round_until_the_cap()
    {
        var workflow = await RunCommandAsync(Command("/review"));
        long? reviewRunId = 1; // the /review command's review run
        for (var round = 1; round <= 4; round++)
        {
            await _log.AppendAsync(new ReviewCommentsSubmitted
            {
                Owner = "o",
                Repo = "r",
                ItemNumber = 1,
                PrNumber = 1,
                RunId = reviewRunId,
                Comments = [new ReviewFinding("a.cs", 10, "major", "fix")],
            },
                CancellationToken.None);
            await _orchestrator.HandleRunCompletedAsync(
                new RunCompleted
                  {
                  RunId = reviewRunId,
                  TaskId = (await AppendedAsync()).OfType<RunScheduled>()
                    .Single(s => s.RunId == reviewRunId).TaskId,
                  Owner = "o",
                  Repo = "r",
                  ItemNumber = 1,
                }, 0, CancellationToken.None);

            if (round < 4)
            {
                // the fixing round's Implement run completes; its PR publication completes
                // the task and chains the next review run
                var fixRun = (await AppendedAsync()).OfType<RunScheduled>().Last();
                await _orchestrator.HandleRunProcessingAsync(
                    new RunProcessing { RunId = fixRun.RunId, TaskId = fixRun.TaskId, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);
                await _orchestrator.HandleRunCompletedAsync(
                    new RunCompleted
                    {
                        RunId = fixRun.RunId,
                        TaskId = fixRun.TaskId,
                        Owner = "o",
                        Repo = "r",
                        ItemNumber = 1,
                        Branch = "agent/1-fix",
                        CommitSha = "c0ffee",
                    }, 0, CancellationToken.None);
                await _orchestrator.HandlePrPublishedAsync(
                    new PrPublished { Owner = "o", Repo = "r", ItemNumber = 1, PrNumber = 28 }, 0, CancellationToken.None);
                reviewRunId = (await AppendedAsync()).OfType<RunScheduled>().Last().RunId;
            }
        }

        // 3 rounds consumed (cap = 3); the 4th reviewer run still publishes its aggregated
        // verdict, but its fixing round hits the cap and parks
        workflow = await WorkflowAsync(workflow.Id);
        Assert.Equal(3, workflow.ReviewRounds);
        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("review_loop_exhausted"));
        var reviews = (await AppendedAsync()).OfType<PublishReview>().ToList();
        Assert.Equal(4, reviews.Count);
        Assert.All(reviews, r => Assert.Equal("REQUEST_CHANGES", r.Verdict));
    }

    [Fact]
    public async Task A_completed_run_does_not_chain_a_second_task_while_one_is_active()
    {
        var workflow = await RunCommandAsync(Command());
        var evt = new RunCompleted { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1, Branch = "ai/task-1", CommitSha = "c0ffee" };
        await _orchestrator.HandleRunProcessingAsync(new RunProcessing { RunId = 1, TaskId = 1, Owner = "o", Repo = "r", ItemNumber = 1 }, 0, CancellationToken.None);
        await _orchestrator.HandleRunCompletedAsync(evt, 0, CancellationToken.None);
        var runsAfterFirst = (await AppendedAsync()).OfType<RunScheduled>().Count();

        // a replayed (or racing) completion re-announces nothing new: the implement Task
        // is still processing on its PR publication
        await _orchestrator.HandleRunCompletedAsync(evt, 0, CancellationToken.None);

        Assert.Equal(runsAfterFirst, (await AppendedAsync()).OfType<RunScheduled>().Count());
        var active = await ActiveTaskAsync(workflow.Id);
        var implement = Assert.IsType<TaskRow>(active);
        Assert.Equal(TaskTypes.Implement, implement.Type);
        Assert.Equal(TaskStatuses.Processing, implement.Status);

        // the publication completes the Task exactly once: a replayed PrPublished chains nothing
        await _orchestrator.HandlePrPublishedAsync(new PrPublished { Owner = "o", Repo = "r", ItemNumber = 1, PrNumber = 28 }, 0, CancellationToken.None);
        var runsAfterPublish = (await AppendedAsync()).OfType<RunScheduled>().Count();
        await _orchestrator.HandlePrPublishedAsync(new PrPublished { Owner = "o", Repo = "r", ItemNumber = 1, PrNumber = 28 }, 0, CancellationToken.None);

        Assert.Equal(runsAfterPublish, (await AppendedAsync()).OfType<RunScheduled>().Count());
        Assert.Equal(TaskTypes.Review, Assert.IsType<TaskRow>(await ActiveTaskAsync(workflow.Id)).Type);
    }

    [Fact]
    public async Task Review_comment_events_change_nothing_until_the_reviewer_run_completes()
    {
        var workflow = await RunCommandAsync(Command()); // the Implement task occupies the slot
        var runsBefore = (await AppendedAsync()).OfType<RunScheduled>().Count();

        // the comment handler only lets the delivery loop ack the event (Block 3.3b):
        // no publish, no fixing round, no round spent — aggregation happens at completion
        await _orchestrator.HandleReviewCommentsSubmittedAsync(
            new ReviewCommentsSubmitted { Owner = "o", Repo = "r", ItemNumber = 1, PrNumber = 1, RunId = 2, Comments = [] }, 0, CancellationToken.None);

        workflow = await WorkflowAsync(workflow.Id);
        Assert.Equal(0, workflow.ReviewRounds);
        Assert.Equal(runsBefore, (await AppendedAsync()).OfType<RunScheduled>().Count());
        Assert.DoesNotContain(await AppendedAsync(), e => e is PublishReview);
        Assert.Equal(TaskTypes.Implement, Assert.IsType<TaskRow>(await ActiveTaskAsync(workflow.Id)).Type);
    }

    [Fact]
    public async Task A_parked_task_resumes_via_the_resume_command()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleEscalationAsync(
            new BlockerReported { Owner = "o", Repo = "r", ItemNumber = 1, RunId = 1, Description = "db missing", Cause = "no db", Options = ["provide db"] },
            "blocked", 0, CancellationToken.None);
        var runsBefore = (await AppendedAsync()).OfType<RunScheduled>().Count();

        await _orchestrator.HandleUserCommandAsync(Command("/resume"), 0, CancellationToken.None);

        Assert.Equal(runsBefore + 1, (await AppendedAsync()).OfType<RunScheduled>().Count());
        Assert.Equal(TaskStatuses.Queued, Assert.IsType<TaskRow>(await ActiveTaskAsync(workflow.Id)).Status);
    }

    [Fact]
    public async Task Escalation_parks_and_the_resume_command_resumes_with_a_fresh_run()
    {
        var workflow = await RunCommandAsync(Command());
        var runCountBefore = (await AppendedAsync()).OfType<RunScheduled>().Count();

        await _orchestrator.HandleEscalationAsync(
            new BlockerReported { Owner = "o", Repo = "r", ItemNumber = 1, RunId = 1, Description = "db missing", Cause = "no db", Options = ["provide db"] },
            "blocked", 0, CancellationToken.None);

        var active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.WaitingForHuman, Assert.IsType<TaskRow>(active).Status);
        Assert.Contains(await AppendedAsync(), e => e is RunStopRequested);

        await _orchestrator.HandleUserCommandAsync(Command("/resume"), 0, CancellationToken.None);

        active = await ActiveTaskAsync(workflow.Id);
        Assert.Equal(TaskStatuses.Queued, Assert.IsType<TaskRow>(active).Status);
        Assert.Equal(runCountBefore + 1, (await AppendedAsync()).OfType<RunScheduled>().Count());
    }

    [Fact]
    public async Task Resume_without_a_parked_task_is_rejected()
    {
        await RunCommandAsync(Command()); // the Implement task is processing

        await _orchestrator.HandleUserCommandAsync(Command("/resume"), 0, CancellationToken.None);

        Assert.Contains(await AppendedAsync(), e =>
            e is PublishComment { Kind: PublishCommentKind.Notification, Body: var b } && b.Contains("nothing to resume"));
    }

    [Fact]
    public async Task Item_closes_the_workflow_and_stops_runs()
    {
        var workflow = await RunCommandAsync(Command());
        await _orchestrator.HandleItemClosedAsync(new ItemClosed { Owner = "o", Repo = "r", Number = 1 }, 0, CancellationToken.None);

        workflow = await WorkflowAsync(workflow.Id);
        Assert.Equal(WorkflowStatuses.Closed, workflow.Status);
    }
}
