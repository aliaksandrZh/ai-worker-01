namespace PiAgent.Hosts.Tests.Orchestration;

using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

/// <summary>
/// Live-postgres constraint behavior behind the admission/claim paths (task 07): the partial unique
/// index `ux_task_one_unfinished_per_workflow` as the invariant backstop, attempt/session-uniqueness,
/// and the RunPreparation serialization hook (a Workflow's next Run never starts while a previous Run
/// of the same Workflow is non-terminal — decision 60).
/// </summary>
public class OrchestrationConstraintTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task PartialUniqueIndex_RejectsSecondUnfinishedTaskPerWorkflow()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var first = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);

        db.ChangeTracker.Clear();
        var tracked = await db.Workflows.SingleAsync(w => w.Id == first.Workflow.Id);
        var second = new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = tracked.Id,
            Sequence = 2,
            Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id,
            Status = OrchestrationTaskStatuses.Queued,
            CreatedAt = DateTime.UtcNow,
            RetryPolicy = "{}",
        };
        db.OrchestrationTasks.Add(second);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // a second TERMINAL task is fine
        db.ChangeTracker.Clear();
        var terminal = await db.Workflows.SingleAsync(w => w.Id == first.Workflow.Id);
        var succeeded = new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = terminal.Id,
            Sequence = 2,
            Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id,
            Status = OrchestrationTaskStatuses.Succeeded,
            CreatedAt = DateTime.UtcNow,
            RetryPolicy = "{}",
        };
        db.OrchestrationTasks.Add(succeeded);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task UniqueTaskSequence_RejectsDuplicate()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var first = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);

        db.ChangeTracker.Clear();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == first.Workflow.Id);
        var duplicate = new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Sequence = 1, Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id, Status = OrchestrationTaskStatuses.Succeeded,
            CreatedAt = DateTime.UtcNow, RetryPolicy = "{}",
        };
        db.OrchestrationTasks.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task UniqueRunAttempt_RejectsDuplicate()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: true);
        db.Runs.Add(new Run
        {
            Id = Guid.NewGuid(),
            TaskId = seeded.Task.Id,
            AttemptNumber = 1, // duplicate of the seeded attempt
            Status = RunStatuses.Pending,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task AgentSession_RunIdUnique_RejectsDuplicate()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: true);
        db.AgentSessions.Add(new AgentSession
        {
            Id = Guid.NewGuid(),
            RunId = seeded.Run!.Id, // duplicate: one-shot 1:1 session per Run (decision 57)
            TaskId = seeded.Task.Id,
            AgentProfileVersionId = version.Id,
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TaskArtifactInput_UniquePair_RejectsDuplicate()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(
            db, workItem, version, commandName: AgentCommandNames.Code, linkPlanInput: true, withRun: false);
        var input = await db.TaskArtifactInputs.AsNoTracking().SingleAsync(i => i.TaskId == seeded.Task.Id);
        db.TaskArtifactInputs.Add(new TaskArtifactInput
        { Id = Guid.NewGuid(), TaskId = seeded.Task.Id, ArtifactId = input.ArtifactId, Purpose = "duplicate" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task TriggerCommandId_PartialUnique()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        db.ChangeTracker.Clear();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == seeded.Workflow.Id);
        var hijack = new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = workflow.Id, Sequence = 9, Type = OrchestrationTaskTypes.TestDesign,
            AgentProfileVersionId = version.Id, Status = OrchestrationTaskStatuses.Succeeded,
            TriggerCommandId = seeded.Command!.Id, // the same command must never produce a second Task
            CreatedAt = DateTime.UtcNow, RetryPolicy = "{}",
        };
        db.OrchestrationTasks.Add(hijack);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task RunPrepare_SerializationHook_RefusesWhenWorkflowBusy()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: true);
        // decision 60: the previous Run of this Workflow is still non-terminal
        seeded.Run!.Status = RunStatuses.Running;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var task = await db.OrchestrationTasks.SingleAsync(t => t.Id == seeded.Task.Id);
        var refused = await RunPrepare.PrepareAsync(db, task, version, default);
        Assert.Null(refused);
        Assert.Equal(1, await db.Runs.AsNoTracking().CountAsync(r => r.TaskId == task.Id)); // no second run row

        // the Workflow's Run becomes terminal → preparation proceeds
        db.ChangeTracker.Clear();
        var terminal = await db.Runs.SingleAsync(r => r.TaskId == seeded.Task.Id);
        terminal.Status = RunStatuses.Succeeded;
        terminal.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var reloadedTask = await db.OrchestrationTasks.SingleAsync(t => t.Id == seeded.Task.Id);
        var prepared = await RunPrepare.PrepareAsync(db, reloadedTask, version, default);
        Assert.NotNull(prepared);
        Assert.Equal(2, prepared!.Run.AttemptNumber);
        Assert.NotEqual(default, prepared.Session.RunId);
        Assert.Equal(ContainerDesiredStates.Running, prepared.Container.DesiredState);
    }

    /// <summary>Task 16 (decision 60, Workflow-scoped serialization across restarts): after /stop the
    /// cancelled Task's Run stays `stopping` until the Bridge's terminal `cancelled` callback is
    /// persisted — the next command's Task (any type) must not prepare a Run before that, even though
    /// the Workflow no longer holds an unfinished Task; the gate reads PostgreSQL state, so it holds
    /// across any process restart, and opens exactly when the terminal callback is persisted.</summary>
    [Fact]
    public async Task RunPrepare_SerializationGate_HoldsAcrossTasksAndRestarts()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        // the Workflow's stopped plan Task: /stop cancelled it; its Run awaits the terminal callback
        var plan = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: true);
        plan.Task.Status = OrchestrationTaskStatuses.Cancelled;
        plan.Task.CancelledAt = DateTime.UtcNow;
        plan.Run!.Status = RunStatuses.Stopping;
        // a second Task of the SAME Workflow (/code) after the stop — no unfinished Task remains
        var code = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version,
            commandName: AgentCommandNames.Code, withCommand: true, withRun: false, linkPlanInput: true);
        db.ChangeTracker.Clear();

        var codeTask = await db.OrchestrationTasks.SingleAsync(t => t.Id == code.Task.Id);
        var refused = await RunPrepare.PrepareAsync(db, codeTask, version, default);
        Assert.Null(refused); // the stopping Run has not settled — the code Run never starts
        Assert.Equal(0, await db.Runs.AsNoTracking().CountAsync(r => r.TaskId == code.Task.Id));

        // the `cancelled` terminal callback was persisted → the gate opens for a different Task type
        db.ChangeTracker.Clear();
        await db.Runs.Where(r => r.Id == plan.Run.Id).ExecuteUpdateAsync(set => set
            .SetProperty(r => r.Status, RunStatuses.Cancelled)
            .SetProperty(r => r.CompletedAt, DateTime.UtcNow));
        db.ChangeTracker.Clear();
        var reloadedCodeTask = await db.OrchestrationTasks.SingleAsync(t => t.Id == code.Task.Id);
        var prepared = await RunPrepare.PrepareAsync(db, reloadedCodeTask, version, default);
        Assert.NotNull(prepared);
        Assert.Equal(OrchestrationTaskTypes.Implement, prepared!.Task.Type);
        Assert.Equal(1, prepared.Run.AttemptNumber); // the new Task's first attempt
    }
}
