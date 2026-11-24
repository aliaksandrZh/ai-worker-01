using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

namespace PiAgent.Hosts.Tests.Orchestration;

/// <summary>
/// The Projects v2 board → trigger tests (projects.yaml `auto_plan` / `auto_code`, consumed by the
/// project-event cycle): a `project_item_updated` carrying the configured status transition admits
/// the system-owned Task of the trigger's type. Covers trigger creation + replay dedupe, deferral while the
/// Workflow is busy, the silent-consume guards (failed Workflow, explicit /plan after the
/// transition, non-matching transition, unconfigured board, no ready planner profile), and the
/// cold-start gate.
/// </summary>
public class ProjectPlanTriggerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Board = "aliaksandrZh/7";

    /// <summary>Postgres timestamptz stores microseconds; a raw UtcNow carries 100ns ticks that
    /// get truncated on the write-back — seed timestamps pre-truncated so strict equality holds.</summary>
    private static DateTime Micros(DateTime value) => new(value.Ticks / 10 * 10, DateTimeKind.Utc);
    private static readonly ProjectBoardTrigger Trigger = new("aliaksandrZh", 7, "Status", "Backlog", "Clarifying", OrchestrationTaskTypes.Plan);
    private static CancellationToken TestToken => default;

    private static ProjectEventLogCycle NewCycle(PostgresFixture fixture, bool synchronized = true,
        IReadOnlyList<ProjectBoardTrigger>? triggers = null)
    {
        var configuration = new ConfigurationState();
        if (synchronized)
        {
            configuration.SetProjectBoardTriggers(triggers ?? [Trigger]);
            configuration.MarkSynchronized("jsxhuz", 10);
        }
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(fixture.ConnectionString)
            .UseSnakeCaseNamingConvention());
        return new ProjectEventLogCycle(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            configuration, NullLogger<ProjectEventLogCycle>.Instance);
    }

    private static async Task<WorkItem> SeedIssueAsync(AppDbContext db)
    {
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        db.ChangeTracker.Clear();
        return workItem;
    }

    private static async Task<GitHubEvent> AddTransitionEventAsync(
        AppDbContext db, WorkItem workItem, string from = "Backlog", string to = "Clarifying",
        string field = "Status", string board = Board)
    {
        var githubEvent = new GitHubEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = workItem.RepositoryId,
            WorkItemId = workItem.Id,
            Source = GitHubEventSources.ProjectItemsSync,
            DedupeKey = $"test:{Guid.NewGuid()}",
            EventType = GitHubEventTypes.ProjectItemUpdated,
            Payload = JsonSerializer.Serialize(new
            {
                project = board,
                item_id = 42,
                content_type = "Issue",
                number = workItem.Number,
                repository = "octo-org/example",
                changes = new Dictionary<string, string> { [field] = $"{from} -> {to}" },
            }),
            OccurredAtGithub = Micros(DateTime.UtcNow.AddMinutes(-5)),
            ReceivedAt = DateTime.UtcNow,
            ProcessingStatus = GitHubEventStatuses.Pending,
        };
        db.GitHubEvents.Add(githubEvent);
        await db.SaveChangesAsync();
        return githubEvent;
    }

    private static void SetPending(AppDbContext db, Guid eventId) =>
        db.GitHubEvents.Where(e => e.Id == eventId)
            .ExecuteUpdate(u => u
                .SetProperty(e => e.ProcessingStatus, GitHubEventStatuses.Pending)
                .SetProperty(e => e.ProcessedAt, (DateTime?)null)
                .SetProperty(e => e.ProcessingError, (string?)null));

    private static async Task<OrchestrationTask> SetTaskSucceededAsync(AppDbContext db, Guid taskId)
    {
        await db.OrchestrationTasks.Where(t => t.Id == taskId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, OrchestrationTaskStatuses.Succeeded));
        db.ChangeTracker.Clear();
        return await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
    }

    [Fact]
    public async Task StatusTransition_CreatesSystemPlanTask()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedIssueAsync(db);
        var first = await AddTransitionEventAsync(db, workItem);

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == workItem.Id);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskTypes.Plan, task.Type);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
        Assert.Null(task.TriggerCommandId);
        Assert.Equal(first.Id, task.TriggerGithubEventId);
        Assert.Equal(first.OccurredAtGithub, task.TriggerAtGithub); // the no-new-input gate anchor
        Assert.Null(task.TargetHeadSha);
        Assert.Equal(WorkflowStatuses.Active, workflow.Status);
        Assert.Equal(task.Id, workflow.CurrentTaskId);
        Assert.NotNull(await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.TaskId == task.Id));
        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == first.Id)).ProcessingStatus);
    }

    [Fact]
    public async Task EventReplay_DoesNotDuplicateTheTask()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedIssueAsync(db);
        var first = await AddTransitionEventAsync(db, workItem);
        var cycle = NewCycle(postgres);

        await cycle.RunCycleAsync(TestToken);
        SetPending(db, first.Id); // simulate a crash replay
        await cycle.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.OrchestrationTasks.CountAsync());
    }

    [Fact]
    public async Task UnfinishedTask_DefersTheTrigger_ThenRunsWhenIdle()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedIssueAsync(db);
        var first = await AddTransitionEventAsync(db, workItem);
        var cycle = NewCycle(postgres);

        // First trigger → plan queued; the second transition (a later move) defers behind it.
        await cycle.RunCycleAsync(TestToken);
        db.ChangeTracker.Clear();
        var firstTask = await db.OrchestrationTasks.AsNoTracking().SingleAsync();
        var second = await AddTransitionEventAsync(db, workItem);
        await cycle.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(GitHubEventStatuses.Pending,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == second.Id)).ProcessingStatus);
        Assert.Contains("deferred",
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == second.Id)).ProcessingError);

        // The first plan settles; the deferred trigger now runs.
        await SetTaskSucceededAsync(db, firstTask.Id);
        await cycle.RunCycleAsync(TestToken);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(second.Id,
            (await db.OrchestrationTasks.AsNoTracking().OrderBy(t => t.Sequence).LastAsync()).TriggerGithubEventId);
    }

    [Fact]
    public async Task ExplicitPlanAfterTransition_ConsumesTheTriggerSilently()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedIssueAsync(db);
        var first = await AddTransitionEventAsync(db, workItem);
        var cycle = NewCycle(postgres);

        // The human moved the card and immediately commented /plan (later than the event).
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            Status = WorkflowStatuses.WaitingForHuman,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var command = new AgentCommand
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            GithubCommentId = 777,
            AuthorLogin = "alice",
            CommandName = AgentCommandNames.Plan,
            SourceBodySnapshot = "@jsxhuz /plan",
            CreatedAt = DateTime.UtcNow.AddMinutes(-4),
        };
        db.AgentCommands.Add(command);
        db.OrchestrationTasks.Add(new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = (await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == workItem.Id)).Id,
            Sequence = 1,
            Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id,
            Status = OrchestrationTaskStatuses.Succeeded,
            TriggerCommandId = command.Id, // the explicit /plan
            TriggerAtGithub = DateTime.UtcNow.AddMinutes(-4), // after the event's OccurredAtGithub
            CreatedAt = DateTime.UtcNow,
            RetryPolicy = "{}",
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await cycle.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == first.Id)).ProcessingStatus);
    }

    [Fact]
    public async Task StaleTransition_ItemNoLongerInTargetColumn_ConsumesTheTrigger()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedIssueAsync(db);
        var first = await AddTransitionEventAsync(db, workItem);
        // the board moved on: the synced snapshot says the item is back in Backlog
        var sync = new ProjectSync { Id = Guid.NewGuid(), Owner = "aliaksandrZh", ProjectNumber = 7, UpdatedAt = DateTime.UtcNow };
        db.ProjectSyncs.Add(sync);
        db.ProjectItems.Add(new ProjectItem
        {
            Id = Guid.NewGuid(),
            ProjectSyncId = sync.Id,
            ItemId = 42,
            ContentType = "Issue",
            RepositoryId = workItem.RepositoryId,
            RepositoryFullName = "octo-org/example",
            Number = (int)workItem.Number,
            Title = "An issue",
            FieldValues = "[{\"id\":1,\"name\":\"Status\",\"value\":\"Backlog\"}]",
            CreatedAtGithub = DateTime.UtcNow,
            UpdatedAtGithub = DateTime.UtcNow,
            LastSyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == first.Id)).ProcessingStatus);
    }

    [Fact]
    public async Task FailedWorkflow_ConsumesTheTriggerSilently()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedIssueAsync(db);
        var first = await AddTransitionEventAsync(db, workItem);
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            Status = WorkflowStatuses.Failed,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == first.Id)).ProcessingStatus);
    }

    [Fact]
    public async Task NonMatchingTransition_Consumed_NoTask()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedIssueAsync(db);
        var first = await AddTransitionEventAsync(db, workItem, from: "Clarifying", to: "Done");

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(0, await db.Workflows.CountAsync());
        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == first.Id)).ProcessingStatus);
    }

    [Fact]
    public async Task UnconfiguredBoard_Consumed_NoTask()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedIssueAsync(db);
        var first = await AddTransitionEventAsync(db, workItem, board: "someone/9");

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == first.Id)).ProcessingStatus);
    }

    [Fact]
    public async Task NoReadyPlannerProfile_Consumed_NoTask()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan,
            versionState: ProfileVersionStates.Building);
        db.ChangeTracker.Clear();
        var first = await AddTransitionEventAsync(db, workItem);

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == first.Id)).ProcessingStatus);
    }

    [Fact]
    public async Task ColdStartGate_NothingClaimed()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedIssueAsync(db);
        var first = await AddTransitionEventAsync(db, workItem);

        await NewCycle(postgres, synchronized: false).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(GitHubEventStatuses.Pending,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == first.Id)).ProcessingStatus);
    }

    [Fact]
    public async Task ProcessingOrphans_AreReleasedBeforeTheClaim()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedIssueAsync(db);
        var orphan = await AddTransitionEventAsync(db, workItem);
        await db.GitHubEvents.Where(e => e.Id == orphan.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(e => e.ProcessingStatus, GitHubEventStatuses.Processing));
        db.ChangeTracker.Clear();

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == orphan.Id)).ProcessingStatus);
    }

    // ---- auto_code (Clarifying → Ready + AI:Code → the system-owned implement Task) ----

    private static readonly ProjectBoardTrigger CodeTrigger =
        new("aliaksandrZh", 7, "Status", "Clarifying", "Ready", OrchestrationTaskTypes.Implement,
            RequiresPlanArtifact: true); // auto_code implements an APPROVED plan; auto_fix does not

    /// <summary>The coder's convergence seed: AI:Code on the item, a Workflow holding a SUCCEEDED
    /// plan Task with its plan artifact (the coder implements an approved plan), the coder profile ready.</summary>
    private static async Task<WorkItem> SeedCodeConvergenceAsync(AppDbContext db)
    {
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Implement);
        await db.WorkItems.Where(w => w.Id == workItem.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.Labels, "[\"AI:Code\"]"));
        var workflow = new Workflow
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            Status = WorkflowStatuses.WaitingForHuman,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflow);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var planTask = new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Sequence = 1,
            Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id,
            Status = OrchestrationTaskStatuses.Succeeded,
            TriggerAtGithub = DateTime.UtcNow.AddMinutes(-30),
            CreatedAt = DateTime.UtcNow,
            RetryPolicy = "{}",
        };
        db.OrchestrationTasks.Add(planTask);
        db.Artifacts.Add(new Artifact
        {
            Id = Guid.NewGuid(),
            TaskId = planTask.Id,
            WorkflowId = workflow.Id,
            Type = ArtifactTypes.Plan,
            Format = ArtifactFormats.Markdown,
            Content = "# Plan",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return workItem;
    }

    [Fact]
    public async Task CodeTransition_CreatesSystemImplementTask()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedCodeConvergenceAsync(db);
        var first = await AddTransitionEventAsync(db, workItem, from: "Clarifying", to: "Ready");

        await NewCycle(postgres, triggers: [CodeTrigger]).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == workItem.Id);
        var task = await db.OrchestrationTasks.AsNoTracking()
            .SingleAsync(t => t.Type == OrchestrationTaskTypes.Implement);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
        Assert.Null(task.TriggerCommandId);
        Assert.Equal(first.Id, task.TriggerGithubEventId);
        Assert.Equal(WorkflowStatuses.Active, workflow.Status);
        // the approved plan rides on the task — without this input the claim-time recheck
        // cancels the task (plan_not_available) and the item hangs on a stale AI Scheduled
        var input = await db.TaskArtifactInputs.AsNoTracking().SingleAsync(i => i.TaskId == task.Id);
        Assert.Equal(ArtifactInputPurposes.ApprovedPlan, input.Purpose);
        // the lifecycle label rides the same transition: AI Scheduled enqueued through the outbox
        Assert.Equal(OutboxActionTypes.AiLabel,
            (await db.OutboxActions.AsNoTracking().SingleAsync(a => a.TaskId == task.Id)).ActionType);
    }

    [Fact]
    public async Task CodeTransition_WithoutPlanArtifact_ConsumesSilently()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Implement);
        await db.WorkItems.Where(w => w.Id == workItem.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.Labels, "[\"AI:Code\"]"));
        var first = await AddTransitionEventAsync(db, workItem, from: "Clarifying", to: "Ready");
        db.ChangeTracker.Clear();

        await NewCycle(postgres, triggers: [CodeTrigger]).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(0, await db.Workflows.CountAsync());
        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == first.Id)).ProcessingStatus);
    }

    [Fact]
    public async Task CodeTransition_WithoutAiCodeLabel_ConsumesSilently()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedCodeConvergenceAsync(db);
        await db.WorkItems.Where(w => w.Id == workItem.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.Labels, "[\"other-label\"]"));
        var first = await AddTransitionEventAsync(db, workItem, from: "Clarifying", to: "Ready");
        db.ChangeTracker.Clear();

        await NewCycle(postgres, triggers: [CodeTrigger]).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync(t => t.Type == OrchestrationTaskTypes.Implement));
        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == first.Id)).ProcessingStatus);
    }
}
