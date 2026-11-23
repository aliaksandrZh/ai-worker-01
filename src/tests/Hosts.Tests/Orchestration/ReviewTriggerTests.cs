using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

namespace PiAgent.Hosts.Tests.Orchestration;

/// <summary>
/// System-owned automatic initial review triggers and their
/// claim-time cancellation. Covers the once-per-Workflow guard, draft deferral and the draft→ready
/// transition, the explicit-`/review` suppression, and request withdrawal before execution.
/// </summary>
public class ReviewTriggerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Bot = "jsxhuz";
    private static CancellationToken TestToken => default;

    private static ReviewTriggerCycle NewCycle(PostgresFixture fixture, bool synchronized = true)
    {
        var configuration = new PiAgent.Orchestrator.Config.ConfigurationState();
        if (synchronized)
            configuration.MarkSynchronized(Bot, 10);
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(fixture.ConnectionString)
            .UseSnakeCaseNamingConvention());
        return new ReviewTriggerCycle(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            configuration, NullLogger<ReviewTriggerCycle>.Instance);
    }

    /// <summary>A PR with a ready reviewer profile, optionally draft / requested reviewers / pinned SHAs.</summary>
    private static async Task<WorkItem> SeedPullRequestAsync(
        AppDbContext db, bool draft = false, bool botRequested = true, string head = "head-1")
    {
        var (_, workItem, _) = await CommandTestSupport.SeedAsync(db, pullRequest: true);
        workItem.Draft = draft;
        workItem.HeadSha = head;
        workItem.BaseSha = "base-1";
        workItem.RequestedReviewers = botRequested ? JsonSerializer.Serialize(new[] { Bot }) : "[]";
        await db.SaveChangesAsync();
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Review);
        db.ChangeTracker.Clear();
        return workItem;
    }

    private static async Task<GitHubEvent> AddEventAsync(AppDbContext db, WorkItem workItem, string eventType, string payload)
    {
        var githubEvent = new GitHubEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = workItem.RepositoryId,
            WorkItemId = workItem.Id,
            Source = GitHubEventSources.SnapshotDiff,
            DedupeKey = $"test:{Guid.NewGuid()}",
            EventType = eventType,
            Payload = payload,
            ReceivedAt = DateTime.UtcNow,
            ProcessingStatus = GitHubEventStatuses.Pending,
        };
        db.GitHubEvents.Add(githubEvent);
        await db.SaveChangesAsync();
        return githubEvent;
    }

    private static string ReviewRequestPayload(string reviewer = Bot) =>
        JsonSerializer.Serialize(new { pr_number = 1, reviewer });

    private static string DraftFlipPayload() =>
        JsonSerializer.Serialize(new { number = 1, changed = new Dictionary<string, string> { ["draft"] = "True -> False" }, body_changed = false });

    private static async Task<int> AutomaticReviewCountAsync(AppDbContext db, Guid workflowId) =>
        await db.OrchestrationTasks.CountAsync(t => t.WorkflowId == workflowId && t.TriggerGithubEventId != null);

    [Fact]
    public async Task ReviewRequested_OnReadyPr_CreatesOneInitialReview_OncePerWorkflow()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db);
        var first = await AddEventAsync(db, workItem, GitHubEventTypes.ReviewRequested, ReviewRequestPayload());
        var cycle = NewCycle(postgres);

        await cycle.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == workItem.Id);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskTypes.Review, task.Type);
        Assert.Equal(first.Id, task.TriggerGithubEventId);
        Assert.Null(task.TriggerCommandId);
        Assert.NotNull(task.TriggerAtGithub); // no-new-input gate anchor
        Assert.Equal("head-1", task.TargetHeadSha);
        Assert.Equal("base-1", task.TargetBaseSha);
        Assert.NotNull(workflow.InitialReviewCreatedAt);

        // The first automatic Task finishes (succeeded). A re-request must still not create another one:
        // the only remaining guard is the once-per-Workflow `InitialReviewCreatedAt` (not the unfinished-Task rule).
        await db.OrchestrationTasks.Where(t => t.Id == task.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, OrchestrationTaskStatuses.Succeeded));
        await AddEventAsync(db, workItem, GitHubEventTypes.ReviewRequested, ReviewRequestPayload());
        await cycle.RunCycleAsync(TestToken);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.OrchestrationTasks.CountAsync());
    }

    [Fact]
    public async Task ReviewRequested_OnDraftPr_IsDeferred_NoTask()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db, draft: true);
        await AddEventAsync(db, workItem, GitHubEventTypes.ReviewRequested, ReviewRequestPayload());

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
        Assert.Null((await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id))?.InitialReviewCreatedAt);
    }

    [Fact]
    public async Task DraftToReady_WithActiveRequest_StartsDeferredInitialReview()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db, draft: true);
        await AddEventAsync(db, workItem, GitHubEventTypes.ReviewRequested, ReviewRequestPayload());
        var cycle = NewCycle(postgres);
        await cycle.RunCycleAsync(TestToken); // deferred on draft

        // the PR becomes ready for review
        var current = await db.WorkItems.SingleAsync(w => w.Id == workItem.Id);
        current.Draft = false;
        await db.SaveChangesAsync();
        await AddEventAsync(db, workItem, GitHubEventTypes.WorkItemUpdated, DraftFlipPayload());
        await cycle.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.OrchestrationTasks.CountAsync());
        Assert.NotNull((await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == workItem.Id)).InitialReviewCreatedAt);
    }

    [Fact]
    public async Task NonDraftUpdate_OnReadyPr_DoesNotStartReview()
    {
        // only a draft→ready flip may start the deferred review; an unrelated edit (title/labels) must not
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db, draft: false);
        var titleChanged = JsonSerializer.Serialize(new { number = 1, changed = new Dictionary<string, string> { ["title"] = "a -> b" }, body_changed = false });
        await AddEventAsync(db, workItem, GitHubEventTypes.WorkItemUpdated, titleChanged);

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
    }

    [Fact]
    public async Task DraftToReady_WithoutActiveRequest_StartsNothing()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db, draft: false, botRequested: false);
        await AddEventAsync(db, workItem, GitHubEventTypes.WorkItemUpdated, DraftFlipPayload());

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
    }

    [Fact]
    public async Task DraftToReady_AfterExplicitReview_IsSuppressed()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db, draft: true);
        var version = await OrchestrationTestSupport.SeedVersionAsync(db, ProfileVersionStates.Ready);
        // an explicit /review already ran for this PR and finished (so the unfinished-Task rule cannot be
        // the reason nothing is created — only the explicit-review suppression can block the automatic one)
        var explicitReview = await OrchestrationTestSupport.SeedQueuedAsync(
            db, workItem, version, commandName: AgentCommandNames.Review, withRun: false);
        await db.OrchestrationTasks.Where(t => t.Id == explicitReview.Task.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, OrchestrationTaskStatuses.Succeeded));
        db.ChangeTracker.Clear();
        var explicitCount = await db.OrchestrationTasks.CountAsync();

        var current = await db.WorkItems.SingleAsync(w => w.Id == workItem.Id);
        current.Draft = false;
        await db.SaveChangesAsync();
        await AddEventAsync(db, workItem, GitHubEventTypes.WorkItemUpdated, DraftFlipPayload());
        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(explicitCount, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(0, await AutomaticReviewCountAsync(db, (await db.Workflows.SingleAsync(w => w.WorkItemId == workItem.Id)).Id));
    }

    [Fact]
    public async Task ReviewRequestRemoved_BeforeStart_CancelsQueuedInitialReview_AtClaim()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db);
        await AddEventAsync(db, workItem, GitHubEventTypes.ReviewRequested, ReviewRequestPayload());
        await NewCycle(postgres).RunCycleAsync(TestToken);
        db.ChangeTracker.Clear();
        Assert.Equal(1, await db.OrchestrationTasks.CountAsync());

        // the request is withdrawn while the Task is still queued
        var current = await db.WorkItems.SingleAsync(w => w.Id == workItem.Id);
        current.RequestedReviewers = "[]";
        await db.SaveChangesAsync();
        await AddEventAsync(db, workItem, GitHubEventTypes.ReviewRequestRemoved, ReviewRequestPayload());
        await NewCycle(postgres).RunCycleAsync(TestToken);

        // the claim path cancels the not-yet-started review with the spec reason
        var harness = OrchestrationTestSupport.NewWorker(postgres);
        await harness.Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, task.Status);
        Assert.Equal(AgentCommandRejectionReasons.ReviewRequestRemovedBeforeExecution, task.FailureReason);
        var notice = await db.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Contains("review request", notice.Payload);
    }

    [Fact]
    public async Task AutomaticReview_HeadDriftBeforeStart_CancelsWithHeadChanged_AndNotifies()
    {
        // SHA drift before a queued automatic review starts: cancel + system comment asking for /review
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db, head: "head-1");
        await AddEventAsync(db, workItem, GitHubEventTypes.ReviewRequested, ReviewRequestPayload());
        await NewCycle(postgres).RunCycleAsync(TestToken);
        db.ChangeTracker.Clear();

        var current = await db.WorkItems.SingleAsync(w => w.Id == workItem.Id);
        current.HeadSha = "head-2"; // a new commit landed before the queued review started
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await OrchestrationTestSupport.NewWorker(postgres).Worker.RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, task.Status);
        Assert.Equal(AgentCommandRejectionReasons.ReviewHeadChanged, task.FailureReason);
        Assert.Equal("head-1", task.TargetHeadSha); // the pinned SHA is never rewritten
        var notice = await db.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Contains("run /review again", notice.Payload);
    }

    [Fact]
    public async Task ReviewRequested_ForAnotherReviewer_IsIgnored()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db, botRequested: false);
        await AddEventAsync(db, workItem, GitHubEventTypes.ReviewRequested, ReviewRequestPayload("someone-else"));

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
    }

    [Fact]
    public async Task NewCommitAlone_DoesNotTriggerReview()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db, botRequested: false);
        await AddEventAsync(db, workItem, GitHubEventTypes.PullRequestSynchronized, "{}");

        await NewCycle(postgres).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
    }

    [Fact]
    public async Task Cycle_WaitsForConfigurationSync()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var workItem = await SeedPullRequestAsync(db);
        var githubEvent = await AddEventAsync(db, workItem, GitHubEventTypes.ReviewRequested, ReviewRequestPayload());

        await NewCycle(postgres, synchronized: false).RunCycleAsync(TestToken);

        db.ChangeTracker.Clear();
        Assert.Equal(0, await db.OrchestrationTasks.CountAsync());
        Assert.Equal(GitHubEventStatuses.Pending, (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == githubEvent.Id)).ProcessingStatus);
    }
}
