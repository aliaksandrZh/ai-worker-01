using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

namespace PiAgent.Hosts.Tests.Orchestration;

/// <summary>
/// The AI-scope labels (AI:Plan / AI:Code / AI:Review / AI:Test) as the standing convergence gate:
/// task-producing commands without the exact label are silently ignored (stored rejected, no
/// issue reply), the board/plan and review system triggers require the label too, and the
/// LabelTriggerCycle scanner starts the mapped task type when the label appears and all
/// conditions converge — with the owner's run-decision rule (no previous run → run; previous run
/// with no new human signal → no-new-input; failed/cancelled previous run → /retry owns it).
/// </summary>
public class AiScopeLabelTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Bot = "jsxhuz";
    private const string AllScopeLabels = """["AI:Plan","AI:Code","AI:Review","AI:Test"]""";
    private const string NoLabels = "[]";
    private static CancellationToken TestToken => default;

    private static LabelTriggerCycle NewScanner(PostgresFixture fixture, bool synchronized = true)
    {
        var configuration = new ConfigurationState();
        if (synchronized)
            configuration.MarkSynchronized(Bot, 10);
        return new LabelTriggerCycle(
            new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options
                    .UseNpgsql(fixture.ConnectionString)
                    .UseSnakeCaseNamingConvention())
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(),
            configuration, NullLogger<LabelTriggerCycle>.Instance);
    }

    private static async Task SetLabelsAsync(AppDbContext db, WorkItem workItem, string labelsJson)
    {
        await db.WorkItems.Where(w => w.Id == workItem.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.Labels, labelsJson));
        db.ChangeTracker.Clear();
    }

    /// <summary>The board snapshot saying the item currently sits in `status` (the Plan condition).</summary>
    private static async Task SeedBoardStatusAsync(AppDbContext db, WorkItem workItem, string status)
    {
        var sync = new ProjectSync { Id = Guid.NewGuid(), Owner = "octo-org", ProjectNumber = 6, UpdatedAt = DateTime.UtcNow };
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
            FieldValues = $$"""[{"id":1,"name":"Status","value":"{{status}}"}]""",
            CreatedAtGithub = DateTime.UtcNow,
            UpdatedAtGithub = DateTime.UtcNow,
            LastSyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    // ---- the command gate ----

    [Fact]
    public async Task CommandWithoutScopeLabel_SilentlyIgnored_NoReply()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, body: "@jsxhuz /plan");
        await CommandTestSupport.SeedCommandEventAsync(db, comment, workItem);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await SetLabelsAsync(db, workItem, NoLabels);
        db.ChangeTracker.Clear();

        var harness = CommandTestSupport.NewProcessor(postgres.ConnectionString);
        harness.Handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"permission": "admin", "role_name": "admin"}"""),
        });
        await harness.Processor.RunCycleAsync(default);

        await using var verify = postgres.NewDbContext();
        var command = await verify.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.LabelScopeMissing, command.RejectionReason);
        // silent: nothing in the outbox at all — no reply, no lifecycle label
        Assert.Empty(await verify.OutboxActions.AsNoTracking().ToListAsync());
        Assert.Equal(0, await verify.OrchestrationTasks.CountAsync());
    }

    [Fact]
    public async Task AdmittedCommand_WithScopeLabel_FiresTheLifecycleLabel()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, body: "@jsxhuz /plan");
        await CommandTestSupport.SeedCommandEventAsync(db, comment, workItem);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await SetLabelsAsync(db, workItem, AllScopeLabels);
        db.ChangeTracker.Clear();

        var harness = CommandTestSupport.NewProcessor(postgres.ConnectionString);
        harness.Handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"permission": "admin", "role_name": "admin"}"""),
        });
        await harness.Processor.RunCycleAsync(default);

        await using var verify = postgres.NewDbContext();
        Assert.Equal(AgentCommandStatuses.Queued,
            (await verify.AgentCommands.AsNoTracking().SingleAsync()).Status);
        var label = Assert.Single(await verify.OutboxActions.AsNoTracking()
            .Where(a => a.ActionType == OutboxActionTypes.AiLabel).ToListAsync());
        using var payload = JsonDocument.Parse(label.Payload);
        Assert.Equal(AiStateLabels.Scheduled, payload.RootElement.GetProperty("label").GetString());
    }

    // ---- the scanner ----

    [Fact]
    public async Task Scanner_LabelPlusClarifying_StartsThePlan()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await SetLabelsAsync(db, workItem, """["AI:Plan"]""");
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await SeedBoardStatusAsync(db, workItem, "Clarifying");

        await NewScanner(postgres).RunCycleAsync(TestToken);
        await NewScanner(postgres).RunCycleAsync(TestToken); // idempotent: the queued task occupies the slot

        await using var verify = postgres.NewDbContext();
        var task = await verify.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskTypes.Plan, task.Type);
        Assert.Null(task.TriggerCommandId);
        Assert.Null(task.TriggerGithubEventId); // scanner-created (the relaxed trigger check)
        Assert.NotNull(task.TriggerAtGithub);
        var workflow = await verify.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == workItem.Id);
        Assert.Equal(WorkflowStatuses.Active, workflow.Status);
        var label = Assert.Single(await verify.OutboxActions.AsNoTracking()
            .Where(a => a.ActionType == OutboxActionTypes.AiLabel).ToListAsync());
        using var payload = JsonDocument.Parse(label.Payload);
        Assert.Equal(AiStateLabels.Scheduled, payload.RootElement.GetProperty("label").GetString());
    }

    [Fact]
    public async Task Scanner_LabelWithoutClarifying_StartsNothing()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await SetLabelsAsync(db, workItem, """["AI:Plan"]""");
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await SeedBoardStatusAsync(db, workItem, "Backlog"); // everything does NOT converge

        await NewScanner(postgres).RunCycleAsync(TestToken);

        await using var verify = postgres.NewDbContext();
        Assert.Equal(0, await verify.OrchestrationTasks.CountAsync());
        Assert.Empty(await verify.OutboxActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task Scanner_NoLabelWhileInClarifying_StartsNothing()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await SetLabelsAsync(db, workItem, NoLabels);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await SeedBoardStatusAsync(db, workItem, "Clarifying");

        await NewScanner(postgres).RunCycleAsync(TestToken);

        await using var verify = postgres.NewDbContext();
        Assert.Equal(0, await verify.OrchestrationTasks.CountAsync());
    }

    [Fact]
    public async Task Scanner_PreviousPlanWithoutNewSignal_NoNewInput()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await SetLabelsAsync(db, workItem, """["AI:Plan"]""");
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await SeedBoardStatusAsync(db, workItem, "Clarifying");
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        var workflowId = seeded.Workflow.Id;
        // the anchor AFTER the seed's comment (-5 min): nothing human happened since the run
        await db.OrchestrationTasks.Where(t => t.Id == seeded.Task.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, OrchestrationTaskStatuses.Succeeded)
                .SetProperty(t => t.TriggerAtGithub, DateTime.UtcNow.AddMinutes(-1)));
        db.ChangeTracker.Clear();

        await NewScanner(postgres).RunCycleAsync(TestToken);

        await using var verify = postgres.NewDbContext();
        Assert.Equal(1, await verify.OrchestrationTasks.CountAsync()); // the seeded one only
    }

    [Fact]
    public async Task Scanner_PreviousPlanWithNewHumanSignal_RunsAgain()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await SetLabelsAsync(db, workItem, """["AI:Plan"]""");
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await SeedBoardStatusAsync(db, workItem, "Clarifying");
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        var anchor = DateTime.UtcNow.AddMinutes(-10);
        await db.OrchestrationTasks.Where(t => t.Id == seeded.Task.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, OrchestrationTaskStatuses.Succeeded)
                .SetProperty(t => t.TriggerAtGithub, anchor));
        // a human spoke after the run's anchor
        db.GitHubComments.Add(new GitHubComment
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            GithubCommentId = 99001,
            Kind = GitHubCommentKinds.IssueComment,
            AuthorLogin = "alice",
            Body = "please re-check the plan",
            CreatedAtGithub = anchor.AddMinutes(1),
            UpdatedAtGithub = anchor.AddMinutes(1),
            LastSyncedAt = DateTime.UtcNow,
            IsDeleted = false,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewScanner(postgres).RunCycleAsync(TestToken);

        await using var verify = postgres.NewDbContext();
        Assert.Equal(2, await verify.OrchestrationTasks.CountAsync());
        Assert.Equal(2, (await verify.OrchestrationTasks.AsNoTracking().OrderBy(t => t.Sequence).LastAsync()).Sequence);
    }

    [Fact]
    public async Task Scanner_PreviousPlanFailed_OwnedByRetry_NeverReTriggered()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await SetLabelsAsync(db, workItem, """["AI:Plan"]""");
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await SeedBoardStatusAsync(db, workItem, "Clarifying");
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        await db.OrchestrationTasks.Where(t => t.Id == seeded.Task.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, OrchestrationTaskStatuses.Failed));
        db.ChangeTracker.Clear();

        await NewScanner(postgres).RunCycleAsync(TestToken);

        await using var verify = postgres.NewDbContext();
        Assert.Equal(1, await verify.OrchestrationTasks.CountAsync());
    }

    [Fact]
    public async Task Scanner_CodeLabel_NeedsThePlanArtifact()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await SetLabelsAsync(db, workItem, """["AI:Code"]""");
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Implement);

        await NewScanner(postgres).RunCycleAsync(TestToken);
        await using var check = postgres.NewDbContext();
        Assert.Equal(0, await check.OrchestrationTasks.CountAsync()); // no successful plan artifact yet

        // a successful plan task with its plan artifact → the code run is approved
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, workItem, version, withRun: false);
        var artifact = new Artifact
        {
            Id = Guid.NewGuid(),
            TaskId = seeded.Task.Id,
            WorkflowId = seeded.Workflow.Id,
            Type = ArtifactTypes.Plan,
            Format = ArtifactFormats.Markdown,
            Content = "the plan",
            CreatedAt = DateTime.UtcNow,
        };
        db.Artifacts.Add(artifact);
        await db.OrchestrationTasks.Where(t => t.Id == seeded.Task.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Type, OrchestrationTaskTypes.Plan)
                .SetProperty(t => t.Status, OrchestrationTaskStatuses.Succeeded));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await NewScanner(postgres).RunCycleAsync(TestToken);

        await using var verify = postgres.NewDbContext();
        var task = await verify.OrchestrationTasks.AsNoTracking()
            .SingleAsync(t => t.Type == OrchestrationTaskTypes.Implement);
        var input = await verify.TaskArtifactInputs.AsNoTracking().SingleAsync(i => i.TaskId == task.Id);
        Assert.Equal(artifact.Id, input.ArtifactId);
        Assert.Equal(ArtifactInputPurposes.ApprovedPlan, input.Purpose);
    }

    [Fact]
    public async Task Scanner_ReviewLabel_OpenPr_RunsOnce()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db, pullRequest: true);
        await db.WorkItems.Where(w => w.Id == workItem.Id)
            .ExecuteUpdateAsync(u => u
                .SetProperty(w => w.HeadSha, "head-1")
                .SetProperty(w => w.BaseSha, "base-1"));
        await SetLabelsAsync(db, workItem, """["AI:Review"]""");
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Review);

        await NewScanner(postgres).RunCycleAsync(TestToken);
        await NewScanner(postgres).RunCycleAsync(TestToken); // once per Workflow lifetime

        await using var verify = postgres.NewDbContext();
        var task = await verify.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskTypes.Review, task.Type);
        Assert.Equal("head-1", task.TargetHeadSha);
        Assert.NotNull((await verify.Workflows.AsNoTracking().SingleAsync()).InitialReviewCreatedAt);
    }

    [Fact]
    public async Task Scanner_TestLabel_NoRoutingRule_NeverFires()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await SetLabelsAsync(db, workItem, """["AI:Test"]"""); // test_design is not implemented

        await NewScanner(postgres).RunCycleAsync(TestToken);

        await using var verify = postgres.NewDbContext();
        Assert.Equal(0, await verify.OrchestrationTasks.CountAsync());
    }

    [Fact]
    public async Task Scanner_ColdStartGate_NothingStarts()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(db);
        await SetLabelsAsync(db, workItem, """["AI:Plan"]""");
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await SeedBoardStatusAsync(db, workItem, "Clarifying");

        var configuration = new ConfigurationState();
        configuration.MarkFailed("configuration synchronization failed");
        var scanner = new LabelTriggerCycle(
            new ServiceCollection()
                .AddDbContext<AppDbContext>(options => options
                    .UseNpgsql(postgres.ConnectionString)
                    .UseSnakeCaseNamingConvention())
                .BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(),
            configuration, NullLogger<LabelTriggerCycle>.Instance);
        await scanner.RunCycleAsync(TestToken);

        await using var verify = postgres.NewDbContext();
        Assert.Equal(0, await verify.OrchestrationTasks.CountAsync());
    }
}
