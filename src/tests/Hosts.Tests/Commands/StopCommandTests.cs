using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Hosts.Tests.Runtime;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

namespace PiAgent.Hosts.Tests.Commands;

/// <summary>
/// The /stop control path, driven through the comment-command
/// processor exactly as GitHub comments arrive: "nothing to stop" without any unfinished work (no row
/// touched); a queued/claimed Task cancelled immediately with its Job, trigger command and
/// never-dispatched pending Run rows; a running Run signalled over the Runtime API (POST …/stop with
/// the service token) while the Task stays unfinished until the runtime confirms; one Outbox
/// notification covering the cancellations; a transport failure releases the event for an idempotent
/// retry instead of half-cancelling.
/// </summary>
public class StopCommandTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string StopTokenOk = """{"runId":"x","status":"stopping"}""";

    private static HttpResponseMessage RuntimeOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(StopTokenOk, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage RuntimeUnavailable() => new(HttpStatusCode.ServiceUnavailable);

    // ---- mid-claim race guards ----

    /// <summary>/stop cancelled a claimed Task while its claim worker was mid-flight; the claim-time
    /// cancellation must be a no-op then — in particular it must NEVER enqueue its own notice.</summary>
    [Fact]
    public async Task CancelTaskAtClaim_AlreadyCancelledByStop_IsANoOpWithoutNotice()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version);
        await db.OrchestrationTasks.ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Cancelled)
            .SetProperty(t => t.FailureReason, AgentCommandRejectionReasons.StopRequested));
        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.SingleAsync(t => t.Id == prepared.Task.Id);
        var job = await db.OrchestrationJobs.SingleAsync(j => j.Id == prepared.Job.Id);
        db.ChangeTracker.Clear();

        await TaskLifecycle.CancelTaskAtClaimAsync(db, task, job, repo, workItem,
            AgentCommandRejectionReasons.RuntimeDispatchFailed, new JobQueueMetrics(), default);

        var reloaded = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, reloaded.Status);
        Assert.Equal(AgentCommandRejectionReasons.StopRequested, reloaded.FailureReason); // /stop's reason kept
        Assert.Empty(await db.OutboxActions.AsNoTracking().ToListAsync()); // no spurious claim-cancellation notice
        await db.DisposeAsync();
    }

    // ---- helpers ----

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

    /// <summary>Seeds an authorized `/stop` comment and returns the case with its stubbed Runtime API.</summary>
    private async Task<(AppDbContext db, ProcessorHarness harness, GitHubComment comment, WorkItem workItem, DirectHttpStub runtime)> NewCaseAsync(string body)
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, body: body);
        await CommandTestSupport.SeedCommandEventAsync(db, comment, workItem);
        db.ChangeTracker.Clear();
        var runtimeStub = new DirectHttpStub(_ => RuntimeOk());
        var harness = CommandTestSupport.NewProcessor(postgres.ConnectionString, runtimeStub: runtimeStub);
        return (db, harness, comment, workItem, runtimeStub);
    }

    // ---- nothing to stop ----

    [Fact]
    public async Task StopWithoutWorkflow_NothingToStopReply_NoRowsTouched()
    {
        var (db, harness, comment, workItem, runtime) = await NewCaseAsync("@jsxhuz /stop");
        AuthStub200(harness.Handler);
                await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, command.Status);
        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("nothing to stop", payload, StringComparison.Ordinal);
        // nothing is closed or deleted; a Workflow is not even created
        Assert.Empty(await db.Workflows.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrchestrationTasks.AsNoTracking().ToListAsync());
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task StopWithWorkflowButTerminalTask_NothingToStop_WorkflowKept()
    {
        var (db, harness, comment, workItem, runtime) = await NewCaseAsync("@jsxhuz /stop");
        AuthStub200(harness.Handler);
                var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version);
        await db.OrchestrationTasks.ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Succeeded));
        await db.Workflows.ExecuteUpdateAsync(u => u
            .SetProperty(w => w.Status, WorkflowStatuses.WaitingForHuman));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("nothing to stop", payload, StringComparison.Ordinal);
        // a finished (succeeded) Task is not stopped; the Workflow is not closed or deleted
        Assert.Equal(OrchestrationTaskStatuses.Succeeded,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id)).Status);
        Assert.Equal(WorkflowStatuses.WaitingForHuman, (await db.Workflows.AsNoTracking().SingleAsync()).Status);
        Assert.Empty(runtime.Requests);
    }

    // ---- queued cancellation ----

    [Fact]
    public async Task StopWithQueuedTask_CancelsTaskJobCommand_PendingRunDeleted_LeavesOneNotification()
    {
        var (db, harness, comment, workItem, runtime) = await NewCaseAsync("@jsxhuz /stop");
        AuthStub200(harness.Handler);
                var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version);
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        // /stop processed
        var stop = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, stop.Status);
        // the queued Task cancelled with its recorded reason
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, task.Status);
        Assert.Equal(AgentCommandRejectionReasons.StopRequested, task.FailureReason);
        Assert.NotNull(task.CancelledAt);
        // the Job cancelled; never-dispatched pending Run rows removed (session/container cascade)
        Assert.Equal(OrchestrationJobStatuses.Cancelled,
            (await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == prepared.Job.Id)).Status);
        Assert.Empty(await db.Runs.AsNoTracking().ToListAsync());
        Assert.Empty(await db.AgentSessions.AsNoTracking().ToListAsync());
        Assert.Empty(await db.ContainerInstances.AsNoTracking().ToListAsync());
        // the trigger command failed with the stop reason
        var trigger = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == prepared.Command!.Id);
        Assert.Equal(AgentCommandStatuses.Failed, trigger.Status);
        Assert.Equal(AgentCommandRejectionReasons.StopRequested, trigger.RejectionReason);
        // Workflow waiting_for_human with no open work
        var workflow = await db.Workflows.AsNoTracking().SingleAsync();
        Assert.Equal(WorkflowStatuses.WaitingForHuman, workflow.Status);
        Assert.Null(workflow.CurrentTaskId);
        // exactly one Outbox notification covering the cancellation; never the runtime API
        var notice = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("/stop: cancellation processed.", notice, StringComparison.Ordinal);
        Assert.Contains("Queued tasks cancelled with their jobs: 1", notice, StringComparison.Ordinal);
        Assert.Contains("Storage (session files, logs, workspace) is retained", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("Active task", notice, StringComparison.Ordinal);
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public async Task StopWithClaimedTask_CancelsTheProcessingJobToo()
    {
        var (db, harness, comment, workItem, runtime) = await NewCaseAsync("@jsxhuz /stop");
        AuthStub200(harness.Handler);
                var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version);
        // simulated claim: job processing + task claimed (/stop cuts through the claim)
        await db.OrchestrationJobs.ExecuteUpdateAsync(u => u
            .SetProperty(j => j.Status, OrchestrationJobStatuses.Processing));
        await db.OrchestrationTasks.ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Claimed));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        Assert.Equal(OrchestrationTaskStatuses.Cancelled,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id)).Status);
        Assert.Equal(OrchestrationJobStatuses.Cancelled,
            (await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == prepared.Job.Id)).Status);
        Assert.Empty(await db.Runs.AsNoTracking().ToListAsync());
    }

    // ---- active run signal ----

    [Fact]
    public async Task StopWithRunningRun_SignalsRuntimeAPI_TaskStaysUntilConfirmation()
    {
        var (db, harness, comment, workItem, runtime) = await NewCaseAsync("@jsxhuz /stop");
        AuthStub200(harness.Handler);
                var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version);
        await db.Runs.ExecuteUpdateAsync(u => u
            .SetProperty(r => r.Status, RunStatuses.Running)
            .SetProperty(r => r.AttemptNumber, 2));
        await db.OrchestrationTasks.ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Running));
        await db.OrchestrationJobs.ExecuteUpdateAsync(u => u
            .SetProperty(j => j.Status, OrchestrationJobStatuses.Processing));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        // the Runtime API received exactly one stop signal on the service route with the reason code
        var stopRequest = runtime.Requests.Single();
        Assert.EndsWith($"/internal/runtime/runs/{prepared.Run!.Id}/stop", stopRequest.RequestUri!.PathAndQuery);
        Assert.Equal("tok-orch", stopRequest.Headers.GetValues("X-Service-Token").Single()); // service credential
        Assert.Contains("stop_requested", runtime.Bodies.Single(), StringComparison.Ordinal);
        // nothing is pre-emptively torn down: the terminal consumer cancels Task/Workflow AFTER
        // runtime confirmation ("after runtime confirmation or reconciliation")
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Running, task.Status);
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.Id == prepared.Run.Id);
        Assert.Equal(RunStatuses.Running, run.Status);
        Assert.Null(run.StopRequestedAt); // the Runtime API records the signal, not the Orchestrator
        var workflow = await db.Workflows.AsNoTracking().SingleAsync();
        Assert.Equal(WorkflowStatuses.Active, workflow.Status);
        Assert.Equal(prepared.Task.Id, workflow.CurrentTaskId);
        // exactly one notification covering the active cancellation
        var payload = (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload;
        Assert.Contains("Active task /plan (attempt #2", payload, StringComparison.Ordinal);
        Assert.Contains("graceful stop dispatched", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Queued tasks cancelled", payload, StringComparison.Ordinal);
        // the /stop command is processed
        var stop = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, stop.Status);
    }

    [Fact]
    public async Task StopSignalFailure_ReleasesTheEventAndKeepsTheWorkflowUntouched()
    {
        var (db, harness, comment, workItem, runtime) = await NewCaseAsync("@jsxhuz /stop");
        AuthStub200(harness.Handler);
        runtime.Fallback = _ => RuntimeUnavailable();
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var prepared = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version);
        await db.Runs.ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RunStatuses.Running));
        await db.OrchestrationTasks.ExecuteUpdateAsync(u => u
            .SetProperty(t => t.Status, OrchestrationTaskStatuses.Running));
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        // the failure rolls the whole /stop back: no cancellations, no notification, and the whole
        // command row (created inside the same transaction) released with the event for the retry
        Assert.Empty(await db.OutboxActions.AsNoTracking().ToListAsync());
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == prepared.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Running, task.Status);
        Assert.Empty(await db.AgentCommands.AsNoTracking().Where(c => c.GithubCommentId == comment.GithubCommentId).ToListAsync());
        var githubEvent = await db.GitHubEvents.AsNoTracking()
            .SingleAsync(e => e.EventType == GitHubEventTypes.IssueCommentCreated);
        Assert.Equal(GitHubEventStatuses.Pending, githubEvent.ProcessingStatus); // released for the idempotent retry
        Assert.Contains("stop signal", githubEvent.ProcessingError ?? "", StringComparison.Ordinal);
    }
}
