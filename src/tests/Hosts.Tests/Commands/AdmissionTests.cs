using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Orchestration;
using Xunit;

namespace PiAgent.Hosts.Tests.Commands;

/// <summary>
/// The admission stage (task 07, 04 §2/06 §12/decisions 35, 71, 79, 80): decision table over
/// `CommandAdmission.AdmitAsync` against real postgres (the FOR UPDATE lock + partial unique index
/// included via SaveChanges), and two end-to-end cases through the task-06 processor harness.
/// </summary>
public class AdmissionTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    // ---- helpers ----

    private async Task<(AppDbContext db, Repository Repo, WorkItem Wi, AgentCommand Command)> NewAuthorizedAsync(
        string commandName,
        bool pullRequest = false,
        bool open = true,
        Action<AgentCommand>? tweak = null)
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, pullRequest: pullRequest);
        if (!open)
        {
            workItem.State = "closed";
            await db.SaveChangesAsync();
        }
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
        tweak?.Invoke(command);
        db.AgentCommands.Add(command);
        await db.SaveChangesAsync();
        // nothing is detached: AdmitAsync (like the real processor path) receives tracked entities
        return (db, repo, workItem, command);
    }

    /// <summary>A completed trigger row for a hand-seeded Task: the `ck_task_has_trigger` constraint
    /// requires every task row to reference a command or a producing event, exactly as production rows do.</summary>
    private static AgentCommand NewSeedTrigger(AppDbContext db, WorkItem workItem)
    {
        var trigger = new AgentCommand
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = NextSeedCommentId(),
            AuthorLogin = "alice", CommandName = AgentCommandNames.Plan, Arguments = "{}",
            ParserVersion = "1", Status = AgentCommandStatuses.Processed,
            SourceBodySnapshot = "@jsxhuz /plan", CreatedAt = DateTime.UtcNow, ParsedAt = DateTime.UtcNow,
            ProcessedAt = DateTime.UtcNow,
        };
        db.AgentCommands.Add(trigger);
        return trigger;
    }

    private static long _seedCommentId = 98000;
    private static long NextSeedCommentId() => ++_seedCommentId;

    private static async Task<(Workflow? Workflow, OrchestrationTask? Task, OrchestrationJob? Job)> LoadAdmittedAsync(
        AppDbContext db, WorkItem workItem, AgentCommand command)
    {
        var workflow = await db.Workflows.AsNoTracking().SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleOrDefaultAsync(t => t.TriggerCommandId == command.Id);
        var job = task is null ? null : await db.OrchestrationJobs.AsNoTracking().SingleOrDefaultAsync(j => j.TaskId == task.Id);
        return (workflow, task, job);
    }

    // ---- accepted path ----

    [Fact]
    public async Task PlanCommand_CreatesWorkflowActiveTaskAndJob()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Plan);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, retryJson: """{"max_automatic_retries": 3, "initial_backoff": "00:01:05"}""");

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);
        await db.SaveChangesAsync();

        Assert.Null(outcome);
        var (workflow, task, job) = await LoadAdmittedAsync(db, workItem, command);
        Assert.NotNull(workflow);
        Assert.Equal(WorkflowStatuses.Active, workflow!.Status);
        Assert.Equal(task!.Id, workflow.CurrentTaskId);
        Assert.NotNull(workflow.StartedAt);
        Assert.Equal(1, task.Sequence);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
        Assert.Equal(OrchestrationTaskTypes.Plan, task.Type);
        Assert.Equal(command.Id, task.TriggerCommandId);
        Assert.NotNull(task.TriggerAtGithub); // no-new-input gate anchor (owner decision 2026-11-23)
        Assert.Null(task.TargetHeadSha);
        Assert.Contains("max_automatic_retries", task.RetryPolicy);
        Assert.NotNull(job);
        Assert.Equal(OrchestrationJobTypes.RunTask, job!.JobType);
        Assert.Equal(OrchestrationJobStatuses.Pending, job.Status);
        Assert.Equal(0, job.Priority);
        var refreshed = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == command.Id);
        Assert.Equal(AgentCommandStatuses.Queued, refreshed.Status);
    }

    [Fact]
    public async Task SecondCommandSameWorkflow_RejectedNotQueued()
    {
        var (db, _, workItem, first) = await NewAuthorizedAsync(AgentCommandNames.Plan);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var admitter = db.Repositories.AsNoTracking().Single();
        Assert.Null(await CommandAdmission.AdmitAsync(db, first, workItem, admitter, botLogin: null, default));
        await db.SaveChangesAsync();

        var second = new AgentCommand
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = 99001,
            AuthorLogin = "alice", CommandName = AgentCommandNames.Plan, Arguments = "{}",
            ParserVersion = "1", Status = AgentCommandStatuses.Authorized,
            SourceBodySnapshot = "@jsxhuz /plan", CreatedAt = DateTime.UtcNow, ParsedAt = DateTime.UtcNow,
        };
        db.AgentCommands.Add(second);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var outcome = await CommandAdmission.AdmitAsync(db, second, workItem, admitter, botLogin: null, default);
        await db.SaveChangesAsync();

        Assert.NotNull(outcome);
        Assert.Equal(AgentCommandRejectionReasons.UnfinishedTaskExists, outcome!.Reason);
        Assert.Equal(1, await db.OrchestrationTasks.AsNoTracking().CountAsync());
        Assert.Equal(1, await db.OrchestrationJobs.AsNoTracking().CountAsync());
        Assert.Equal(AgentCommandStatuses.Authorized,
            (await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == second.Id)).Status);
    }

    [Fact]
    public async Task SecondSequenceIncrements()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Plan);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var admitter = db.Repositories.AsNoTracking().Single();
        // an older succeeded Task at sequence 5
        var workflowSeed = new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, Status = WorkflowStatuses.WaitingForHuman,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var oldTask = new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = workflowSeed.Id, Sequence = 5, Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = (await db.AgentProfileVersions.AsNoTracking().SingleAsync()).Id,
            Status = OrchestrationTaskStatuses.Succeeded, CreatedAt = DateTime.UtcNow, RetryPolicy = "{}",
            TriggerCommandId = NewSeedTrigger(db, workItem).Id,
        };
        db.Workflows.Add(workflowSeed);
        db.OrchestrationTasks.Add(oldTask);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, admitter, botLogin: null, default);
        await db.SaveChangesAsync();

        Assert.Null(outcome);
        Assert.Equal(6, (await LoadAdmittedAsync(db, workItem, command)).Task!.Sequence);
    }

    // ---- /code preconditions (decision 80) ----

    [Fact]
    public async Task CodeWithoutPlan_RejectedNoWorkflowCreated()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Code);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Implement);

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);
        await db.SaveChangesAsync();

        Assert.NotNull(outcome);
        Assert.Equal(AgentCommandRejectionReasons.PlanNotAvailable, outcome!.Reason);
        Assert.False(await db.Workflows.AsNoTracking().AnyAsync());
        Assert.Empty(await db.OrchestrationTasks.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OrchestrationJobs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CodeWithPlanArtifact_LinksApprovedPlanInput()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Code);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Implement
        );
        var plannedWorkflow = new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, Status = WorkflowStatuses.WaitingForHuman,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var planTask = new OrchestrationTask
        {
            Id = Guid.NewGuid(), WorkflowId = plannedWorkflow.Id, Sequence = 1, Type = OrchestrationTaskTypes.Plan,
            AgentProfileVersionId = version.Id, Status = OrchestrationTaskStatuses.Succeeded, CreatedAt = DateTime.UtcNow, RetryPolicy = "{}",
            TriggerCommandId = NewSeedTrigger(db, workItem).Id,
        };
        var planArtifact = new Artifact
        {
            Id = Guid.NewGuid(), TaskId = planTask.Id, WorkflowId = plannedWorkflow.Id,
            Type = ArtifactTypes.Plan, Format = ArtifactFormats.Markdown, Content = "the plan",
            CreatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(plannedWorkflow);
        db.OrchestrationTasks.Add(planTask);
        db.Artifacts.Add(planArtifact);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);
        await db.SaveChangesAsync();

        Assert.Null(outcome);
        var (_, task, _) = await LoadAdmittedAsync(db, workItem, command);
        var input = await db.TaskArtifactInputs.AsNoTracking().SingleAsync(i => i.TaskId == task!.Id);
        Assert.Equal(planArtifact.Id, input.ArtifactId);
        Assert.Equal(ArtifactInputPurposes.ApprovedPlan, input.Purpose);
    }

    [Fact]
    public async Task CodeApprovesLatestPlanArtifact()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Code);
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Implement);
        var plannedWorkflow = new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, Status = WorkflowStatuses.WaitingForHuman,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(plannedWorkflow);
        var olderArtifactId = Guid.NewGuid();
        foreach (var (sequence, artifactId) in new[] { (1, olderArtifactId), (2, Guid.NewGuid()) })
        {
            var planTask = new OrchestrationTask
            {
                Id = Guid.NewGuid(), WorkflowId = plannedWorkflow.Id, Sequence = sequence, Type = OrchestrationTaskTypes.Plan,
                AgentProfileVersionId = version.Id, Status = OrchestrationTaskStatuses.Succeeded, CreatedAt = DateTime.UtcNow, RetryPolicy = "{}",
                TriggerCommandId = NewSeedTrigger(db, workItem).Id,
            };
            db.OrchestrationTasks.Add(planTask);
            db.Artifacts.Add(new Artifact
            {
                Id = artifactId, TaskId = planTask.Id, WorkflowId = plannedWorkflow.Id,
                Type = ArtifactTypes.Plan, Format = ArtifactFormats.Markdown, Content = $"plan {sequence}",
                CreatedAt = DateTime.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);
        await db.SaveChangesAsync();

        Assert.Null(outcome);
        var (_, task, _) = await LoadAdmittedAsync(db, workItem, command);
        var input = await db.TaskArtifactInputs.AsNoTracking().SingleAsync(i => i.TaskId == task!.Id);
        Assert.NotEqual(olderArtifactId, input.ArtifactId); // the sequence-2 plan artifact is linked
    }

    // ---- profile gates (decision 35 split) ----

    [Fact]
    public async Task MissingRoutingRule_RejectedAgentProfileNotConfigured()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Code);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan); // only a plan rule exists

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);

        Assert.NotNull(outcome);
        Assert.Equal(AgentCommandRejectionReasons.AgentProfileNotConfigured, outcome!.Reason);
        Assert.False(await db.Workflows.AsNoTracking().AnyAsync());
    }

    [Fact]
    public async Task RepoScopedRule_WinsOverGlobal()
    {
        var (db, repo, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Plan);
        var (_, globalVersion, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, repositoryId: null);
        var (_, repoVersion, _) = await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, repositoryId: repo.Id);

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);
        await db.SaveChangesAsync();
        Assert.Null(outcome);
        var (_, task, _) = await LoadAdmittedAsync(db, workItem, command);
        Assert.Equal(repoVersion.Id, task!.AgentProfileVersionId);
    }

    [Fact]
    public async Task DisabledProfile_RejectedAgentProfileNotConfigured()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Plan);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, profileEnabled: false);

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);

        Assert.NotNull(outcome);
        Assert.Equal(AgentCommandRejectionReasons.AgentProfileNotConfigured, outcome!.Reason);
    }

    [Theory]
    [InlineData(ProfileVersionStates.Draft)]
    [InlineData(ProfileVersionStates.Building)]
    [InlineData(ProfileVersionStates.BuildFailed)]
    public async Task VersionNotReady_RejectedAgentImageNotReady(string versionState)
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Plan);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, versionState: versionState);

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);

        Assert.NotNull(outcome);
        Assert.Equal(AgentCommandRejectionReasons.AgentImageNotReady, outcome!.Reason);
    }

    [Fact]
    public async Task VersionDisabled_RejectedAgentProfileNotConfigured()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Plan);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan, versionState: ProfileVersionStates.Disabled);

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);

        Assert.NotNull(outcome);
        Assert.Equal(AgentCommandRejectionReasons.AgentProfileNotConfigured, outcome!.Reason);
    }

    // ---- workflow lifecycle gates (decision 71 + root closure) ----

    [Fact]
    public async Task FailedWorkflow_RejectsTaskProducingCommands()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Plan);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var workflowSeed = new Workflow
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, Status = WorkflowStatuses.Failed,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.Workflows.Add(workflowSeed);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var admitter = db.Repositories.AsNoTracking().Single();

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, admitter, botLogin: null, default);
        Assert.NotNull(outcome);
        Assert.Equal(AgentCommandRejectionReasons.WorkflowFailed, outcome!.Reason);
        Assert.False(await db.Workflows.AsNoTracking().AnyAsync(w => w.Status != WorkflowStatuses.Failed));

        var retryCommand = new AgentCommand
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = 99002,
            AuthorLogin = "alice", CommandName = AgentCommandNames.Retry, Arguments = "{}",
            ParserVersion = "1", Status = AgentCommandStatuses.Authorized,
            SourceBodySnapshot = "@jsxhuz /retry", CreatedAt = DateTime.UtcNow, ParsedAt = DateTime.UtcNow,
        };
        db.AgentCommands.Add(retryCommand);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Equal(AgentCommandRejectionReasons.WorkflowFailed,
            (await CommandAdmission.AdmitAsync(db, retryCommand, workItem, admitter, botLogin: null, default))?.Reason);
    }

    [Fact]
    public async Task ControlAndRetryCommands_StayAuthorizedUntouched()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Status);

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);
        await db.SaveChangesAsync();

        Assert.Null(outcome);
        Assert.False(await db.Workflows.AsNoTracking().AnyAsync());
        Assert.Equal(AgentCommandStatuses.Authorized,
            (await db.AgentCommands.AsNoTracking().SingleAsync(c => c.Id == command.Id)).Status);
    }

    [Fact]
    public async Task ClosedWorkItem_RejectedWorkItemClosed()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Plan, open: false);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);

        Assert.NotNull(outcome);
        Assert.Equal(AgentCommandRejectionReasons.WorkItemClosed, outcome!.Reason);
    }

    // ---- no-new-input gate (owner decision 2026-11-23) ----

    /// <summary>Admits a /plan and completes it; the trigger comment sat at UtcNow-5min (CommandTestSupport),
    /// so every "signal" below is timestamped after the gate's anchor.</summary>
    private async Task<(AppDbContext Db, Repository Repo, WorkItem Wi, AgentCommand Repeat)> CompletedPlanWithRepeatAsync()
    {
        var (db, repo, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Plan);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var admitter = db.Repositories.AsNoTracking().Single();
        Assert.Null(await CommandAdmission.AdmitAsync(db, command, workItem, admitter, "jsxhuz", default));
        await db.SaveChangesAsync();
        var task = await db.OrchestrationTasks.SingleAsync(); // tracked: AdmitAsync added it to this context
        task.Status = OrchestrationTaskStatuses.Succeeded;
        await db.SaveChangesAsync();

        var repeat = new AgentCommand
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = 99010,
            AuthorLogin = "alice", CommandName = AgentCommandNames.Plan, Arguments = "{}",
            ParserVersion = "1", Status = AgentCommandStatuses.Authorized,
            SourceBodySnapshot = "@jsxhuz /plan", CreatedAt = DateTime.UtcNow, ParsedAt = DateTime.UtcNow,
        };
        db.AgentCommands.Add(repeat);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (db, repo, workItem, repeat);
    }

    [Fact]
    public async Task RepeatAfterSuccessWithoutSignal_RejectedNoNewInput()
    {
        var (db, _, workItem, repeat) = await CompletedPlanWithRepeatAsync();
        var admitter = db.Repositories.AsNoTracking().Single();

        var outcome = await CommandAdmission.AdmitAsync(db, repeat, workItem, admitter, "jsxhuz", default);

        Assert.NotNull(outcome);
        Assert.Equal(AgentCommandRejectionReasons.NoNewInput, outcome!.Reason);
        Assert.Equal(1, await db.OrchestrationTasks.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task BotReplyAfterSuccess_IsNotANewSignal()
    {
        var (db, _, workItem, repeat) = await CompletedPlanWithRepeatAsync();
        var admitter = db.Repositories.AsNoTracking().Single();
        db.GitHubComments.Add(new GitHubComment
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = 99011,
            Kind = GitHubCommentKinds.IssueComment, AuthorLogin = "jsxhuz", Body = "plan posted",
            CreatedAtGithub = DateTime.UtcNow.AddMinutes(-3), UpdatedAtGithub = DateTime.UtcNow.AddMinutes(-3),
            LastSyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var outcome = await CommandAdmission.AdmitAsync(db, repeat, workItem, admitter, "jsxhuz", default);

        Assert.Equal(AgentCommandRejectionReasons.NoNewInput, outcome!.Reason);
    }

    [Fact]
    public async Task HumanCommentAfterSuccess_IsANewSignal()
    {
        var (db, _, workItem, repeat) = await CompletedPlanWithRepeatAsync();
        var admitter = db.Repositories.AsNoTracking().Single();
        db.GitHubComments.Add(new GitHubComment
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = 99012,
            Kind = GitHubCommentKinds.IssueComment, AuthorLogin = "bob", Body = "please also cover X",
            CreatedAtGithub = DateTime.UtcNow.AddMinutes(-3), UpdatedAtGithub = DateTime.UtcNow.AddMinutes(-3),
            LastSyncedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var outcome = await CommandAdmission.AdmitAsync(db, repeat, workItem, admitter, "jsxhuz", default);
        await db.SaveChangesAsync();

        Assert.Null(outcome);
        Assert.Equal(2, await db.OrchestrationTasks.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task HumanBodyEditAfterSuccess_IsANewSignal()
    {
        var (db, _, workItem, repeat) = await CompletedPlanWithRepeatAsync();
        var admitter = db.Repositories.AsNoTracking().Single();
        db.GitHubEvents.Add(new GitHubEvent
        {
            Id = Guid.NewGuid(), RepositoryId = workItem.RepositoryId, WorkItemId = workItem.Id,
            Source = GitHubEventSources.SnapshotDiff, DedupeKey = $"wi:node-x:upd:{DateTime.UtcNow.Ticks}:abc",
            EventType = GitHubEventTypes.WorkItemUpdated,
            Payload = """{"number": 1, "changed": {}, "body_changed": true}""",
            OccurredAtGithub = DateTime.UtcNow.AddMinutes(-2), ReceivedAt = DateTime.UtcNow.AddMinutes(-2),
            ProcessingStatus = GitHubEventStatuses.Pending,
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var outcome = await CommandAdmission.AdmitAsync(db, repeat, workItem, admitter, "jsxhuz", default);
        await db.SaveChangesAsync();

        Assert.Null(outcome);
        Assert.Equal(2, await db.OrchestrationTasks.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task HumanPushAfterReviewSuccess_IsANewSignal()
    {
        var (db, workItem, repeat) = await CompletedReviewWithRepeatAsync(outsideRunWindows: true);
        var admitter = db.Repositories.AsNoTracking().Single();

        var outcome = await CommandAdmission.AdmitAsync(db, repeat, workItem, admitter, "jsxhuz", default);
        await db.SaveChangesAsync();

        Assert.Null(outcome);
        Assert.Equal(2, await db.OrchestrationTasks.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task AgentPushDuringItsRun_IsNotANewSignal()
    {
        var (db, workItem, repeat) = await CompletedReviewWithRepeatAsync(outsideRunWindows: false);
        var admitter = db.Repositories.AsNoTracking().Single();

        var outcome = await CommandAdmission.AdmitAsync(db, repeat, workItem, admitter, "jsxhuz", default);

        Assert.Equal(AgentCommandRejectionReasons.NoNewInput, outcome!.Reason);
    }

    /// <summary>Admits a /review on a PR and completes it; adds one head-drift event at UtcNow-2min and,
    /// when `outsideRunWindows` is false, a finished Run window swallowing that instant (the agent's
    /// own push). No runs exist in the true case, so the same instant counts as a human push.</summary>
    private async Task<(AppDbContext Db, WorkItem Wi, AgentCommand Repeat)> CompletedReviewWithRepeatAsync(bool outsideRunWindows)
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, pullRequest: true);
        workItem.HeadSha = "head-abc";
        workItem.BaseSha = "base-def";
        await db.SaveChangesAsync();
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Review);
        var admitter = db.Repositories.AsNoTracking().Single();
        var command = new AgentCommand
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = comment.GithubCommentId,
            AuthorLogin = "alice", CommandName = AgentCommandNames.Review, Arguments = "{}",
            ParserVersion = "1", Status = AgentCommandStatuses.Authorized,
            SourceBodySnapshot = comment.Body, CreatedAt = DateTime.UtcNow, ParsedAt = DateTime.UtcNow,
        };
        db.AgentCommands.Add(command);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Null(await CommandAdmission.AdmitAsync(db, command, workItem, admitter, "jsxhuz", default));
        await db.SaveChangesAsync();
        var reviewTask = await db.OrchestrationTasks.SingleAsync(); // tracked: AdmitAsync added it to this context
        reviewTask.Status = OrchestrationTaskStatuses.Succeeded;
        if (!outsideRunWindows)
        {
            db.Runs.Add(new Run
            {
                Id = Guid.NewGuid(), TaskId = reviewTask.Id, AttemptNumber = 1,
                Status = RunStatuses.Succeeded,
                StartedAt = DateTime.UtcNow.AddMinutes(-10), CompletedAt = DateTime.UtcNow.AddMinutes(-1),
            });
        }
        await db.SaveChangesAsync();

        db.GitHubEvents.Add(new GitHubEvent
        {
            Id = Guid.NewGuid(), RepositoryId = workItem.RepositoryId, WorkItemId = workItem.Id,
            Source = GitHubEventSources.SnapshotDiff, DedupeKey = "prs:node-x:head-2",
            EventType = GitHubEventTypes.PullRequestSynchronized,
            Payload = """{"number": 1, "from_sha": "head-abc", "to_sha": "head-2"}""",
            OccurredAtGithub = DateTime.UtcNow.AddMinutes(-2), ReceivedAt = DateTime.UtcNow.AddMinutes(-2),
            ProcessingStatus = GitHubEventStatuses.Pending,
        });
        var repeat = new AgentCommand
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = 99013,
            AuthorLogin = "alice", CommandName = AgentCommandNames.Review, Arguments = "{}",
            ParserVersion = "1", Status = AgentCommandStatuses.Authorized,
            SourceBodySnapshot = "@jsxhuz /review", CreatedAt = DateTime.UtcNow, ParsedAt = DateTime.UtcNow,
        };
        db.AgentCommands.Add(repeat);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return (db, workItem, repeat);
    }

    // ---- review SHA pinning ----

    [Fact]
    public async Task ReviewCommand_PinsHeadAndBaseSha()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, pullRequest: true);
        workItem.HeadSha = "head-abc";
        workItem.BaseSha = "base-def";
        await db.SaveChangesAsync();
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Review);
        var command = new AgentCommand
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = comment.GithubCommentId,
            AuthorLogin = "alice", CommandName = AgentCommandNames.Review, Arguments = "{}",
            ParserVersion = "1", Status = AgentCommandStatuses.Authorized,
            SourceBodySnapshot = comment.Body, CreatedAt = DateTime.UtcNow, ParsedAt = DateTime.UtcNow,
        };
        db.AgentCommands.Add(command);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var outcome = await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);
        await db.SaveChangesAsync();

        Assert.Null(outcome);
        var (_, task, _) = await LoadAdmittedAsync(db, workItem, command);
        Assert.Equal("head-abc", task!.TargetHeadSha);
        Assert.Equal("base-def", task.TargetBaseSha);
    }

    // ---- end to end through the task-06 processor harness ----

    [Fact]
    public async Task AuthorizedCommand_EndsQueuedWithTaskAndJob()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, body: "@jsxhuz /plan");
        await CommandTestSupport.SeedCommandEventAsync(db, comment, workItem);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        db.ChangeTracker.Clear();
        var harness = CommandTestSupport.NewProcessor(postgres.ConnectionString);
        harness.Handler.EnqueueResponse(PermissionResponse("admin", "admin"));

        db.ChangeTracker.Clear();
        await harness.Processor.RunCycleAsync(default);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Queued, command.Status);
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.TriggerCommandId == command.Id);
        Assert.Equal(OrchestrationTaskStatuses.Queued, task.Status);
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync(j => j.TaskId == task.Id);
        Assert.Equal(OrchestrationJobStatuses.Pending, job.Status);
    }

    [Fact]
    public async Task SameWorkflowSecondComment_RejectedReplyEnqueued()
    {
        // first command admitted end-to-end; second comment event → admission rejection + Outbox reply
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, body: "@jsxhuz /plan");
        await CommandTestSupport.SeedCommandEventAsync(db, comment, workItem);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var commentAt = DateTime.UtcNow.AddMinutes(-5); // one captured instant: updated == created (never edited)
        var secondComment = new GitHubComment
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = 987654,
            Kind = GitHubCommentKinds.IssueComment, AuthorLogin = "bob", Body = "@jsxhuz /plan",
            CreatedAtGithub = commentAt, UpdatedAtGithub = commentAt,
            LastSyncedAt = DateTime.UtcNow,
        };
        db.GitHubComments.Add(secondComment);
        await CommandTestSupport.SeedCommandEventAsync(db, secondComment, workItem);
        db.ChangeTracker.Clear();
        var harness = CommandTestSupport.NewProcessor(postgres.ConnectionString);
        // one 200 per command comment: alice (admitted) then bob (rejected at admission)
        harness.Handler.EnqueueResponse(PermissionResponse("admin", "admin"));
        harness.Handler.EnqueueResponse(PermissionResponse("admin", "admin"));

        db.ChangeTracker.Clear();
        await harness.Processor.RunCycleAsync(default);

        var first = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        var second = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == secondComment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Queued, first.Status);
        Assert.Equal(AgentCommandStatuses.Rejected, second.Status);
        Assert.Equal(AgentCommandRejectionReasons.UnfinishedTaskExists, second.RejectionReason);
        Assert.Single(await db.OutboxActions.AsNoTracking().ToListAsync()); // exactly the rejection reply
        Assert.Equal(1, await db.OrchestrationTasks.AsNoTracking().CountAsync());
    }

    // ---- retry policy pinning ----

    [Fact]
    public async Task RetryPolicy_PinnedFromProfileOverSystemDefaults()
    {
        var (db, _, workItem, command) = await NewAuthorizedAsync(AgentCommandNames.Plan);
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan,
            retryJson: """{"max_automatic_retries": 4, "initial_backoff": "00:02:00"}""");

        await CommandAdmission.AdmitAsync(db, command, workItem, db.Repositories.AsNoTracking().Single(), botLogin: null, default);
        await db.SaveChangesAsync();

        var (_, task, _) = await LoadAdmittedAsync(db, workItem, command);
        using var policy = JsonDocument.Parse(task!.RetryPolicy);
        Assert.Equal(4, policy.RootElement.GetProperty("max_automatic_retries").GetInt32());
        Assert.Equal(120, policy.RootElement.GetProperty("initial_backoff_seconds").GetInt32());
    }

    private static HttpResponseMessage PermissionResponse(string permission, string roleName) =>
        new(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.StringContent($$"""{"permission": "{{permission}}", "role_name": "{{roleName}}"}""") };
}
