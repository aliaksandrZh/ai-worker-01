using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Orchestration;
using System.Net;
using Xunit;

namespace PiAgent.Hosts.Tests.Orchestration;

/// <summary>
/// The durable job queue: FIFO claim (available_at, then
/// priority), the global cap counting claimed/starting/running(/stopping) work — queued Tasks and
/// pending Jobs occupy no slot — transactional claims that never double-claim, expired leases rolling
/// back to pending, and the claim-time rechecks (open WorkItem, pinned review SHA, ready profile,
/// plan present, permissions rechecked) that cancel-or-release but never execute.
/// </summary>
public class JobQueueWorkerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken TestToken => default;

    private static HttpResponseMessage AdminPermission() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"permission": "admin", "role_name": "admin"}"""),
    };

    /// <summary>One stubbed 200 (queued responses) — and a fallback so any number of gate calls works.</summary>
    private static void StubPermission200(PollerTestSupport.StubHandler handler)
    {
        handler.EnqueueResponse(AdminPermission());
        handler.Fallback = _ => AdminPermission();
    }

    // ---- claim + prepare ----

    [Fact]
    public async Task Claim_TransitionsTaskAndJobAndPreparesRun()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        version.ContainerImage = "agent-planner:v1";
        await db.SaveChangesAsync();
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);
        StubPermission200(harness.Handler);

        db.ChangeTracker.Clear();
        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Processing, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.NotNull(job.LockedUntil);
        Assert.NotNull(job.LockedBy);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Claimed, task.Status);
        Assert.NotNull(task.ClaimedAt);
        var run = await db.Runs.AsNoTracking().SingleAsync(r => r.TaskId == task.Id);
        Assert.Equal(RunStatuses.Pending, run.Status);
        Assert.Equal(1, run.AttemptNumber);
        var session = await db.AgentSessions.AsNoTracking().SingleAsync(s => s.RunId == run.Id);
        Assert.Equal(AgentSessionStatuses.Provisioning, session.Status);
        Assert.Equal(task.Id, session.TaskId);
        var container = await db.ContainerInstances.AsNoTracking().SingleAsync(c => c.AgentSessionId == session.Id);
        Assert.Equal(ContainerDesiredStates.Running, container.DesiredState);
        Assert.Equal(ContainerObservedStates.Unknown, container.ObservedState);
        Assert.Equal("agent-planner:v1", container.ContainerImage);
        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == seeded.Command!.Id);
        Assert.Equal(AgentCommandStatuses.Queued, command.Status); // untouched while waiting on the run
        Assert.Equal(1, harness.Metrics.ActiveRuns);
        Assert.Equal(1, harness.Metrics.JobsClaimedTotal);
    }

    [Fact]
    public async Task QueuedTasksStayQueued_WhenNoWork()
    {
        await postgres.ResetAsync();
        var harness = OrchestrationTestSupport.NewWorker(postgres);
        await harness.Worker.RunCycleAsync(TestToken);
        await harness.Worker.RunCycleAsync(TestToken);
        Assert.Equal(0, harness.Metrics.JobsClaimedTotal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CapLimitsClaims(int cap)
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var (repo, wiA, _) = await OrchestrationTestSupport.SeedWorkItemAsync(db);
        var (_, wiB, _) = await OrchestrationTestSupport.SeedWorkItemAsync(db, existingRepository: repo);
        await OrchestrationTestSupport.SeedQueuedAsync(db, wiA, version, withRun: false);
        await OrchestrationTestSupport.SeedQueuedAsync(db, wiB, version, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres, maxConcurrentRuns: cap);
        StubPermission200(harness.Handler);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(cap, harness.Metrics.ActiveRuns);
        Assert.Equal(cap, harness.Metrics.JobsClaimedTotal);
        Assert.Equal(2 - cap, harness.Metrics.PendingJobs);
    }

    [Fact]
    public async Task CapCountsStartingAndRunningRuns()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var (repo, wiA, _) = await OrchestrationTestSupport.SeedWorkItemAsync(db);
        var (_, wiB, _) = await OrchestrationTestSupport.SeedWorkItemAsync(db, existingRepository: repo);
        var claimed = await OrchestrationTestSupport.SeedQueuedAsync(db, wiA, version, withRun: false);
        claimed.Task.Status = OrchestrationTaskStatuses.Running;
        claimed.Task.StartedAt = DateTime.UtcNow;
        claimed.Job.Status = OrchestrationJobStatuses.Processing;
        var runningRun = new Run
        { Id = Guid.NewGuid(), TaskId = claimed.Task.Id, AttemptNumber = 1, Status = RunStatuses.Running };
        db.Runs.Add(runningRun);
        await db.SaveChangesAsync();
        await OrchestrationTestSupport.SeedQueuedAsync(db, wiB, version, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres, maxConcurrentRuns: 1);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, harness.Metrics.JobsClaimedTotal); // the running Run holds the only slot
        Assert.Equal(1, harness.Metrics.ActiveRuns);
    }

    [Fact]
    public async Task ClaimFifoOrder_FollowsAvailableAtThenPriority()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var (repo, wiOlder, _) = await OrchestrationTestSupport.SeedWorkItemAsync(db);
        var (_, wiNewer, _) = await OrchestrationTestSupport.SeedWorkItemAsync(db, existingRepository: repo);
        await OrchestrationTestSupport.SeedQueuedAsync(db, wiOlder, version, withRun: false, availableAt: DateTime.UtcNow.AddMinutes(-5));
        await OrchestrationTestSupport.SeedQueuedAsync(db, wiNewer, version, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres, maxConcurrentRuns: 1);
        StubPermission200(harness.Handler);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var claimedTask = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Status == OrchestrationTaskStatuses.Claimed);
        Assert.Equal(wiOlder.Id, (await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == claimedTask.WorkflowId)).WorkItemId);

        // ---- priority tie-break: same available_at, higher priority first (descending) ----
        await postgres.ResetAsync();
        await using var db2 = postgres.NewDbContext();
        var version2 = await OrchestrationTestSupport.SeedVersionAsync(db2, ProfileVersionStates.Ready);
        var (repo2, wiLow, _) = await OrchestrationTestSupport.SeedWorkItemAsync(db2);
        var (_, wiHigh, _) = await OrchestrationTestSupport.SeedWorkItemAsync(db2, existingRepository: repo2);
        var at = DateTime.UtcNow;
        await OrchestrationTestSupport.SeedQueuedAsync(db2, wiLow, version2, withRun: false, availableAt: at, priority: 0);
        await OrchestrationTestSupport.SeedQueuedAsync(db2, wiHigh, version2, withRun: false, availableAt: at, priority: 5);
        db2.ChangeTracker.Clear();
        var harness2 = OrchestrationTestSupport.NewWorker(postgres, maxConcurrentRuns: 1);
        StubPermission200(harness2.Handler);

        await harness2.Worker.RunCycleAsync(TestToken);

        db2.ChangeTracker.Clear();
        var claimed = await db2.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Status == OrchestrationTaskStatuses.Claimed);
        Assert.Equal(wiHigh.Id, (await db2.Workflows.AsNoTracking().SingleAsync(w => w.Id == claimed.WorkflowId)).WorkItemId);
    }

    [Fact]
    public async Task ConcurrentWorkers_NeverDoubleClaim()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        db.ChangeTracker.Clear();

        var harnessA = OrchestrationTestSupport.NewWorker(postgres);
        var harnessB = OrchestrationTestSupport.NewWorker(postgres);
        StubPermission200(harnessA.Handler);
        StubPermission200(harnessB.Handler);
        await Task.WhenAll(harnessA.Worker.RunCycleAsync(TestToken), harnessB.Worker.RunCycleAsync(TestToken));

        db.ChangeTracker.Clear();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync();
        Assert.Equal(1, job.Attempts);
        Assert.Equal(1, await db.Runs.AsNoTracking().CountAsync());
        Assert.Equal(1, await db.OrchestrationTasks.AsNoTracking().CountAsync(t => t.Status == OrchestrationTaskStatuses.Claimed));
    }

    // ---- lease expiry ----

    [Fact]
    public async Task ExpiredLease_ReturnsJobAndTaskAndDeletesPendingRun()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: true);
        // simulate a crashed claim: processing job with an expired lease, task claimed
        seeded.Job.Status = OrchestrationJobStatuses.Processing;
        seeded.Job.LockedUntil = DateTime.UtcNow.AddMinutes(-1);
        seeded.Job.LockedBy = "ghost";
        seeded.Job.Attempts = 1;
        seeded.Task.Status = OrchestrationTaskStatuses.Claimed;
        seeded.Task.ClaimedAt = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Pending, job.Status);
        Assert.Null(job.LockedUntil);
        Assert.Null(job.LockedBy);
        Assert.Equal("claim lease expired", job.LastError);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
        Assert.Null(task.ClaimedAt);
        Assert.Empty(await db.Runs.AsNoTracking().Where(r => r.TaskId == task.Id).ToListAsync());
        Assert.Empty(await db.AgentSessions.AsNoTracking().ToListAsync()); // cascaded away with the run
        Assert.Empty(await db.ContainerInstances.AsNoTracking().ToListAsync());
        Assert.Equal(1, harness.Metrics.LeaseExpirationsTotal);
    }

    [Fact]
    public async Task ExpiredLease_AfterHandoff_RestoresHandoffState_NeverRewinds()
    {
        // task 16: the crashed claim had already handed the run off (the Runtime API flipped it out of
        // pending inside /start) — the sweep must restore the hand-off state instead of rewinding the
        // task to queued, otherwise the settled Run would be prepared/dispatched a second time
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: true);
        seeded.Run!.Status = RunStatuses.Starting; // the Runtime API's /start write-back
        seeded.Run.StartedAt = DateTime.UtcNow.AddMinutes(-1);
        seeded.Job.Status = OrchestrationJobStatuses.Processing;
        seeded.Job.LockedUntil = DateTime.UtcNow.AddMinutes(-1);
        seeded.Job.LockedBy = "ghost";
        seeded.Job.Attempts = 1;
        seeded.Task.Status = OrchestrationTaskStatuses.Claimed;
        seeded.Task.ClaimedAt = DateTime.UtcNow.AddMinutes(-5);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(default);

        db.ChangeTracker.Clear();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Completed, job.Status); // hand-off restored, not rewound
        Assert.Null(job.LockedUntil);
        Assert.Null(job.LastError);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Starting, task.Status);
        Assert.NotNull(task.StartedAt);
        Assert.Equal(1, await db.Runs.AsNoTracking().CountAsync(r => r.TaskId == task.Id)); // no second run
        Assert.Equal(RunStatuses.Starting, (await db.Runs.AsNoTracking().SingleAsync(r => r.TaskId == task.Id)).Status);
        Assert.Single(await db.AgentSessions.AsNoTracking().ToListAsync()); // untouched

        // the terminal-Run consumer then owns the transition — a settled Run is consumed exactly once
        await db.Runs.Where(r => r.TaskId == task.Id).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.Status, RunStatuses.Succeeded)
            .SetProperty(r => r.CompletedAt, DateTime.UtcNow));
        var consumer = new RunTerminalConsumer(harness.Services.GetRequiredService<IServiceScopeFactory>());
        await consumer.RunCycleAsync(default);
        db.ChangeTracker.Clear();
        Assert.Equal(OrchestrationTaskStatuses.Succeeded, (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id)).Status);
        Assert.Equal(WorkflowStatuses.WaitingForHuman, (await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == seeded.Workflow.Id)).Status);
    }

    [Fact]
    public async Task ExpiredLease_StoppedTask_JobCancelledNotResurrected()
    {
        // task 16: /stop cancelled the claimed task while a hung claim held the lease — the sweep must
        // not resurrect cancelled work into the queue (it would only re-fail the job on the next claim)
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: true);
        seeded.Task.Status = OrchestrationTaskStatuses.Cancelled;
        seeded.Task.CancelledAt = DateTime.UtcNow.AddMinutes(-4);
        seeded.Job.Status = OrchestrationJobStatuses.Processing;
        seeded.Job.LockedUntil = DateTime.UtcNow.AddMinutes(-1);
        seeded.Job.LockedBy = "ghost";
        seeded.Job.Attempts = 1;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(default);

        db.ChangeTracker.Clear();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Cancelled, job.Status);
        Assert.Null(job.LockedUntil);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id)).Status);
        Assert.Empty(await db.Runs.AsNoTracking().Where(r => r.TaskId == seeded.Task.Id).ToListAsync());
        Assert.Equal(1, harness.Metrics.LeaseExpirationsTotal);
    }

    [Fact]
    public async Task ExpiredLease_DoesNotTouchTerminalTask()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        seeded.Task.Status = OrchestrationTaskStatuses.Failed;
        seeded.Task.FailureReason = "startup_timeout";
        seeded.Task.CompletedAt = DateTime.UtcNow;
        seeded.Job.Status = OrchestrationJobStatuses.Processing;
        seeded.Job.LockedUntil = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Failed, task.Status); // never rolled back
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Pending, job.Status); // swept back to pending, damped for this tick

        // a second tick claims it — the claim guard refuses (terminal task) — no Run, no double-claim churn
        await harness.Worker.RunCycleAsync(TestToken);
        db.ChangeTracker.Clear();
        var finalJob = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Failed, finalJob.Status);
        Assert.Contains("queued", finalJob.LastError);
        Assert.Equal(OrchestrationTaskStatuses.Failed,
            (await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id)).Status);
    }

    [Fact]
    public async Task ClaimRefusesJobWhoseTaskIsNotQueued()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        seeded.Task.Status = OrchestrationTaskStatuses.Failed; // e.g. failed before any new Run
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Failed, job.Status);
        Assert.Contains("queued", job.LastError);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Failed, task.Status);
        Assert.Empty(await db.Runs.AsNoTracking().ToListAsync()); // no Run ever starts while the Task is Failed
    }

    // ---- pause ----

    [Fact]
    public async Task ClaimSkipsPausedRepository_AndResumesAfterReenable()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db, enabled: false);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Pending, job.Status); // skipped, not cancelled
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
        Assert.Empty(await db.Runs.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OutboxActions.AsNoTracking().ToListAsync());

        // re-enabling resumes processing
        var tracked = await db.Repositories.SingleAsync(r => r.Id == repo.Id);
        tracked.IsEnabled = true;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        StubPermission200(harness.Handler);
        await harness.Worker.RunCycleAsync(TestToken);
        db.ChangeTracker.Clear();
        Assert.Equal(1, harness.Metrics.JobsClaimedTotal);
    }

    // ---- claim-time rechecks (cancel-or-release, never execute) ----

    [Fact]
    public async Task Recheck_WorkItemClosed_CancelsWithReason()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        workItem.State = "closed";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, task.Status);
        Assert.Equal(AgentCommandRejectionReasons.WorkItemClosed, task.FailureReason);
        Assert.NotNull(task.CancelledAt);
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Cancelled, job.Status);
        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == seeded.Command!.Id);
        Assert.Equal(AgentCommandStatuses.Failed, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.WorkItemClosed, command.RejectionReason);
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == seeded.Workflow.Id);
        Assert.Equal(WorkflowStatuses.WaitingForHuman, workflow.Status);
        Assert.Null(workflow.CurrentTaskId);
        var notice = await db.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Contains("closed", notice.Payload);
        Assert.Equal(1, harness.Metrics.ClaimCancellationsTotal);
    }

    [Fact]
    public async Task Recheck_ReviewShaDrift_CancelsReviewHeadChanged()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db, pullRequest: true);
        workItem.HeadSha = "new-sha";
        workItem.BaseSha = "base";
        await db.SaveChangesAsync();
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(
            db, workItem, version, commandName: AgentCommandNames.Review, targetHeadSha: "old-sha", withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(AgentCommandRejectionReasons.ReviewHeadChanged, task.FailureReason);
        Assert.Equal("old-sha", task.TargetHeadSha); // the pinned SHA is never rewritten
        var notice = await db.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Contains("run /review again", notice.Payload);
    }

    [Fact]
    public async Task Recheck_ProfileVersionNotReady_CancelsAgentImageNotReady()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        // the rule's version was ready at admission; its state drifted to building before the claim
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        version.State = ProfileVersionStates.Building;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, task.Status);
        Assert.Equal(AgentCommandRejectionReasons.AgentImageNotReady, task.FailureReason);
        Assert.Empty(await db.Runs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Recheck_ProfileVersionDisabled_CancelsAgentProfileNotConfigured()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        version.State = ProfileVersionStates.Disabled;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(AgentCommandRejectionReasons.AgentProfileNotConfigured, task.FailureReason);
    }

    [Fact]
    public async Task Recheck_MissingPlanInput_CancelsPlanNotAvailable()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        // implement task whose approved_plan input vanished after admission (data bug scenario)
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(
            db, workItem, version, commandName: AgentCommandNames.Code, linkPlanInput: false, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, task.Status);
        Assert.Equal(AgentCommandRejectionReasons.PlanNotAvailable, task.FailureReason);
    }

    [Fact]
    public async Task Recheck_CodeWithPlanInput_ClaimsAndPrepares()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        await OrchestrationTestSupport.SeedQueuedAsync(
            db, workItem, version, commandName: AgentCommandNames.Code, linkPlanInput: true, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);
        StubPermission200(harness.Handler);

        await harness.Worker.RunCycleAsync(TestToken);

        Assert.Equal(1, harness.Metrics.JobsClaimedTotal);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, AgentCommandRejectionReasons.UserNotCollaborator)]
    [InlineData(HttpStatusCode.OK, AgentCommandRejectionReasons.InsufficientPermission)]
    public async Task Recheck_PermissionRevoked_Cancels(HttpStatusCode statusCode, string expectedReason)
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);
        if (statusCode == HttpStatusCode.OK)
            harness.Handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"permission": "read", "role_name": "read"}"""),
            });
        else
            harness.Handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, task.Status);
        Assert.Equal(expectedReason, task.FailureReason);
        Assert.Equal(expectedReason,
            (await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == seeded.Command!.Id)).RejectionReason);
        Assert.Equal(1, harness.Metrics.ClaimCancellationsTotal);
    }

    [Fact]
    public async Task Recheck_AllowlistRevoked_CancelsNotInAllowlist()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        const string config = """{"allowlist": {"enabled": true, "logins": ["bob"]}, "permitted_commands": ["plan","status"], "permitted_work_item_types": ["issue"], "authorization": {"minimum_permission": "write"}}""";
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db, configuration: config, authorLogin: "alice");
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(AgentCommandRejectionReasons.NotInAllowlist, task.FailureReason);
        // allowlist denial is config-driven: no GitHub call was queued or needed
        Assert.Equal(1, harness.Metrics.ClaimCancellationsTotal);
    }

    [Fact]
    public async Task Recheck_PermissionTransient_ReleasesClaim()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);
        harness.Handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Pending, job.Status);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
        Assert.Equal(AgentCommandStatuses.Queued,
            (await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == seeded.Command!.Id)).Status);
        Assert.Equal(0, harness.Metrics.ClaimCancellationsTotal);
        Assert.Empty(await db.Runs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Recheck_WorkflowFailedWhileQueued_CancelsWorkflowFailed()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        seeded.Workflow.Status = WorkflowStatuses.Failed;
        seeded.Workflow.FailureReason = "workflow_corruption";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, task.Status);
        Assert.Equal(AgentCommandRejectionReasons.WorkflowFailed, task.FailureReason);
    }

    [Fact]
    public async Task Recheck_WorkflowClosedWhileQueued_CancelsWorkItemClosed()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        seeded.Workflow.Status = WorkflowStatuses.Closed;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, task.Status);
        Assert.Equal(AgentCommandRejectionReasons.WorkItemClosed, task.FailureReason);
    }

    // ---- cold-start gate ----

    [Fact]
    public async Task ColdGate_BlocksClaimingUntilSynchronized()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres, synchronized: false);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Pending, job.Status);
        Assert.Equal(0, harness.Metrics.JobsClaimedTotal);
    }

    [Fact]
    public async Task Claim_WaitsForJobsAvailableInTheFuture()
    {
        // the automatic retry's backoff rides job.available_at — a pending job scheduled in
        // the future is never claimed before its time (FIFO by available_at)
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false,
            availableAt: DateTime.UtcNow.AddSeconds(30));
        db.ChangeTracker.Clear();
        var harness = OrchestrationTestSupport.NewWorker(postgres);
        StubPermission200(harness.Handler);

        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.Id == seeded.Job.Id);
        Assert.Equal(OrchestrationJobStatuses.Pending, job.Status);
        Assert.Equal(0, harness.Metrics.JobsClaimedTotal);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == seeded.Task.Id);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
    }
}
