using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Orchestration;
using PiAgent.Infrastructure.Persistence;
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
}
