using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

namespace PiAgent.Hosts.Tests.Runtime;

/// <summary>
/// Ticket 09: the Orchestrator-side terminal-Run consumer — Task/Workflow transitions derived from
/// Runtime-persisted facts (03 §2: the Orchestrator owns the machine; agents report facts): promotion
/// `starting → running`, the `completed` consumer reading its persisted `outcome` (`succeeded` vs
/// `blocked`, decision 72), `failed`, and the desired container stop (03 §5).
/// </summary>
public class RunTerminalConsumerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private RunTerminalConsumer NewConsumer()
    {
        var connectionString = postgres.ConnectionString;
        return new(new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention())
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>());
    }

    /// <summary>Starts a run: Task starting, Run running/terminal per the scenario.</summary>
    private async Task<(OrchestrationTask Task, Run Run)> SeedStartingRunAsync(string runStatus, string? completedPayload = null)
    {
        var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        // claim → prepare (Run+Session+Container+Workspace) → dispatch write-back (Task/Run starting)
        var preparation = await RunPrepare.PrepareAsync(db, seeded.Task, version, default);
        seeded.Task.Status = OrchestrationTaskStatuses.Starting;
        seeded.Job.Status = OrchestrationJobStatuses.Completed;
        preparation!.Run.Status = runStatus;
        preparation.Run.StartedAt = DateTime.UtcNow;
        if (runStatus == RunStatuses.Succeeded)
        {
            preparation.Run.CompletedAt = DateTime.UtcNow;
            preparation.Run.ResultSummary = "done";
            db.RunEvents.Add(new RunEvent
            {
                Id = Guid.NewGuid(),
                RunId = preparation.Run.Id,
                EventId = "evt-terminal",
                Type = RunEventTypes.Completed,
                Payload = completedPayload ?? """{"eventId":"evt-terminal","result":{"summary":"done"}}""",
                OccurredAt = DateTime.UtcNow,
                ReceivedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return (seeded.Task, preparation.Run);
    }

    private async Task<OrchestrationTask> ReloadTaskAsync(Guid taskId)
    {
        var db = postgres.NewDbContext();
        return await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
    }

    [Fact]
    public async Task RunningRun_PromotesTaskToRunning()
    {
        await postgres.ResetAsync();
        var (task, _) = await SeedStartingRunAsync(RunStatuses.Running);
        await NewConsumer().RunCycleAsync(default);
        Assert.Equal(OrchestrationTaskStatuses.Running, (await ReloadTaskAsync(task.Id)).Status);
        // promoted once — no terminal transitions happened
        var db = postgres.NewDbContext();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId);
        Assert.Equal(WorkflowStatuses.Active, workflow.Status);
        Assert.Equal(task.Id, workflow.CurrentTaskId);
    }

    [Fact]
    public async Task PendingRun_LeavesTheTaskAlone()
    {
        await postgres.ResetAsync();
        var (task, _) = await SeedStartingRunAsync(RunStatuses.Pending);
        await NewConsumer().RunCycleAsync(default);
        Assert.Equal(OrchestrationTaskStatuses.Starting, (await ReloadTaskAsync(task.Id)).Status);
    }

    [Fact]
    public async Task SucceededRun_MarksTaskSucceeded_WaitingForHuman_AndDesiredStop()
    {
        await postgres.ResetAsync();
        var (task, run) = await SeedStartingRunAsync(RunStatuses.Succeeded);
        await NewConsumer().RunCycleAsync(default);

        var reloaded = await ReloadTaskAsync(task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Succeeded, reloaded.Status);
        Assert.NotNull(reloaded.CompletedAt);
        var db = postgres.NewDbContext();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId);
        Assert.Equal(WorkflowStatuses.WaitingForHuman, workflow.Status);
        Assert.Null(workflow.CurrentTaskId);
        var container = await db.ContainerInstances.AsNoTracking().SingleAsync(c => c.RunId == run.Id);
        Assert.Equal(ContainerDesiredStates.Stopped, container.DesiredState); // observed 'stopped' is the Runtime's
        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == task.TriggerCommandId);
        Assert.Equal(AgentCommandStatuses.Processed, command.Status);
    }

    [Fact]
    public async Task NeedsHumanInputOutcome_MarksTheTaskBlocked()
    {
        await postgres.ResetAsync();
        var (task, run) = await SeedStartingRunAsync(RunStatuses.Succeeded,
            completedPayload: """{"eventId":"evt-terminal","outcome":"needs_human_input","result":{"summary":"asked"}}""");
        await NewConsumer().RunCycleAsync(default);

        Assert.Equal(OrchestrationTaskStatuses.Blocked, (await ReloadTaskAsync(task.Id)).Status); // decision 72
        var db = postgres.NewDbContext();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId);
        Assert.Equal(WorkflowStatuses.WaitingForHuman, workflow.Status);
    }

    [Fact]
    public async Task CancelledRun_MarksTaskCancelledWithTheStopReason_WaitingForHuman()
    {
        await postgres.ResetAsync();
        // ticket 14: the graceful stop confirmed by the terminal `cancelled` callback (06 §8.2)
        var (task, run) = await SeedStartingRunAsync(RunStatuses.Cancelled);
        await NewConsumer().RunCycleAsync(default);

        var reloaded = await ReloadTaskAsync(task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, reloaded.Status);
        Assert.NotNull(reloaded.CancelledAt);
        Assert.Equal(AgentCommandRejectionReasons.StopRequested, reloaded.FailureReason); // the only V1 canceller
        var db = postgres.NewDbContext();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId);
        Assert.Equal(WorkflowStatuses.WaitingForHuman, workflow.Status);
        Assert.Null(workflow.CurrentTaskId);
        // the trigger command failed as run_cancelled (the job was completed at dispatch — nothing to cancel)
        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == task.TriggerCommandId);
        Assert.Equal(AgentCommandStatuses.Failed, command.Status);
        Assert.Equal("run_cancelled", command.RejectionReason);
        var container = await db.ContainerInstances.AsNoTracking().SingleAsync(c => c.RunId == run.Id);
        Assert.Equal(ContainerDesiredStates.Stopped, container.DesiredState);
    }

    [Fact]
    public async Task FailedRun_MarksTaskFailedWithTheReportedCode()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (task, run) = await SeedStartingRunAsync(RunStatuses.Succeeded);
        await db.Runs.Where(r => r.Id == run.Id).ExecuteUpdateAsync(set =>
        {
            set.SetProperty(r => r.Status, RunStatuses.Failed);
            set.SetProperty(r => r.ErrorCode, "github_publication_failed");
        });
        db.ChangeTracker.Clear();
        await NewConsumer().RunCycleAsync(default);

        var reloaded = await ReloadTaskAsync(task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Failed, reloaded.Status);
        Assert.Equal("github_publication_failed", reloaded.FailureReason);
        var verification = postgres.NewDbContext();
        var workflow = await verification.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId);
        Assert.Equal(WorkflowStatuses.WaitingForHuman, workflow.Status);
        var command = await verification.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == task.TriggerCommandId);
        Assert.Equal(AgentCommandStatuses.Failed, command.Status);
    }

    [Fact]
    public async Task GithubPublicationFailed_EnqueuesTheDecision42Notification_OnceWithArtifactsKept()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (task, run) = await SeedStartingRunAsync(RunStatuses.Failed);
        await db.Runs.Where(r => r.Id == run.Id).ExecuteUpdateAsync(set =>
        {
            set.SetProperty(r => r.Status, RunStatuses.Failed);
            set.SetProperty(r => r.ErrorCode, RunErrorCodes.GithubPublicationFailed);
            set.SetProperty(r => r.ErrorMessage, "required publication kind 'issue_comment' has no confirmed successful tool execution");
        });
        db.ChangeTracker.Clear();
        await NewConsumer().RunCycleAsync(default);

        var notifications = await db.OutboxActions.AsNoTracking().ToListAsync();
        var notification = Assert.Single(notifications); // exactly one per failed Run (06 §13)
        Assert.Equal(OutboxActionTypes.Comment, notification.ActionType);
        Assert.Equal(task.Id, notification.TaskId);
        using var body = System.Text.Json.JsonDocument.Parse(notification.Payload);
        var text = body.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Task failed: /plan", text, StringComparison.Ordinal);
        Assert.Contains("| Attempt: #1 | Stage: Required GitHub publication", text, StringComparison.Ordinal);
        Assert.Contains("github_publication_failed", text, StringComparison.Ordinal);
        Assert.Contains($"(id: run_{run.Id})", text, StringComparison.Ordinal);
        Assert.Contains("The produced artifacts are kept", text, StringComparison.Ordinal);
        Assert.Contains("(or use /retry)", text, StringComparison.Ordinal);
        // decision 42: never re-publishes the agent's result — the notification carries diagnostics only
        Assert.DoesNotContain("## Plan", text, StringComparison.Ordinal);
        // deterministic idempotency: the Run's own id is the dedup key of this notification
        Assert.Equal(run.Id.ToString(), notification.IdempotencyKey);
    }

    [Fact]
    public async Task OtherRunFailures_GetTheGenericFinalFailureNotification()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (task, run) = await SeedStartingRunAsync(RunStatuses.Failed);
        await db.Runs.Where(r => r.Id == run.Id).ExecuteUpdateAsync(set =>
        {
            set.SetProperty(r => r.Status, RunStatuses.Failed);
            set.SetProperty(r => r.ErrorCode, "manifest_invalid");
        });
        // realistic detail: manifest_invalid comes from the completion validation — the agent ran
        db.RunEvents.Add(new RunEvent
        {
            Id = Guid.NewGuid(), RunId = run.Id, EventId = "evt-started",
            Type = RunEventTypes.AgentStarted, OccurredAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        await NewConsumer().RunCycleAsync(default);

        // ticket 15 (decision 29): the generic 06 §13 template now covers every finally-failed Task —
        // codes outside the exhausted-retries/publication cases get the plain resubmit closing
        var notification = Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
        using var body = System.Text.Json.JsonDocument.Parse(notification.Payload);
        var text = body.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Task failed: /plan", text, StringComparison.Ordinal);
        Assert.Contains("manifest_invalid", text, StringComparison.Ordinal);
        Assert.Contains("| Stage: Execution", text, StringComparison.Ordinal); // post-execution
        Assert.Contains("The Workflow is waiting for human input", text, StringComparison.Ordinal);
        Assert.Contains("(or use /retry)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Automatic retries exhausted", text, StringComparison.Ordinal);
        Assert.DoesNotContain("The produced artifacts are kept", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimedOutRun_MarksTaskFailedMaxRunDuration()
    {
        await postgres.ResetAsync();
        var (task, run) = await SeedStartingRunAsync(RunStatuses.Succeeded);
        var db = postgres.NewDbContext();
        await db.Runs.Where(r => r.Id == run.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, RunStatuses.TimedOut));
        db.ChangeTracker.Clear();
        await NewConsumer().RunCycleAsync(default);
        Assert.Equal(OrchestrationTaskStatuses.Failed, (await ReloadTaskAsync(task.Id)).Status);
        Assert.Equal("max_run_duration", (await ReloadTaskAsync(task.Id)).FailureReason);
    }

    [Fact]
    public async Task Consumer_ReadsOutcomeFromThePersistedCompletedPayload_WithoutColumn()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (task, run) = await SeedStartingRunAsync(RunStatuses.Succeeded,
            completedPayload: """{"eventId":"e","outcome":"needs_human_input"}""");
        var outcome = await RunTerminalConsumer.CompletedOutcomeAsync(db, run, default);
        Assert.Equal("needs_human_input", outcome);
        db.RunEvents.Add(new RunEvent
        {
            Id = Guid.NewGuid(), RunId = run.Id, EventId = "evt-2",
            Type = RunEventTypes.Completed, Payload = """{"eventId":"e"}""",
            OccurredAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
        var outcome2 = await RunTerminalConsumer.CompletedOutcomeAsync(db, run, default);
        Assert.Equal("completed", outcome2);
    }

    // ---- ticket 15: automatic pre-execution retry (decisions 28/70) ----

    /// <summary>Seeds a terminal-failed attempt over SeedStartingRunAsync: the given error code, attempt
    /// number and spent automatic-retry budget; no `agent_started` event (pre-execution) unless
    /// `executionStarted` adds one.</summary>
    private async Task<(OrchestrationTask Task, Run Run)> SeedFailedAttemptAsync(
        string? errorCode, int attemptNumber = 1, int automaticRetriesUsed = 0,
        bool executionStarted = false, string? retryPolicy = null)
    {
        var db = postgres.NewDbContext();
        var (task, run) = await SeedStartingRunAsync(RunStatuses.Failed);
        await db.Runs.Where(r => r.Id == run.Id).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.ErrorCode, errorCode)
            .SetProperty(r => r.ErrorMessage, "seed failure")
            .SetProperty(r => r.AttemptNumber, attemptNumber));
        await db.OrchestrationTasks.Where(t => t.Id == task.Id).ExecuteUpdateAsync(set => set
            .SetProperty(t => t.AutomaticRetriesUsed, automaticRetriesUsed)
            .SetProperty(t => t.RetryPolicy, retryPolicy ?? "{}"));
        if (executionStarted)
            db.RunEvents.Add(new RunEvent
            {
                Id = Guid.NewGuid(), RunId = run.Id, EventId = "evt-started",
                Type = RunEventTypes.AgentStarted, OccurredAt = DateTime.UtcNow, ReceivedAt = DateTime.UtcNow,
            });
        await db.SaveChangesAsync();
        return (task, run);
    }

    [Fact]
    public async Task StartupTimeout_PreExecution_WithBudget_RequeuesSilently()
    {
        await postgres.ResetAsync();
        var (originalTask, originalRun) = await SeedFailedAttemptAsync(RunErrorCodes.StartupTimeout);
        await NewConsumer().RunCycleAsync(default);

        var db = postgres.NewDbContext();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == originalTask.Id);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status); // Failed → Queued, same Task (03 §2)
        Assert.Null(task.FailureReason);
        Assert.Null(task.CompletedAt);
        Assert.Equal(1, task.AutomaticRetriesUsed); // decision 28 budget spent
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId);
        Assert.Equal(WorkflowStatuses.Active, workflow.Status); // retried Task still occupies the Workflow
        Assert.Equal(task.Id, workflow.CurrentTaskId);
        // silent: the trigger command stays queued, no notification (06 §13)
        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == task.TriggerCommandId);
        Assert.Equal(AgentCommandStatuses.Queued, command.Status);
        Assert.Empty(await db.OutboxActions.AsNoTracking().ToListAsync());
        // no new Run until the claim flow recreates it at attempt_number+1
        Assert.Single(await db.Runs.AsNoTracking().Where(r => r.TaskId == task.Id).ToListAsync());
        // the failed attempt's job is closed and exactly one fresh pending job schedules the retry
        var jobs = await db.OrchestrationJobs.AsNoTracking().Where(j => j.TaskId == task.Id).ToListAsync();
        Assert.Equal(2, jobs.Count);
        Assert.Single(jobs, j => j.Status == OrchestrationJobStatuses.Completed);
        var retryJob = Assert.Single(jobs, j => j.Status == OrchestrationJobStatuses.Pending);
        Assert.Equal(OrchestrationJobTypes.RunTask, retryJob.JobType);
        var backoffStart = retryJob.AvailableAt - DateTime.UtcNow;
        Assert.InRange(backoffStart.TotalSeconds, 25, 35); // pinned initial_backoff (30 s)
        // the failed attempt's container goes to desired `stopped`
        var container = await db.ContainerInstances.AsNoTracking().SingleAsync(c => c.RunId == originalRun.Id);
        Assert.Equal(ContainerDesiredStates.Stopped, container.DesiredState);
    }

    [Fact]
    public async Task RuntimeFailure_PreExecution_WithBudget_RequeuesSilently()
    {
        await postgres.ResetAsync();
        // reconciler case: the container died while the Run was still `starting` — no agent_started
        await SeedFailedAttemptAsync(RunErrorCodes.RuntimeFailure);
        await NewConsumer().RunCycleAsync(default);

        var db = postgres.NewDbContext();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Status == OrchestrationTaskStatuses.Queued);
        Assert.Equal(1, task.AutomaticRetriesUsed);
        Assert.Empty(await db.OutboxActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task StartupTimeout_BudgetExhausted_FinallyFailsWithExhaustedNotification()
    {
        await postgres.ResetAsync();
        var (originalTask, originalRun) = await SeedFailedAttemptAsync(
            RunErrorCodes.StartupTimeout, attemptNumber: 2, automaticRetriesUsed: 1);
        await NewConsumer().RunCycleAsync(default);

        var db = postgres.NewDbContext();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == originalTask.Id);
        Assert.Equal(OrchestrationTaskStatuses.Failed, task.Status);
        Assert.Equal(RunErrorCodes.StartupTimeout, task.FailureReason);
        Assert.NotNull(task.CompletedAt);
        Assert.Equal(1, task.AutomaticRetriesUsed); // never grew past the exhausted budget
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId);
        Assert.Equal(WorkflowStatuses.WaitingForHuman, workflow.Status);
        Assert.Null(workflow.CurrentTaskId);
        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == task.TriggerCommandId);
        Assert.Equal(AgentCommandStatuses.Failed, command.Status);
        // no fresh job was created behind the exhausted budget
        Assert.Single(await db.OrchestrationJobs.AsNoTracking().Where(j => j.TaskId == task.Id).ToListAsync());
        // one notification in the 06 §13 template, exhausted-retries closing
        var notification = Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
        Assert.Equal(originalRun.Id.ToString(), notification.IdempotencyKey);
        using var body = System.Text.Json.JsonDocument.Parse(notification.Payload);
        var text = body.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Task failed: /plan", text, StringComparison.Ordinal);
        Assert.Contains("| Attempt: #2 | Stage: Startup (never became ready)", text, StringComparison.Ordinal);
        Assert.Contains("startup_timeout", text, StringComparison.Ordinal);
        Assert.Contains("Automatic retries exhausted.", text, StringComparison.Ordinal);
        Assert.Contains("(or use /retry)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartupTimeout_ZeroBudget_FinallyFailsWithPlainClosing()
    {
        await postgres.ResetAsync();
        var (originalTask, _) = await SeedFailedAttemptAsync(RunErrorCodes.StartupTimeout,
            retryPolicy: """{"max_automatic_retries": 0, "initial_backoff_seconds": 5}""");
        await NewConsumer().RunCycleAsync(default);

        var db = postgres.NewDbContext();
        var reloaded = await ReloadTaskAsync(originalTask.Id);
        Assert.Equal(OrchestrationTaskStatuses.Failed, reloaded.Status);
        Assert.Equal(0, reloaded.AutomaticRetriesUsed);
        var notification = Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
        using var body = System.Text.Json.JsonDocument.Parse(notification.Payload);
        var text = body.RootElement.GetProperty("text").GetString()!;
        Assert.DoesNotContain("Automatic retries exhausted", text, StringComparison.Ordinal); // nothing was ever retried
        Assert.Contains("The Workflow is waiting for human input", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeFailure_AfterExecutionBegan_NeverAutoRetries()
    {
        await postgres.ResetAsync();
        // the agent STARTED (agent_started persisted) — decision 70: /retry is the only recovery path
        await SeedFailedAttemptAsync(RunErrorCodes.RuntimeFailure, executionStarted: true);
        await NewConsumer().RunCycleAsync(default);

        var db = postgres.NewDbContext();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Status == OrchestrationTaskStatuses.Failed);
        Assert.Equal(0, task.AutomaticRetriesUsed);
        Assert.Equal(WorkflowStatuses.WaitingForHuman,
            (await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == task.WorkflowId)).Status);
        var notification = Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
        using var body = System.Text.Json.JsonDocument.Parse(notification.Payload);
        var text = body.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("| Stage: Execution", text, StringComparison.Ordinal); // post-execution stage
        Assert.DoesNotContain("Automatic retries exhausted", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TimedOutRun_FinallyFailsWithNotification()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (originalTask, originalRun) = await SeedStartingRunAsync(RunStatuses.Succeeded);
        await db.Runs.Where(r => r.Id == originalRun.Id).ExecuteUpdateAsync(set =>
        {
            set.SetProperty(r => r.Status, RunStatuses.TimedOut);
            set.SetProperty(r => r.ErrorMessage, "reconciled: the run exceeded max_run_duration");
        });
        db.ChangeTracker.Clear();
        await NewConsumer().RunCycleAsync(default);

        var reloaded = await ReloadTaskAsync(originalTask.Id);
        Assert.Equal(OrchestrationTaskStatuses.Failed, reloaded.Status);
        Assert.Equal("max_run_duration", reloaded.FailureReason);
        Assert.Equal(0, reloaded.AutomaticRetriesUsed); // never retried (03 §3)
        var notification = Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
        using var body = System.Text.Json.JsonDocument.Parse(notification.Payload);
        var text = body.RootElement.GetProperty("text").GetString()!;
        Assert.Contains("Failure: max_run_duration", text, StringComparison.Ordinal);
        Assert.Contains("| Attempt: #1 | Stage: Execution", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Automatic retries exhausted", text, StringComparison.Ordinal);
    }
}
