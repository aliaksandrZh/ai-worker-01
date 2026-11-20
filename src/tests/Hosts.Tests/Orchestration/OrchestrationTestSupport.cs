using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Orchestration;

namespace PiAgent.Hosts.Tests.Orchestration;

public sealed record WorkerHarness(
    ServiceProvider Services,
    JobQueueWorker Worker,
    PollerTestSupport.StubHandler Handler,
    JobQueueMetrics Metrics,
    ConfigurationState ConfigState);

public sealed record AdmittedTaskWork(
    AgentCommand? Command,
    OrchestrationTask Task,
    OrchestrationJob Job,
    Workflow Workflow,
    Run? Run);

/// <summary>
/// Seeds for the job-queue tests (task 07): an admitted Task+Job directly (bypassing admission) with
/// optional prepared Run/Session/Container rows, and a JobQueueWorker wired against the Postgres
/// fixture with a stubbed GitHub permission gate.
/// </summary>
public static class OrchestrationTestSupport
{
    private static int _commandNumber;

    /// <summary>Creates (or loads) the Workflow, then Task (queued) + Job (pending) + optionally
    /// Command and prepared Run/Session/Container rows — admission bypassed so tests control the exact
    /// pre-claim state.</summary>
    public static async Task<AdmittedTaskWork> SeedQueuedAsync(
        AppDbContext db,
        WorkItem workItem,
        AgentProfileVersion version,
        string commandName = AgentCommandNames.Plan,
        bool withCommand = true,
        string commandStatus = AgentCommandStatuses.Queued,
        bool withRun = true,
        string? targetHeadSha = null,
        bool linkPlanInput = false,
        DateTime? availableAt = null,
        int priority = 0)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var now = DateTime.UtcNow;
        var workflow = await db.Workflows.SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id);
        if (workflow is null)
        {
            workflow = new Workflow
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItem.Id,
                Status = WorkflowStatuses.WaitingForHuman,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Workflows.Add(workflow);
        }

        var maxSequence = (await db.OrchestrationTasks
            .Where(t => t.WorkflowId == workflow.Id)
            .MaxAsync(t => (int?)t.Sequence)) ?? 0;

        var taskType = commandName switch
        {
            AgentCommandNames.Plan => OrchestrationTaskTypes.Plan,
            AgentCommandNames.Code => OrchestrationTaskTypes.Implement,
            AgentCommandNames.Review => OrchestrationTaskTypes.Review,
            AgentCommandNames.TestCases => OrchestrationTaskTypes.TestDesign,
            _ => OrchestrationTaskTypes.Plan,
        };

        AgentCommand? command = null;
        if (withCommand)
        {
            command = new AgentCommand
            {
                Id = Guid.NewGuid(),
                WorkItemId = workItem.Id,
                GithubCommentId = Interlocked.Increment(ref _commandNumber) + 990_000,
                AuthorLogin = workItem.AuthorLogin,
                CommandName = commandName,
                Arguments = "{}",
                ParserVersion = "1",
                Status = commandStatus,
                SourceBodySnapshot = "@bot /" + commandName,
                CreatedAt = now,
                ParsedAt = now,
            };
            db.AgentCommands.Add(command);
        }

        var task = new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Sequence = maxSequence + 1,
            Type = taskType,
            AgentProfileVersionId = version.Id,
            Status = OrchestrationTaskStatuses.Queued,
            TriggerCommandId = command?.Id,
            TargetHeadSha = targetHeadSha,
            CreatedAt = now,
            RetryPolicy = "{}",
        };
        db.OrchestrationTasks.Add(task);

        if (linkPlanInput && taskType == OrchestrationTaskTypes.Implement)
        {
            var planArtifact = new Artifact
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                WorkflowId = workflow.Id,
                Type = ArtifactTypes.Plan,
                Format = ArtifactFormats.Markdown,
                Content = "seed plan",
                CreatedAt = now,
            };
            db.Artifacts.Add(planArtifact);
            db.TaskArtifactInputs.Add(new TaskArtifactInput
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                ArtifactId = planArtifact.Id,
                Purpose = ArtifactInputPurposes.ApprovedPlan,
                CreatedAt = now,
            });
        }

        var job = new OrchestrationJob
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            JobType = OrchestrationJobTypes.RunTask,
            Status = OrchestrationJobStatuses.Pending,
            Priority = priority,
            Attempts = 0,
            AvailableAt = availableAt ?? now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.OrchestrationJobs.Add(job);

        workflow.Status = WorkflowStatuses.Active;
        workflow.CurrentTaskId = task.Id;
        workflow.StartedAt ??= now;
        workflow.UpdatedAt = now;

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        Run? run = null;
        if (withRun)
        {
            run = new Run
            {
                Id = Guid.NewGuid(),
                TaskId = task.Id,
                AttemptNumber = 1,
                Status = RunStatuses.Pending,
            };
            var session = new AgentSession
            {
                Id = Guid.NewGuid(),
                RunId = run.Id,
                TaskId = task.Id,
                AgentProfileVersionId = version.Id,
                Status = AgentSessionStatuses.Provisioning,
                CreatedAt = now,
            };
            var container = new ContainerInstance
            {
                Id = Guid.NewGuid(),
                AgentSessionId = session.Id,
                RunId = run.Id,
                ContainerImage = version.ContainerImage,
                DesiredState = ContainerDesiredStates.Running,
                ObservedState = ContainerObservedStates.Unknown,
                CreatedAt = now,
            };
            db.Runs.Add(run);
            db.AgentSessions.Add(session);
            db.ContainerInstances.Add(container);
            await db.SaveChangesAsync();
        }
        return new AdmittedTaskWork(command, task, job, workflow, run);
    }

    /// <summary>Repo + WorkItem through the command-test seed (call with `existingRepository` for
    /// additional WorkItems of the same repository — the full name is unique).</summary>
    public static Task<(Repository Repo, WorkItem WorkItem, GitHubComment Comment)> SeedWorkItemAsync(
        AppDbContext db, Repository? existingRepository = null) =>
        CommandTestSupport.SeedAsync(db, existingRepository: existingRepository);

    /// <summary>A worker against the real Postgres fixture with a stubbed GitHub permission gate.</summary>
    public static WorkerHarness NewWorker(
        PostgresFixture fixture,
        int maxConcurrentRuns = 10,
        int leaseSeconds = 300,
        PollerTestSupport.StubHandler? handler = null,
        bool synchronized = true)
    {
        var stub = handler ?? new PollerTestSupport.StubHandler();
        var configuration = new ConfigurationState();
        if (synchronized)
            configuration.MarkSynchronized("jsxhuz", maxConcurrentRuns);
        else
            configuration.MarkFailed("configuration synchronization failed"); // cold-start gate stays closed

        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options
            .UseNpgsql(fixture.ConnectionString)
            .UseSnakeCaseNamingConvention());
        services.AddSingleton(configuration);
        services.AddSingleton(new CollaboratorPermissionGate(
            new HttpClient(stub) { BaseAddress = new Uri("https://api.github.com/") }, "test-pat"));
        services.AddSingleton(new JobQueueOptions { LeaseSeconds = leaseSeconds });
        var metrics = new JobQueueMetrics();
        services.AddSingleton(metrics);
        services.AddLogging(logging => logging.AddSimpleConsole().SetMinimumLevel(LogLevel.Information));

        var provider = services.BuildServiceProvider();
        var worker = new JobQueueWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            configuration,
            provider.GetRequiredService<CollaboratorPermissionGate>(),
            Microsoft.Extensions.Options.Options.Create(new JobQueueOptions { LeaseSeconds = leaseSeconds }),
            new NullRunDispatcher(),
            metrics,
            provider.GetRequiredService<ILogger<JobQueueWorker>>());
        return new WorkerHarness(provider, worker, stub, metrics, configuration);
    }

    public static async Task<AgentProfileVersion> SeedVersionAsync(AppDbContext db, string versionState, bool profileEnabled = true)
    {
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(
            db, OrchestrationTaskTypes.Plan, versionState: versionState, profileEnabled: profileEnabled);
        return version;
    }
}
