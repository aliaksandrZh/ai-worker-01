using System.Net;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

namespace PiAgent.Hosts.Tests.Commands;

/// <summary>
/// The /retry command (ticket 15, 06 §9a, decisions 27/65/70): re-runs the WorkItem's last failed Task
/// WITHOUT creating a new Task — same Task (pinned profile version, same target_head_sha for reviews,
/// reused input artifacts and Workspace), fresh OrchestrationJob, normal claim flow with
/// attempt_number+1 and a fresh AgentSession. Accepted retries are silent (06 §13); rejections leave
/// the Task `Failed` and enqueue exactly one reply. Per-task-type conditions stay at claim time
/// (owner decision): the claim test covers the implement plan-input reuse path.
/// </summary>
public class RetryCommandTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static void AuthStub200(PollerTestSupport.StubHandler handler) =>
        handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"permission": "admin", "role_name": "admin"}"""),
        });

    private async Task RunAsync(AppDbContext db, ProcessorHarness harness)
    {
        db.ChangeTracker.Clear();
        await harness.Processor.RunCycleAsync(default);
    }

    /// <summary>Seeds an authorized `/retry` comment like a fresh GitHub comment delivery.</summary>
    private async Task<(AppDbContext db, ProcessorHarness harness, GitHubComment comment, WorkItem workItem)> NewCaseAsync(string body, Repository? existingRepository = null)
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, body: body, existingRepository: existingRepository);
        await CommandTestSupport.SeedCommandEventAsync(db, comment, workItem);
        db.ChangeTracker.Clear();
        var harness = CommandTestSupport.NewProcessor(postgres.ConnectionString);
        return (db, harness, comment, workItem);
    }

    /// <summary>Seeds the post-terminal-consumer state of a failed attempt: Task `failed` (with the
    /// spent automatic-retry budget), Run `failed`, claim Job `completed`, trigger command `failed`,
    /// Workflow `waiting_for_human` without open work.</summary>
    private async Task<AdmittedTaskWork> SeedFailedTaskAsync(
        AppDbContext db,
        WorkItem workItem,
        string commandName = AgentCommandNames.Plan,
        string failureReason = RunErrorCodes.StartupTimeout,
        int automaticRetriesUsed = 0)
    {
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version,
            commandName: commandName, linkPlanInput: commandName == AgentCommandNames.Code);
        await db.OrchestrationTasks.Where(t => t.Id == prepared.Task.Id).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Failed)
            .SetProperty(t => t.FailureReason, failureReason)
            .SetProperty(t => t.CompletedAt, DateTime.UtcNow)
            .SetProperty(t => t.AutomaticRetriesUsed, automaticRetriesUsed));
        await db.Runs.ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Status, RunStatuses.Failed)
            .SetProperty(r => r.ErrorCode, failureReason)
            .SetProperty(r => r.CompletedAt, DateTime.UtcNow));
        await db.OrchestrationJobs.ExecuteUpdateAsync(u => u
            .SetProperty(j => j.Status, OrchestrationJobStatuses.Completed));
        await db.Workflows.ExecuteUpdateAsync(u => u
            .SetProperty(w => w.Status, WorkflowStatuses.WaitingForHuman)
            .SetProperty(w => w.CurrentTaskId, (Guid?)null));
        await db.AgentCommands.Where(c => c.Id == prepared.Command!.Id).ExecuteUpdateAsync(u => u
            .SetProperty(c => c.Status, AgentCommandStatuses.Failed)
            .SetProperty(c => c.RejectionReason, failureReason)
            .SetProperty(c => c.ProcessedAt, DateTime.UtcNow));
        await db.SaveChangesAsync();
        return prepared;
    }

    /// <summary>Extra Workflow task (any status) — for cancelled-skip and unfinished-occupancy cases.</summary>
    private static async Task<OrchestrationTask> SeedExtraTaskAsync(AppDbContext db, AdmittedTaskWork prepared, string status)
    {
        var task = new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = prepared.Workflow.Id,
            Sequence = prepared.Task.Sequence + 1,
            Type = prepared.Task.Type,
            AgentProfileVersionId = prepared.Task.AgentProfileVersionId,
            Status = status,
            CreatedAt = DateTime.UtcNow,
            RetryPolicy = "{}",
        };
        if (status == OrchestrationTaskStatuses.Cancelled)
            task.CancelledAt = DateTime.UtcNow;
        db.OrchestrationTasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    // ---- accepted retry ----

    [Fact]
    public async Task Retry_Accepted_RequeuesSameTask_WithFreshJob_Silently()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        var prepared = await SeedFailedTaskAsync(db, workItem);
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        // the retry command is processed with NO reply (06 §13: no row for an accepted /retry)
        var retry = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, retry.Status);
        Assert.Null(retry.RejectionReason);
        Assert.Empty(await db.OutboxActions.AsNoTracking().ToListAsync());
        // the SAME Task re-enters the queue (no new Task row)
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
        Assert.Null(task.FailureReason);
        Assert.Null(task.CompletedAt);
        // the Workflow re-enters `active` with the retried Task as its open work (03 §1)
        var workflow = await db.Workflows.AsNoTracking().SingleAsync();
        Assert.Equal(WorkflowStatuses.Active, workflow.Status);
        Assert.Equal(task.Id, workflow.CurrentTaskId);
        // a fresh OrchestrationJob is available now (no backoff for a manual retry)
        var jobs = await db.OrchestrationJobs.AsNoTracking().Where(j => j.TaskId == task.Id).ToListAsync();
        Assert.Equal(2, jobs.Count);
        var fresh = Assert.Single(jobs, j => j.Status == OrchestrationJobStatuses.Pending);
        Assert.True(fresh.AvailableAt <= DateTime.UtcNow.AddSeconds(1));
        // the pinned profile version is untouched
        Assert.Equal(prepared.Task.AgentProfileVersionId, task.AgentProfileVersionId);
        // no new Run yet (the claim flow creates attempt_number+1)
        Assert.Single(await db.Runs.AsNoTracking().Where(r => r.TaskId == task.Id).ToListAsync());
    }

    [Fact]
    public async Task Retry_ThenClaim_CreatesAttempt2_FreshSession_ReusesWorkspaceAndInputs()
    {
        var (db, harness, _, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        var prepared = await SeedFailedTaskAsync(db, workItem, commandName: AgentCommandNames.Code, automaticRetriesUsed: 1);
        // a retried implement Task keeps its (approval) input artifact and reuses its Workspace
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(),
            TaskId = prepared.Task.Id,
            WorkItemId = workItem.Id,
            RepositoryId = workItem.RepositoryId,
            Status = WorkspaceStatuses.Ready,
            StorageKind = "host_dir",
            StorageStatus = "available",
            AccessMode = "read_write",
            CreatedAt = DateTime.UtcNow,
        };
        db.Workspaces.Add(workspace);
        await db.SaveChangesAsync();
        var inputArtifactIds = await db.TaskArtifactInputs.AsNoTracking()
            .Where(i => i.TaskId == prepared.Task.Id).Select(i => i.ArtifactId).ToListAsync();
        db.ChangeTracker.Clear();

        await RunAsync(db, harness); // /retry accepted

        // the claim flow then does the attempt+1 work (decision 70: normal claim flow follows)
        var workerHarness = OrchestrationTestSupport.NewWorker(postgres);
        PollerTestSupport.StubHandler gateStub = workerHarness.Handler;
        gateStub.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"permission": "admin", "role_name": "admin"}"""),
        });
        gateStub.Fallback = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"permission": "admin", "role_name": "admin"}"""),
        };
        await workerHarness.Worker.RunCycleAsync(default);

        db.ChangeTracker.Clear();
        // the retried Task passed the claim and got a new Run at attempt_number+1 with a FRESH session
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Claimed, task.Status);
        var runs = await db.Runs.AsNoTracking().Where(r => r.TaskId == task.Id).ToListAsync();
        Assert.Equal(2, runs.Count);
        Assert.Equal(RunStatuses.Failed, runs.Single(r => r.AttemptNumber == 1).Status);
        Assert.Equal(RunStatuses.Pending, runs.Single(r => r.AttemptNumber == 2).Status); // fresh attempt, dispatch not yet written back
        var claimJob = Assert.Single(await db.OrchestrationJobs.AsNoTracking()
            .Where(j => j.TaskId == task.Id && j.Status == OrchestrationJobStatuses.Processing).ToListAsync());
        Assert.NotEqual(prepared.Job.Id, claimJob.Id); // the retry's fresh job
        var newAttempt = runs.Single(r => r.AttemptNumber == 2);
        var attempt1RunId = runs.Single(r => r.AttemptNumber == 1).Id;
        var oldSession = await db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == attempt1RunId);
        var newSession = await db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == newAttempt.Id);
        Assert.NotEqual(oldSession.Id, newSession.Id); // a retry never reuses the session
        Assert.Equal(task.Id, newSession.TaskId);
        // the Workspace (and the input artifact) are reused unchanged
        Assert.Equal(workspace.Id, (await db.Workspaces.AsNoTracking().SingleAsync(w => w.TaskId == task.Id)).Id);
        var inputsAfter = await db.TaskArtifactInputs.AsNoTracking()
            .Where(i => i.TaskId == task.Id).Select(i => i.ArtifactId).SingleAsync();
        Assert.Equal(inputArtifactIds.Single(), inputsAfter);
        // manual /retry ignores the automatic budget (it was already spent: automaticRetriesUsed = 1)
        Assert.Equal(1, task.AutomaticRetriesUsed);
    }

    [Fact]
    public async Task Retry_KeepsThePinnedReviewHeadSha()
    {
        var (db, harness, _, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        var prepared = await SeedFailedTaskAsync(db, workItem, commandName: AgentCommandNames.Review);
        await db.OrchestrationTasks.Where(t => t.Id == prepared.Task.Id).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.TargetHeadSha, "deadbeef"));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        // the SAME Task retries: the pinned review SHA (decision 31) and profile version are untouched
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
        Assert.Equal("deadbeef", task.TargetHeadSha);
        Assert.Equal(prepared.Task.AgentProfileVersionId, task.AgentProfileVersionId);
    }

    // ---- rejected retries (Task stays Failed; exactly one reply; decision 70) ----

    [Fact]
    public async Task Retry_WithoutWorkflow_RejectedTaskNotRetryable()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var retry = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, retry.Status);
        Assert.Equal(AgentCommandRejectionReasons.TaskNotRetryable, retry.RejectionReason);
        Assert.Empty(await db.OrchestrationTasks.AsNoTracking().ToListAsync());
        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("no failed task to retry", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Retry_OnSucceededTask_RejectedTaskNotRetryable_TaskStaysSucceeded()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        var prepared = await SeedFailedTaskAsync(db, workItem);
        await db.OrchestrationTasks.Where(t => t.Id == prepared.Task.Id).ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Succeeded)
            .SetProperty(t => t.FailureReason, (string?)null));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var retry = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, retry.Status);
        Assert.Equal(AgentCommandRejectionReasons.TaskNotRetryable, retry.RejectionReason);
        // /retry never re-runs a succeeded Task (06 §9a)
        Assert.Equal(OrchestrationTaskStatuses.Succeeded,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id)).Status);
        Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Retry_SkipsCancelledTask_RetriesTheFailedOne()
    {
        var (db, harness, _, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        var prepared = await SeedFailedTaskAsync(db, workItem);
        var cancelled = await SeedExtraTaskAsync(db, prepared, OrchestrationTaskStatuses.Cancelled);
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        // the most recent task is cancelled and skipped: the failed task before it is the retry target
        Assert.Equal(OrchestrationTaskStatuses.Queued,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id)).Status);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == cancelled.Id)).Status);
        Assert.Empty(await db.OutboxActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Retry_WithUnfinishedTask_RejectedOccupancy_TaskStaysFailed()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        var prepared = await SeedFailedTaskAsync(db, workItem);
        await SeedExtraTaskAsync(db, prepared, OrchestrationTaskStatuses.Queued);
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        // occupancy (06 §12) beats the retry: same-Workflow commands are not queued behind one another
        var retry = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, retry.Status);
        Assert.Equal(AgentCommandRejectionReasons.UnfinishedTaskExists, retry.RejectionReason);
        Assert.Equal(OrchestrationTaskStatuses.Failed,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id)).Status);
        Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Retry_OnDisabledPinnedVersion_RejectedAgentProfileNotReady_TaskStaysFailed()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        var prepared = await SeedFailedTaskAsync(db, workItem);
        var versionId = prepared.Task.AgentProfileVersionId;
        await db.AgentProfileVersions.Where(v => v.Id == versionId).ExecuteUpdateAsync(u => u
            .SetProperty(v => v.State, ProfileVersionStates.Disabled));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var retry = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, retry.Status);
        Assert.Equal(AgentCommandRejectionReasons.AgentProfileNotReady, retry.RejectionReason);
        // decision 70: the rejection leaves the Task Failed (restore the version to retry)
        Assert.Equal(OrchestrationTaskStatuses.Failed,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id)).Status);
        Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Retry_OnBuildFailedVersion_RejectedAgentProfileNotReady()
    {
        var (db, harness, _, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        var prepared = await SeedFailedTaskAsync(db, workItem);
        var versionId = prepared.Task.AgentProfileVersionId;
        await db.AgentProfileVersions.Where(v => v.Id == versionId).ExecuteUpdateAsync(u => u
            .SetProperty(v => v.State, ProfileVersionStates.BuildFailed));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var retry = await db.AgentCommands.SingleAsync(c => c.CommandName == AgentCommandNames.Retry);
        Assert.Equal(AgentCommandRejectionReasons.AgentProfileNotReady, retry.RejectionReason);
        Assert.Equal(OrchestrationTaskStatuses.Failed,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id)).Status);
    }

    [Fact]
    public async Task Retry_OnClosedWorkItem_RejectedWorkItemClosed()
    {
        var (db, harness, _, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        var prepared = await SeedFailedTaskAsync(db, workItem);
        await db.WorkItems.Where(w => w.Id == workItem.Id).ExecuteUpdateAsync(u => u
            .SetProperty(w => w.State, "closed"));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var retry = await db.AgentCommands.SingleAsync(c => c.CommandName == AgentCommandNames.Retry);
        Assert.Equal(AgentCommandStatuses.Rejected, retry.Status);
        Assert.Equal(AgentCommandRejectionReasons.WorkItemClosed, retry.RejectionReason);
        Assert.Equal(OrchestrationTaskStatuses.Failed,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id)).Status);
        Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Retry_OnFailedWorkflow_RejectedWorkflowFailed()
    {
        var (db, harness, _, workItem) = await NewCaseAsync("@jsxhuz /retry");
        AuthStub200(harness.Handler);
        await SeedFailedTaskAsync(db, workItem);
        // decision 71: a failed Workflow admits nothing — admission rejects /retry before the handler
        await db.Workflows.ExecuteUpdateAsync(u => u
            .SetProperty(w => w.Status, WorkflowStatuses.Failed)
            .SetProperty(w => w.FailureReason, "integrity_failure"));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var retry = await db.AgentCommands.SingleAsync(c => c.CommandName == AgentCommandNames.Retry);
        Assert.Equal(AgentCommandStatuses.Rejected, retry.Status);
        Assert.Equal(AgentCommandRejectionReasons.WorkflowFailed, retry.RejectionReason);
        Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync());
    }
}
