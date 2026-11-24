using PiAgent.RuntimeApi.Provisioning;
using PiAgent.RuntimeApi.Runs;
using PiAgent.RuntimeApi.Api;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.Platform.Workers;
using Xunit;

namespace PiAgent.Hosts.Tests.Runtime;

/// <summary>
/// The Orchestrator root-closure cycle over real closure event rows — Workflow closed,
/// unfinished Tasks/Jobs cancelled with `work_item_closed`, active Runs cancelled, Runtime API
/// cleanup invoked, reopen idempotent, related roots untouched.
/// </summary>
public class RootClosureCycleTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static RootClosureCycle NewCycle(PostgresFixture fixture, DirectHttpStub runtimeStub)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(fixture.ConnectionString)
            .UseSnakeCaseNamingConvention());
        var provider = services.BuildServiceProvider();
        var client = new HttpClient(runtimeStub) { BaseAddress = new Uri("http://runtime-api.test") };
        return new RootClosureCycle(
            new FixedScopeFactory(provider),
            new RuntimeApiClient(client,
                Microsoft.Extensions.Options.Options.Create(new RuntimeApiOptions { ServiceToken = "tok-orch" })));
    }

    /// <summary>RootClosureCycle creates scopes from IServiceScopeFactory; the tests hand a fixed provider.</summary>
    private sealed class FixedScopeFactory(IServiceProvider provider) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => provider.CreateScope();
        public IAsyncDisposable CreateAsyncScope() => new ScopeAdapter(CreateScope());

        private sealed class ScopeAdapter(IServiceScope scope) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                scope.Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private static HttpResponseMessage RuntimeOk() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("""{"ok":true}""", System.Text.Encoding.UTF8, "application/json"),
    };

    private static GitHubEvent ClosedEvent(WorkItem workItem, string suffix = "0") => new()
    {
        Id = Guid.NewGuid(),
        RepositoryId = workItem.RepositoryId,
        WorkItemId = workItem.Id,
        Source = GitHubEventSources.RestResourceSync,
        DedupeKey = $"wi:{workItem.GithubNodeId}:closed:{suffix}",
        EventType = GitHubEventTypes.WorkItemClosed,
        Payload = "{\"number\":" + workItem.Number + ",\"close_reason\":\"completed\"}",
        ReceivedAt = DateTime.UtcNow,
        ProcessingStatus = GitHubEventStatuses.Pending,
    };

    [Fact]
    public async Task ClosedEvent_ClosesWorkflow_CancelsTasks_CancelsRun_CleansStorage()
    {
        await postgres.ResetAsync();
        var runtimeStub = new DirectHttpStub(_ => RuntimeOk());
        await using var db = postgres.NewDbContext();
        var (repository, workItem, _) = await CommandTestSupport.SeedAsync(db);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var workflowId = Guid.NewGuid();
        var task = new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = workflowId, Sequence = 1, Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id, Status = OrchestrationTaskStatuses.Queued, CreatedAt = DateTime.UtcNow,
            TriggerCommandId = CommandTestSupport.SeedTriggerCommand(db, workItem).Id,
            RetryPolicy = """{"max_automatic_retries":1,"initial_backoff_seconds":30}""",
        };
        db.Workflows.Add(new Workflow
        {
            Id = workflowId, WorkItemId = workItem.Id, Status = WorkflowStatuses.Active,
            CurrentTaskId = task.Id, CreatedAt = DateTime.UtcNow, StartedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.OrchestrationTasks.Add(task);
        db.OrchestrationJobs.Add(new OrchestrationJob
        {
            Id = Guid.NewGuid(), TaskId = task.Id, JobType = OrchestrationJobTypes.RunTask,
            Status = OrchestrationJobStatuses.Pending, Priority = 0, Attempts = 0,
            AvailableAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.Runs.Add(new Run
        {
            Id = Guid.NewGuid(), TaskId = task.Id, AttemptNumber = 1, Status = RunStatuses.Running,
        });
        db.GitHubEvents.Add(ClosedEvent(workItem));
        await db.SaveChangesAsync();

        await NewCycle(postgres, runtimeStub).RunCycleAsync(CancellationToken.None);

        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == workflowId);
        var cancelledTask = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.TaskId == task.Id);
        var run = await db.Runs.AsNoTracking().SingleAsync();
        var closedEvent = await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.EventType == GitHubEventTypes.WorkItemClosed);
        Assert.Equal(GitHubEventStatuses.Processed, closedEvent.ProcessingStatus);
        var updatedWorkItem = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workItem.Id);
        Assert.Equal(WorkflowStatuses.Closed, workflow.Status);
        Assert.Equal(OrchestrationTaskStatuses.Cancelled, cancelledTask.Status);
        Assert.Equal(AgentCommandRejectionReasons.WorkItemClosed, cancelledTask.FailureReason);
        Assert.Equal(OrchestrationJobStatuses.Cancelled, job.Status);
        Assert.Equal(RunStatuses.Cancelled, run.Status);
        Assert.Equal(GitHubEventStatuses.Processed, closedEvent.ProcessingStatus);
        Assert.Equal("completed", updatedWorkItem.CleanupStatus);
        // cleanup + one stop per active run, service-token guarded
        Assert.Contains(runtimeStub.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/cleanup"));
        // cleanup is silent — no GitHub notification is enqueued
        Assert.Equal(0, await db.OutboxActions.CountAsync(a => a.ActionType == OutboxActionTypes.Comment));
    }

    [Fact]
    public async Task MergedPrClosure_CleansLikeClosure_KeepsMergedAt()
    {
        await postgres.ResetAsync();
        var runtimeStub = new DirectHttpStub(_ => RuntimeOk());
        await using var db = postgres.NewDbContext();
        var (repository, pr, _) = await CommandTestSupport.SeedAsync(db, pullRequest: true);
        var mergedAt = DateTime.UtcNow.AddDays(-1);
        pr.State = "closed";
        pr.MergedAt = mergedAt;
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = pr.Id, Status = WorkflowStatuses.Active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.GitHubEvents.Add(new GitHubEvent
        {
            Id = Guid.NewGuid(), RepositoryId = pr.RepositoryId, WorkItemId = pr.Id,
            Source = GitHubEventSources.RestResourceSync, DedupeKey = $"wi:{pr.GithubNodeId}:closed:0",
            EventType = GitHubEventTypes.WorkItemClosed,
            Payload = "{\"close_reason\":\"merged\"}",
            ReceivedAt = DateTime.UtcNow, ProcessingStatus = GitHubEventStatuses.Pending,
        });
        await db.SaveChangesAsync();

        await NewCycle(postgres, runtimeStub).RunCycleAsync(CancellationToken.None);

        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == pr.Id);
        var prRow = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == pr.Id);
        Assert.Equal(WorkflowStatuses.Closed, workflow.Status);
        Assert.Equal("completed", prRow.CleanupStatus);
        Assert.NotNull(prRow.MergedAt); // merged_at is history — never deleted
        Assert.Equal(DateTimeKind.Utc, prRow.MergedAt.Value.Kind == DateTimeKind.Utc ? DateTimeKind.Utc : DateTimeKind.Unspecified);
    }

    [Fact]
    public async Task Reopen_SecondClosedEvent_IsIdempotent_NoSecondWorkflow()
    {
        await postgres.ResetAsync();
        var runtimeStub = new DirectHttpStub(_ => RuntimeOk());
        await using var db = postgres.NewDbContext();
        var (repository, workItem, _) = await CommandTestSupport.SeedAsync(db);
        db.Workflows.Add(new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, Status = WorkflowStatuses.Active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.GitHubEvents.Add(ClosedEvent(workItem));
        await db.SaveChangesAsync();
        await NewCycle(postgres, runtimeStub).RunCycleAsync(CancellationToken.None);

        // reopen + close again: a second closed event for the same root
        db.GitHubEvents.Add(ClosedEvent(workItem, suffix: "1"));
        await db.SaveChangesAsync();
        await NewCycle(postgres, runtimeStub).RunCycleAsync(CancellationToken.None);

        Assert.Equal(1, await db.Workflows.CountAsync(w => w.WorkItemId == workItem.Id));
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == workItem.Id);
        Assert.Equal(WorkflowStatuses.Closed, workflow.Status);
        Assert.Equal(2, await db.GitHubEvents.CountAsync(e => e.EventType == GitHubEventTypes.WorkItemClosed
            && e.ProcessingStatus == GitHubEventStatuses.Processed));
    }

    [Fact]
    public async Task ClosingRelatedIssue_DoesNotTouchPrWorkflow()
    {
        await postgres.ResetAsync();
        var runtimeStub = new DirectHttpStub(_ => RuntimeOk());
        await using var db = postgres.NewDbContext();
        var (repository, issue, _) = await CommandTestSupport.SeedAsync(db);
        var (_, pr, _) = await CommandTestSupport.SeedAsync(db, pullRequest: true, existingRepository: repository);
        var prWorkflowId = Guid.NewGuid();
        db.Workflows.Add(new Workflow
        {
            Id = prWorkflowId, WorkItemId = pr.Id, Status = WorkflowStatuses.Active,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.GitHubEvents.Add(ClosedEvent(issue));
        await db.SaveChangesAsync();

        await NewCycle(postgres, runtimeStub).RunCycleAsync(CancellationToken.None);

        var prWorkflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == prWorkflowId);
        Assert.Equal(WorkflowStatuses.Active, prWorkflow.Status);
        var prRow = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == pr.Id);
        Assert.Null(prRow.CleanupStatus);
    }
}
