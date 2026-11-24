using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Runtime;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

namespace PiAgent.Hosts.Tests.Orchestration;

/// <summary>
/// The review→fix loop (projects.yaml `auto_review` / `auto_fix` / `review_loop`): the card is the
/// ISSUE, the review runs on its LINKED open pull request. Covers the board triggers (the review
/// task created on the linked PR with pinned SHAs; the fix coder created with the PR's head branch
/// pinned and no plan-artifact requirement) and the review-terminal settlement (changes_requested
/// spends a fix round and moves the card back to In Progress; the cap parks the card with AI Failed
/// + the review_loop_exhausted comment; a passing review moves the card to Testing; a manual
/// task-producing command resets the round counter).
/// </summary>
public class ReviewLoopTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Board = "aliaksandrZh/7";
    private static CancellationToken TestToken => default;

    private static ConfigurationState NewConfiguration(
        (string From, string To, string TaskType, bool RequiresPlanArtifact)[]? triggers = null,
        string? processing = "In Progress", string? review = "In Review", string? testing = "Testing",
        string[]? reviewStatuses = null, int? maxRounds = null)
    {
        var configuration = new ConfigurationState();
        configuration.SetProjectBoardTriggers((triggers ?? [("In Progress", "In Review", OrchestrationTaskTypes.Review, false),
                ("In Review", "In Progress", OrchestrationTaskTypes.Implement, false)])
            .Select(t => new ProjectBoardTrigger("aliaksandrZh", 7, "Status", t.From, t.To, t.TaskType, t.RequiresPlanArtifact))
            .ToList());
        configuration.SetProjectStatusConfigs(
            [new ProjectStatusConfig("aliaksandrZh", 7, "Status", processing, review, testing,
                ReviewStatuses: reviewStatuses ?? ["In Review"])]);
        if (maxRounds is { } rounds)
            configuration.SetReviewLoopConfigs([new ReviewLoopConfig("aliaksandrZh", 7, rounds)]);
        configuration.MarkSynchronized("jsxhuz", 10);
        return configuration;
    }

    private ProjectEventLogCycle NewCycle(ConfigurationState configuration)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(postgres.ConnectionString)
            .UseSnakeCaseNamingConvention());
        return new ProjectEventLogCycle(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            configuration, NullLogger<ProjectEventLogCycle>.Instance);
    }

    private RunTerminalConsumer NewConsumer(ConfigurationState configuration) =>
        new(new ServiceCollection()
            .AddDbContext<AppDbContext>(options => options
                .UseNpgsql(postgres.ConnectionString)
                .UseSnakeCaseNamingConvention())
            .AddSingleton(configuration)
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RunTerminalConsumer>.Instance);

    private sealed record LoopSeed(
        Repository Repo, WorkItem Issue, WorkItem PullRequest, ProjectSync Sync);

    /// <summary>The loop's card setup: the issue (the card), the open PR (HeadRef pinned), the link,
    /// and the synced board row (the card's current column).</summary>
    private async Task<LoopSeed> SeedLoopAsync(AppDbContext db, string cardStatus = "In Review", string headRef = "agent/42-fix")
    {
        var (repo, issue, _) = await CommandTestSupport.SeedAsync(db);
        var pr = new WorkItem
        {
            Id = Guid.NewGuid(),
            RepositoryId = repo.Id,
            GithubNodeId = $"node-pr-{issue.Number}",
            Number = issue.Number + 1,
            Type = WorkItemTypes.PullRequest,
            Title = "The pull request",
            Body = "pr body",
            State = "open",
            AuthorLogin = "someone",
            Assignees = "[]",
            Labels = "[\"AI:Review\"]",
            HeadSha = "head-sha-1",
            HeadRef = headRef,
            BaseSha = "base-sha-1",
            CreatedAtGithub = DateTime.UtcNow.AddHours(-1),
            UpdatedAtGithub = DateTime.UtcNow,
            LastSyncedAt = DateTime.UtcNow,
        };
        db.WorkItems.Add(pr);
        db.WorkItemLinks.Add(new WorkItemLink
        {
            Id = Guid.NewGuid(),
            IssueWorkItemId = issue.Id,
            PrWorkItemId = pr.Id,
            RelationType = WorkItemLinkRelations.Closes,
            Source = WorkItemLinkSources.GithubClosingReference,
            CreatedAt = DateTime.UtcNow,
        });
        var sync = new ProjectSync { Id = Guid.NewGuid(), Owner = "aliaksandrZh", ProjectNumber = 7, UpdatedAt = DateTime.UtcNow };
        db.ProjectSyncs.Add(sync);
        db.ProjectItems.Add(new ProjectItem
        {
            Id = Guid.NewGuid(),
            ProjectSyncId = sync.Id,
            ItemId = 42,
            ContentType = "Issue",
            RepositoryId = repo.Id,
            RepositoryFullName = repo.FullName,
            Number = (int)issue.Number,
            Title = issue.Title,
            FieldValues = $"[{{\"id\":1,\"name\":\"Status\",\"value\":\"{cardStatus}\"}}]",
            CreatedAtGithub = DateTime.UtcNow,
            UpdatedAtGithub = DateTime.UtcNow,
            LastSyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return new LoopSeed(repo, issue, pr, sync);
    }

    private static async Task<GitHubEvent> AddTransitionEventAsync(
        AppDbContext db, WorkItem workItem, string from, string to, string field = "Status", string board = Board)
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
            OccurredAtGithub = new DateTime(DateTime.UtcNow.AddMinutes(-5).Ticks / 10 * 10, DateTimeKind.Utc),
            ReceivedAt = DateTime.UtcNow,
            ProcessingStatus = GitHubEventStatuses.Pending,
        };
        db.GitHubEvents.Add(githubEvent);
        await db.SaveChangesAsync();
        return githubEvent;
    }

    // ---- auto_review: the transition starts the review on the LINKED pull request ----

    [Fact]
    public async Task AutoReviewTransition_CreatesReviewTaskOnTheLinkedPullRequest()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var seed = await SeedLoopAsync(db);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Review);
        await AddTransitionEventAsync(db, seed.Issue, "In Progress", "In Review");

        await NewCycle(NewConfiguration()).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == seed.PullRequest.Id);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskTypes.Review, task.Type);
        Assert.Equal(workflow.Id, task.WorkflowId);
        Assert.Equal("head-sha-1", task.TargetHeadSha); // pinned to the PR, not the card
        Assert.Equal("base-sha-1", task.TargetBaseSha);
        Assert.NotNull(workflow.InitialReviewCreatedAt);
        // the AI Scheduled label rides the PR (the item the review works on)
        var labels = await db.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Equal(OutboxActionTypes.AiLabel, labels.ActionType);
        Assert.Contains("AI Scheduled", labels.Payload);
    }

    [Fact]
    public async Task AutoReviewTransition_NoLinkedPullRequest_ConsumedSilently()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (_, issue, _) = await CommandTestSupport.SeedAsync(db);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Review);
        await AddTransitionEventAsync(db, issue, "In Progress", "In Review");

        await NewCycle(NewConfiguration()).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(0, await db.Workflows.CountAsync());
    }

    // ---- auto_fix: the fix-round coder continues the PR branch, no plan artifact ----

    [Fact]
    public async Task AutoFixTransition_CreatesImplementTask_WithThePinnedPrBranch_AndNoPlanArtifact()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var seed = await SeedLoopAsync(db, cardStatus: "In Progress");
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Implement);
        await AddTransitionEventAsync(db, seed.Issue, "In Review", "In Progress");

        await NewCycle(NewConfiguration()).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == seed.Issue.Id);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskTypes.Implement, task.Type);
        Assert.Equal(workflow.Id, task.WorkflowId);
        // the fix Workspace is pre-created with the PR's head branch → the frozen input carries git.branch
        var workspace = await db.Workspaces.AsNoTracking().SingleAsync();
        Assert.Equal(task.Id, workspace.TaskId);
        Assert.Equal("agent/42-fix", workspace.BranchName);
        // no plan artifact: the review findings are the fix round's input
        Assert.Equal(0, await db.TaskArtifactInputs.CountAsync());
    }

    // ---- the review-terminal settlement (RunTerminalConsumer) ----

    /// <summary>A succeeded review Run on the PR's Workflow (workspace + run + terminal completed event).</summary>
    private async Task<(OrchestrationTask Task, Run Run)> SeedSucceededReviewRunAsync(
        LoopSeed seed, string outcome)
    {
        var db = postgres.NewDbContext();
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Review);
        var seeded = await OrchestrationTestSupport.SeedQueuedAsync(db, seed.PullRequest, version,
            commandName: AgentCommandNames.Review, withCommand: false, withRun: false);
        var preparation = await RunPrepare.PrepareAsync(db, seeded.Task, version, default);
        seeded.Task.Status = OrchestrationTaskStatuses.Starting;
        seeded.Job.Status = OrchestrationJobStatuses.Completed;
        preparation!.Run.Status = RunStatuses.Succeeded;
        preparation.Run.StartedAt = DateTime.UtcNow;
        preparation.Run.CompletedAt = DateTime.UtcNow;
        preparation.Run.ResultSummary = "reviewed";
        db.RunEvents.Add(new RunEvent
        {
            Id = Guid.NewGuid(),
            RunId = preparation.Run.Id,
            EventId = "evt-terminal",
            Type = RunEventTypes.Completed,
            Payload = JsonSerializer.Serialize(new { eventId = "evt-terminal", outcome, result = new { summary = "reviewed" } }),
            OccurredAt = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        return (seeded.Task, preparation.Run);
    }

    [Fact]
    public async Task ReviewChangesRequested_SpendsAFixRound_AndMovesTheCardBackToInProgress()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var seed = await SeedLoopAsync(db, cardStatus: "In Review");
        var (task, _) = await SeedSucceededReviewRunAsync(seed, "changes_requested");

        await NewConsumer(NewConfiguration(maxRounds: 3)).RunCycleAsync(default);

        db.ChangeTracker.Clear();
        Assert.Equal(OrchestrationTaskStatuses.Succeeded, (await ReloadTaskAsync(task.Id)).Status);
        // the round counter increments on the card
        var card = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == seed.Issue.Id);
        Assert.Equal(1, card.ReviewFixRounds);
        // the card moves back to In Progress (the auto_fix trigger) — the project_status action
        var move = await db.OutboxActions.AsNoTracking().SingleAsync(a => a.ActionType == OutboxActionTypes.ProjectStatus);
        using var payload = JsonDocument.Parse(move.Payload);
        Assert.Equal("In Progress", payload.RootElement.GetProperty("status").GetString());
        // no exhaustion comment
        Assert.Equal(0, await db.OutboxActions.CountAsync(a => a.ActionType == OutboxActionTypes.Comment));
    }

    [Fact]
    public async Task ReviewChangesRequested_AtTheCap_ParksTheCard_WithAiFailed_AndTheExhaustedComment()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var seed = await SeedLoopAsync(db, cardStatus: "In Review");
        await db.WorkItems.Where(w => w.Id == seed.Issue.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.ReviewFixRounds, 3)); // the cap (3) already consumed
        db.ChangeTracker.Clear();
        var (task, _) = await SeedSucceededReviewRunAsync(seed, "changes_requested");

        await NewConsumer(NewConfiguration(maxRounds: 3)).RunCycleAsync(default);

        db.ChangeTracker.Clear();
        Assert.Equal(OrchestrationTaskStatuses.Succeeded, (await ReloadTaskAsync(task.Id)).Status);
        // the card does NOT move back: the loop parks
        Assert.Equal(0, await db.OutboxActions.CountAsync(a => a.ActionType == OutboxActionTypes.ProjectStatus));
        // AI Finished was enqueued by the success path, then AI Failed replaces it on delivery
        // (card + PR) — the queue keeps all three actions
        var labels = await db.OutboxActions.AsNoTracking().Where(a => a.ActionType == OutboxActionTypes.AiLabel).ToListAsync();
        Assert.Equal(2, labels.Count(a => a.Payload.Contains("AI Failed")));
        Assert.Single(labels, a => a.Payload.Contains("AI Finished"));
        // the special exhaustion comment on the card
        var comment = await db.OutboxActions.AsNoTracking().SingleAsync(a => a.ActionType == OutboxActionTypes.Comment);
        Assert.Contains(ReviewFlow.ExhaustedCode, comment.Payload);
        // the counter is untouched
        var card = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == seed.Issue.Id);
        Assert.Equal(3, card.ReviewFixRounds);
    }

    [Fact]
    public async Task ReviewPassed_MovesTheCardToTesting()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var seed = await SeedLoopAsync(db, cardStatus: "In Review");
        var (task, _) = await SeedSucceededReviewRunAsync(seed, "completed");

        await NewConsumer(NewConfiguration()).RunCycleAsync(default);

        db.ChangeTracker.Clear();
        Assert.Equal(OrchestrationTaskStatuses.Succeeded, (await ReloadTaskAsync(task.Id)).Status);
        var move = await db.OutboxActions.AsNoTracking().SingleAsync(a => a.ActionType == OutboxActionTypes.ProjectStatus);
        using var payload = JsonDocument.Parse(move.Payload);
        Assert.Equal("Testing", payload.RootElement.GetProperty("status").GetString());
        var card = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == seed.Issue.Id);
        Assert.Equal(0, card.ReviewFixRounds);
    }

    // ---- the counter resets on a manual task-producing command ----

    [Fact]
    public async Task ManualCommandAdmission_ResetsTheRoundCounter()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, body: "@jsxhuz /plan");
        await db.WorkItems.Where(w => w.Id == workItem.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.ReviewFixRounds, 2));
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, repositoryId: repo.Id);
        var command = await SeedQueuedCodeCommandAsync(db, workItem, comment, AgentCommandNames.Plan);
        db.ChangeTracker.Clear();

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, repo,
            botLogin: "jsxhuz", default, configurationState: NewConfiguration());

        Assert.Null(outcome); // admitted
        await db.SaveChangesAsync(); // AdmitAsync writes in the caller's transaction
        db.ChangeTracker.Clear();
        var card = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workItem.Id);
        Assert.Equal(0, card.ReviewFixRounds);
        Assert.Equal(1, await db.OrchestrationTasks.CountAsync());
    }

    private static async Task<AgentCommand> SeedQueuedCodeCommandAsync(
        AppDbContext db, WorkItem workItem, GitHubComment comment, string commandName = AgentCommandNames.Code)
    {
        var command = new AgentCommand
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            GithubCommentId = comment.GithubCommentId,
            AuthorLogin = comment.AuthorLogin,
            CommandName = commandName,
            Arguments = "{}",
            ParserVersion = "1",
            Status = AgentCommandStatuses.Authorized,
            SourceBodySnapshot = comment.Body,
            CreatedAt = DateTime.UtcNow,
            ParsedAt = DateTime.UtcNow,
        };
        db.AgentCommands.Add(command);
        await db.SaveChangesAsync();
        return command;
    }

    private async Task<OrchestrationTask> ReloadTaskAsync(Guid taskId)
    {
        await using var db = postgres.NewDbContext();
        return await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == taskId);
    }
}
