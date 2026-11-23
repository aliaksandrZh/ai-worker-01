using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Orchestrator;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

namespace PiAgent.Hosts.Tests.Commands;

/// <summary>
/// Control-command replies: /status renders the status
/// format from current rows including `Run attempt: #N` and the queue line; a WorkItem without a
/// Workflow is answered "No agent workflow started."; /help hides commands the pipeline would reject
/// right now (target type, pause, routing/profile gate incl. not-ready versions,
/// Workflow state: unfinished Task, failed Workflow, no last-failed Task for /retry). Every triggering
/// comment gets exactly one Outbox reply; Workflow/Task/Run rows are not touched; /stop and /retry
/// stay untouched here.
/// </summary>
public class ControlCommandTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // ---- /status ----

    [Fact]
    public async Task StatusWithoutWorkflow_RepliesNothingStarted_CreatesNothing()
    {
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz /status");
        AuthStub200(harness.Handler);
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, command.Status);
        Assert.Null(command.RejectionReason);
        Assert.Contains("No agent workflow started.", (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload, StringComparison.Ordinal);
        // /status creates no Workflow, Task, Run, Session or Container
        Assert.Empty(await db.Workflows.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrchestrationTasks.AsNoTracking().ToListAsync());
        Assert.Empty(await db.Runs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StatusWithRunningTask_RendersDraftFormatAndLeavesRowsUntouched()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /status");
        AuthStub200(harness.Handler);
        // admitted plan task (running, claim path of task 07) + prepared run attempt #1
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version);
        // simulated claim+runtime progression: run running, the task's job already claimed, container observed
        await db.ContainerInstances.ExecuteUpdateAsync(u => u
            .SetProperty(c => c.ObservedState, ContainerObservedStates.Running));
        await db.Runs.ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Status, RunStatuses.Running)
            .SetProperty(r => r.AttemptNumber, 2)
            .SetProperty(r => r.StartedAt, DateTime.UtcNow.AddMinutes(-18))
            .SetProperty(r => r.LastHeartbeatAt, DateTime.UtcNow.AddSeconds(-20))
            .SetProperty(r => r.LastProgressAt, DateTime.UtcNow.AddSeconds(-45)));
        await db.OrchestrationTasks.ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Running));
        await db.OrchestrationJobs.ExecuteUpdateAsync(u => u
            .SetProperty(j => j.Status, OrchestrationJobStatuses.Processing));
        db.ChangeTracker.Clear();

        var beforeTasks = await db.OrchestrationTasks.AsNoTracking().CountAsync();
        var beforeRuns = await db.Runs.AsNoTracking().CountAsync();
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, command.Status);
        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        // format pieces: workflow/task line with Run attempt: #N, heartbeat, queue
        Assert.Contains("Workflow Status", payload, StringComparison.Ordinal);
        Assert.Contains($"WorkItem: Issue #{workItem.Number}", payload, StringComparison.Ordinal);
        Assert.Contains("Workflow: active", payload, StringComparison.Ordinal);
        Assert.Contains("Type: plan | Status: running | Agent:", payload, StringComparison.Ordinal);
        Assert.Contains("Run attempt: #2", payload, StringComparison.Ordinal);
        Assert.Contains("Container: running", payload, StringComparison.Ordinal);
        Assert.Contains("Last heartbeat: 20s ago | Last progress: 45s ago", payload, StringComparison.Ordinal);
        Assert.Contains("Queue:", payload, StringComparison.Ordinal);
        // the claimed (processing) job occupies no queue position; nothing else waits system-wide
        Assert.Contains("Position: 0 | Pending jobs: 0", payload, StringComparison.Ordinal);
        // rows untouched by the status roundtrip
        Assert.Equal(beforeTasks, await db.OrchestrationTasks.AsNoTracking().CountAsync());
        Assert.Equal(beforeRuns, await db.Runs.AsNoTracking().CountAsync());
        Assert.Equal(OrchestrationTaskStatuses.Running,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id)).Status);
    }

    [Fact]
    public async Task StatusDuringRun_PhaseMirrorsTheLatestRunEvent_NeverTheHeartbeat()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /status");
        AuthStub200(harness.Handler);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version);
        await db.Runs.ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Status, RunStatuses.Running)
            .SetProperty(r => r.StartedAt, DateTime.UtcNow.AddMinutes(-3))
            .SetProperty(r => r.LastHeartbeatAt, DateTime.UtcNow.AddSeconds(-10))
            .SetProperty(r => r.LastProgressAt, DateTime.UtcNow.AddSeconds(-40)));
        await db.OrchestrationTasks.ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Running));
        // the RunEvent stream: the latest NON-heartbeat event is the phase (mirror the event type,
        // no extra enum); a newer heartbeat must not shift it
        db.RunEvents.Add(new RunEvent
        {
            Id = Guid.NewGuid(), RunId = prepared.Run!.Id, EventId = "evt-1", Type = RunEventTypes.AgentStarted,
            OccurredAt = DateTime.UtcNow.AddSeconds(-90), ReceivedAt = DateTime.UtcNow.AddSeconds(-90),
        });
        db.RunEvents.Add(new RunEvent
        {
            Id = Guid.NewGuid(), RunId = prepared.Run.Id, EventId = "evt-2",
            Type = RunEventTypes.ToolStarted,
            Payload = """{"toolCallId":"c1","toolName":"mcp__github__add_issue_comment"}""",
            OccurredAt = DateTime.UtcNow.AddSeconds(-40), ReceivedAt = DateTime.UtcNow.AddSeconds(-40),
        });
        db.RunEvents.Add(new RunEvent
        {
            Id = Guid.NewGuid(), RunId = prepared.Run.Id, EventId = "hb-1", Type = RunEventTypes.Heartbeat,
            OccurredAt = DateTime.UtcNow.AddSeconds(-10), ReceivedAt = DateTime.UtcNow.AddSeconds(-10),
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        // the phase mirrors the latest NON-heartbeat event type verbatim (no extra vocabulary)
        Assert.Contains("Phase: tool_started", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Phase: heartbeat", payload, StringComparison.Ordinal);
        // heartbeat/progress facts stay in the Execution line
        Assert.Contains("Last heartbeat: 10s ago", payload, StringComparison.Ordinal);
        Assert.Contains("Last progress: 40s ago", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusWithFailedWorkflow_ShowsReasonAndNoStartedReply()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /status");
        AuthStub200(harness.Handler);
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id,
            Status = WorkflowStatuses.Failed, FailureReason = "unrecoverable",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("Workflow: failed (unrecoverable)", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusWithCompletedTask_ShowsLastCompletedSection()
    {
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz /status");
        AuthStub200(harness.Handler);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, db.WorkItems.AsNoTracking().Single(), version);
        await db.OrchestrationTasks.ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Succeeded)
            .SetProperty(t => t.CompletedAt, DateTime.UtcNow.AddMinutes(-5)));
        await db.Workflows.ExecuteUpdateAsync(u => u
            .SetProperty(w => w.Status, WorkflowStatuses.WaitingForHuman)
            .SetProperty(w => w.CurrentTaskId, (Guid?)null));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("Last completed Task:", payload, StringComparison.Ordinal);
        Assert.Contains("- plan — succeeded", payload, StringComparison.Ordinal);
        // no Current Task section when nothing is unfinished
        Assert.DoesNotContain("Current Task:", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusWithQueuedTaskAfterOlderFailedTask_ShowsBothWithoutRunChatter()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /status");
        AuthStub200(harness.Handler);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id,
            Status = WorkflowStatuses.Active, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        var older = new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Sequence = 1, Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id, Status = OrchestrationTaskStatuses.Failed,
            TriggerCommandId = CommandTestSupport.SeedTriggerCommand(db, workItem).Id,
            RetryPolicy = "{}", FailureReason = AgentCommandRejectionReasons.AgentImageNotReady,
            CreatedAt = DateTime.UtcNow,
        };
        var current = new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Sequence = 2, Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id, Status = OrchestrationTaskStatuses.Queued,
            TriggerCommandId = CommandTestSupport.SeedTriggerCommand(db, workItem).Id,
            RetryPolicy = "{}", CreatedAt = DateTime.UtcNow,
        };
        db.OrchestrationTasks.AddRange(older, current);
        workflow.CurrentTaskId = current.Id;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("Type: plan | Status: queued", payload, StringComparison.Ordinal);
        Assert.Contains("Last completed Task:", payload, StringComparison.Ordinal);
        Assert.Contains("- plan — failed (agent_image_not_ready)", payload, StringComparison.Ordinal);
        // no Run yet → no attempt/heartbeat/container chatter (sections omitted when inapplicable)
        Assert.DoesNotContain("Run attempt", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Execution:", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusQueuePosition_IsPendingJobsAheadOfOursInFifo()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /status");
        AuthStub200(harness.Handler);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        // first job (other work item, same repo) enters the FIFO earlier; ours is available a minute later
        var repo = await db.Repositories.AsNoTracking().SingleAsync();
        var otherWorkItem = (await OrchestrationTestSupport.SeedWorkItemAsync(db, repo)).WorkItem;
        await OrchestrationTestSupport.SeedQueuedAsync(
            db, otherWorkItem, version, availableAt: DateTime.UtcNow.AddMinutes(-1));
        await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, availableAt: DateTime.UtcNow);
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("Position: 1 | Pending jobs: 2", payload, StringComparison.Ordinal);
    }

    // ---- /help ----

    [Fact]
    public async Task HelpOnIssue_WithPlannerReady_ListsIssueCommands()
    {
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz /help");
        AuthStub200(harness.Handler);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan); // planner ready
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, command.Status);
        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("Available commands for this issue:", payload, StringComparison.Ordinal);
        Assert.Contains("`/plan`", payload, StringComparison.Ordinal);
        Assert.Contains("`/status`", payload, StringComparison.Ordinal);
        Assert.Contains("`/stop`", payload, StringComparison.Ordinal);
        Assert.Contains("`/help`", payload, StringComparison.Ordinal);
        // profile-gated/phase-gated (no rules exist for them) and state-gated (no failed Task)
        Assert.DoesNotContain("/code", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/review", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/test-cases", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/retry", payload, StringComparison.Ordinal);
        // creates nothing
        Assert.Empty(await db.Workflows.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Help_OmitsNotReadyVersion()
    {
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz /help");
        AuthStub200(harness.Handler);
        await CommandTestSupport.SeedProfileAsync(
            db, OrchestrationTaskTypes.Plan, versionState: ProfileVersionStates.Draft);
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.DoesNotContain("/plan", payload, StringComparison.Ordinal);
        Assert.Contains("`/status`", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpOnPausedRepository_OmitsTaskProducing_CommandStillProcessed()
    {
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz /help", enabled: false);
        AuthStub200(harness.Handler);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var version = await db.AgentProfileVersions.AsNoTracking().SingleAsync();
        // a failed last Task + pinned ready version would normally list /retry — the pause hides it
        var workItem = await db.WorkItems.AsNoTracking().SingleAsync();
        var failedWorkflow = new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, Status = WorkflowStatuses.WaitingForHuman,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(failedWorkflow);
        db.OrchestrationTasks.Add(new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = failedWorkflow.Id, Sequence = 1, Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id, Status = OrchestrationTaskStatuses.Failed,
            TriggerCommandId = CommandTestSupport.SeedTriggerCommand(db, workItem).Id,
            RetryPolicy = "{}", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, command.Status); // control commands unaffected by the pause
        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.DoesNotContain("/plan", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/retry", payload, StringComparison.Ordinal);
        Assert.Contains("`/status`", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpWithUnfinishedTask_OmitsTaskProducingCommands()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /help");
        AuthStub200(harness.Handler);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version); // queued task occupies the Workflow
        db.ChangeTracker.Clear();
        Assert.True(await db.OrchestrationTasks.AnyAsync());

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.DoesNotContain("/plan", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/retry", payload, StringComparison.Ordinal);
        Assert.Contains("`/status`", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpAfterSuccessfulPlan_ListsPlanAgain()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /help");
        AuthStub200(harness.Handler);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version);
        // simulated completed plan round-trip: task terminal + plan artifact + workflow waiting
        await db.OrchestrationTasks.ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Succeeded)
            .SetProperty(t => t.CompletedAt, DateTime.UtcNow));
        db.Artifacts.Add(new Artifact
        {
            Id = Guid.NewGuid(), TaskId = prepared.Task.Id, WorkflowId = prepared.Workflow.Id,
            Type = ArtifactTypes.Plan, Format = ArtifactFormats.Markdown, Content = "plan",
            CreatedAt = DateTime.UtcNow,
        });
        await db.Workflows.ExecuteUpdateAsync(u => u
            .SetProperty(w => w.Status, WorkflowStatuses.WaitingForHuman)
            .SetProperty(w => w.CurrentTaskId, (Guid?)null));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("`/plan`", payload, StringComparison.Ordinal);
        Assert.Contains("`/status`", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpOnPullRequest_ListsReviewOnlyWithRule()
    {
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz /help", pullRequest: true);
        AuthStub200(harness.Handler);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Review);
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("Available commands for this pull request:", payload, StringComparison.Ordinal);
        Assert.Contains("`/review`", payload, StringComparison.Ordinal);
        // Issue-only commands invalid for a PR target
        Assert.DoesNotContain("`/plan`", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/code", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/test-cases", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HelpWithFailedTask_ListsRetryAndPlan()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /help");
        AuthStub200(harness.Handler);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id,
            Status = WorkflowStatuses.WaitingForHuman, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        db.OrchestrationTasks.Add(new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Sequence = 1, Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id, Status = OrchestrationTaskStatuses.Failed,
            TriggerCommandId = CommandTestSupport.SeedTriggerCommand(db, workItem).Id,
            RetryPolicy = "{}", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("`/retry`", payload, StringComparison.Ordinal);
        Assert.Contains("`/plan`", payload, StringComparison.Ordinal); // the Workflow is free again
    }

    [Fact]
    public async Task HelpWithFailedWorkflow_ListsOnlyControlCommands()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /help");
        AuthStub200(harness.Handler);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id,
            Status = WorkflowStatuses.Failed, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        db.OrchestrationTasks.Add(new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Sequence = 1, Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id, Status = OrchestrationTaskStatuses.Failed,
            TriggerCommandId = CommandTestSupport.SeedTriggerCommand(db, workItem).Id,
            RetryPolicy = "{}", CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("`/status`", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/plan", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/retry", payload, StringComparison.Ordinal);
    }

    // ---- untouched /stop + reply dedupe ----

    [Fact]
    public async Task StopCommand_IsHandledAsAControlCommand_NothingToStopWithoutWorkflow()
    {
        // /stop is a state-changing control command (StopCommandHandler); on this
        // workflow-less surface it answers "nothing to stop" and still creates no rows
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz /stop");
        AuthStub200(harness.Handler);
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, command.Status);
        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("nothing to stop", payload, StringComparison.Ordinal);
        Assert.Empty(await db.Workflows.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task ExactlyOneReply_PerCommentId_ReplayAddsNothing()
    {
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz /status");
        AuthStub200(harness.Handler);
        await RunAsync(db, harness);
        Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());

        // duplicate delivery of the same comment event: command already handled → no-op, no second reply
        await db.GitHubEvents
            .Where(e => e.DedupeKey == $"ic:{comment.GithubCommentId}:created")
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.ProcessingStatus, GitHubEventStatuses.Pending)
                .SetProperty(e => e.ProcessingError, (string?)null));
        db.ChangeTracker.Clear();
        await harness.Processor.RunCycleAsync(default);

        Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
    }

    // ---- helpers (mirror CommentCommandProcessorTests) ----

    private async Task RunAsync(AppDbContext db, ProcessorHarness harness)
    {
        db.ChangeTracker.Clear();
        await harness.Processor.RunCycleAsync(default);
    }

    private async Task<(AppDbContext db, ProcessorHarness harness, GitHubComment comment, WorkItem workItem)> NewCaseAsync(
        string? body = null,
        bool pullRequest = false,
        string? configuration = null,
        bool enabled = true)
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(
            db, pullRequest: pullRequest, configuration: configuration, enabled: enabled, body: body);
        await CommandTestSupport.SeedCommandEventAsync(db, comment, workItem);
        db.ChangeTracker.Clear();
        // the harness registers a default Runtime API stub so the /stop control path can run;
        // /status and /help never touch it
        var harness = CommandTestSupport.NewProcessor(postgres.ConnectionString,
            runtimeStub: new PiAgent.Hosts.Tests.Runtime.DirectHttpStub(_ =>
                new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new System.Net.Http.StringContent("""{"status":"stopping"}""",
                        System.Text.Encoding.UTF8, "application/json"),
                }));
        return (db, harness, comment, workItem);
    }

    private static void AuthStub200(PollerTestSupport.StubHandler handler) =>
        handler.EnqueueResponse(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("""{"permission": "admin", "role_name": "admin"}"""),
        });
}
