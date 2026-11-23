using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Commands;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Context;
using Xunit;

namespace PiAgent.Hosts.Tests.Orchestration;

/// <summary>
/// The Orchestrator-side Context Assembler. Plans see the issue body + every synced comment
/// chronologically (tombstones excluded but recorded as exclusions); coders carry the never-pruned
/// approved-plan Artifact plus agent-branch facts; reviewers get the pinned Head/Base revisions with
/// reviews + grouped inline threads; budget overflows prune newest-first with per-element exclusion
/// records, and mandatory context that cannot fit is a `context_too_large` assembly (the claim worker
/// turns that into a terminal failed Task with no Run rows — covered by the claim test below).
/// </summary>
public class ContextAssemblerTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken TestToken => default;

    private static async Task<(Repository Repo, WorkItem WorkItem, AgentProfileVersion Version, GitHubComment SeededComment)> SeedAsync(
        AppDbContext db, bool pullRequest = false, string? body = null)
    {
        var (repo, workItem, seededComment) = await CommandTestSupport.SeedAsync(
            db, pullRequest: pullRequest,
            existingRepository: await db.Repositories.AsNoTracking().FirstOrDefaultAsync());
        if (body is not null)
        {
            workItem.Body = body; // CommandTestSupport's `body` parameter is the seeded COMMENT's body
            await db.SaveChangesAsync();
        }
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(
            db, OrchestrationTaskTypes.Plan, ProfileVersionStates.Ready);
        version.MaxContextTokens = 200000;
        await db.SaveChangesAsync();
        return (repo, workItem, version, seededComment);
    }

    private static GitHubComment SeedComment(
        AppDbContext db, WorkItem workItem, long githubCommentId, string author, string body,
        string kind = GitHubCommentKinds.IssueComment, DateTime? createdAt = null, long? inReplyTo = null,
        long? reviewId = null, string? path = null, int? diffPosition = null, bool deleted = false)
    {
        var comment = new GitHubComment
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            GithubCommentId = githubCommentId,
            Kind = kind,
            AuthorLogin = author,
            Body = body,
            CreatedAtGithub = createdAt ?? DateTime.UtcNow.AddMinutes(-5),
            UpdatedAtGithub = createdAt?.AddSeconds(30) ?? DateTime.UtcNow.AddMinutes(-5).AddSeconds(30),
            InReplyToCommentId = inReplyTo,
            GithubReviewId = reviewId,
            Path = path,
            DiffPosition = diffPosition,
            IsDeleted = deleted,
            DeletedAt = deleted ? DateTime.UtcNow : null,
            LastSyncedAt = DateTime.UtcNow,
        };
        db.GitHubComments.Add(comment);
        return comment;
    }

    private static async Task<OrchestrationTask> SeedTaskAsync(
        AppDbContext db, WorkItem workItem, AgentProfileVersion version, string type = OrchestrationTaskTypes.Plan,
        string? targetHeadSha = null, string? targetBaseSha = null)
    {
        var workflow = await db.Workflows.SingleOrDefaultAsync(w => w.WorkItemId == workItem.Id);
        if (workflow is null)
        {
            workflow = new Workflow
            {
                Id = Guid.NewGuid(), WorkItemId = workItem.Id,
                Status = WorkflowStatuses.WaitingForHuman, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Workflows.Add(workflow);
            await db.SaveChangesAsync();
        }
        var maxSequence = await db.OrchestrationTasks
            .Where(t => t.WorkflowId == workflow.Id)
            .MaxAsync(t => (int?)t.Sequence) ?? 0;
        var task = new OrchestrationTask
        {
            Id = Guid.NewGuid(),
            WorkflowId = workflow.Id,
            Sequence = maxSequence + 1,
            Type = type,
            AgentProfileVersionId = version.Id,
            Status = OrchestrationTaskStatuses.Queued,
            TriggerCommandId = CommandTestSupport.SeedTriggerCommand(db, workItem).Id,
            TargetHeadSha = targetHeadSha,
            TargetBaseSha = targetBaseSha,
            CreatedAt = DateTime.UtcNow,
            RetryPolicy = """{"max_automatic_retries":1}""",
        };
        db.OrchestrationTasks.Add(task);
        await db.SaveChangesAsync();
        return task;
    }

    private static async Task<ContextAssemblyResult> AssembleAsync(
        AppDbContext db, OrchestrationTask task, AgentProfileVersion version, WorkItem workItem, Repository repo, AgentCommand? command = null)
    {
        db.ChangeTracker.Clear();
        var reloadedTask = await db.OrchestrationTasks.AsNoTracking().SingleAsync(t => t.Id == task.Id);
        var reloadedWorkItem = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workItem.Id);
        var reloadedRepo = await db.Repositories.AsNoTracking().SingleAsync(r => r.Id == repo.Id);
        var reloadedWorkflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.Id == reloadedTask.WorkflowId);
        var reloadedVersion = await db.AgentProfileVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id);
        return await ContextAssembler.AssembleAsync(
            db, reloadedTask, reloadedVersion, reloadedWorkflow, reloadedWorkItem, reloadedRepo, command, TestToken);
    }

    private static JsonElement Content(ContextAssembly assembly) => JsonDocument.Parse(assembly.Content).RootElement;

    private static JsonElement Excluded(ContextAssembly assembly) => JsonDocument.Parse(assembly.ExcludedSources).RootElement;

    private static JsonElement Included(ContextAssembly assembly) => JsonDocument.Parse(assembly.IncludedSources).RootElement;

    private static string Reason(JsonElement entry) => entry.GetProperty("reason").GetString()!;

    // ---- plan-Task context ----

    [Fact]
    public async Task PlanTask_ContainsIssueBodyAndAllCommentsChronologically_TombstonesExcluded()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, version, seededComment) = await SeedAsync(db, body: "Implement the widget");
        var baseTime = DateTime.UtcNow.AddHours(-2);
        SeedComment(db, workItem, 101, "planner-bot", "Plan from an earlier task", createdAt: baseTime);
        SeedComment(db, workItem, 102, "alice", "Earlier plan answer: prefer approach A", createdAt: baseTime.AddMinutes(10));
        SeedComment(db, workItem, 103, "carol", "deleted complaint", createdAt: baseTime.AddMinutes(20), deleted: true);
        SeedComment(db, workItem, 104, "alice", "Follow-up for the plan", createdAt: baseTime.AddMinutes(30));
        await db.SaveChangesAsync();
        var command = new AgentCommand
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            GithubCommentId = 104,
            AuthorLogin = "alice",
            CommandName = AgentCommandNames.Plan,
            ParserVersion = "1",
            Status = AgentCommandStatuses.Queued,
            SourceBodySnapshot = "@bot /plan focus on the auth module",
            CreatedAt = DateTime.UtcNow,
            ParsedAt = DateTime.UtcNow,
        };
        db.AgentCommands.Add(command);
        var task = await SeedTaskAsync(db, workItem, version);
        await db.SaveChangesAsync();

        var result = await AssembleAsync(db, task, version, workItem, repo, command);
        Assert.False(result.TooLarge, result.TooLargeDetail);
        var content = Content(result.Assembled!);

        Assert.Equal(2, content.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("plan", content.GetProperty("command").GetString());
        Assert.Equal("@bot /plan focus on the auth module", content.GetProperty("commandComment").GetProperty("text").GetString());
        Assert.Equal(104, content.GetProperty("commandComment").GetProperty("githubCommentId").GetInt64());
        var workItemJson = content.GetProperty("workItem");
        Assert.Equal("Implement the widget", workItemJson.GetProperty("body").GetString());
        Assert.Equal("octo-org/example", workItemJson.GetProperty("repository").GetString());
        Assert.Equal(workItem.Number, workItemJson.GetProperty("number").GetInt64());
        Assert.Equal("octo-org/example", content.GetProperty("repository").GetProperty("fullName").GetString());
        Assert.Equal("main", content.GetProperty("repository").GetProperty("defaultBranch").GetString());
        var profileName = await db.AgentProfiles.AsNoTracking()
            .Where(p => p.Id == (db.AgentProfileVersions.AsNoTracking()
                .Single(v => v.Id == task.AgentProfileVersionId)).AgentProfileId)
            .Select(p => p.Name).SingleAsync();
        Assert.Equal(profileName, content.GetProperty("profile").GetProperty("name").GetString());
        Assert.Equal(ContextInstructions.For(OrchestrationTaskTypes.Plan), content.GetProperty("instructions").GetString());

        var conversation = content.GetProperty("conversation");
        var bodyIds = conversation.EnumerateArray()
            .Select(comment => comment.GetProperty("githubCommentId").GetInt64())
            .ToList();
        // chronological (oldest first): the tombstone 103 is absent; the seeded base comment is the newest
        Assert.Equal([101, 102, 104, seededComment.GithubCommentId], bodyIds);

        var excluded = Excluded(result.Assembled!);
        var tombstone = excluded.EnumerateArray().Single(entry => Reason(entry) == "deleted_comment");
        Assert.Equal("comment", tombstone.GetProperty("kind").GetString());
        Assert.Equal(103, tombstone.GetProperty("githubCommentId").GetInt64());
        Assert.Equal("deleted_comment", Reason(tombstone));

        var included = Included(result.Assembled!).EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToList();
        Assert.Contains("work_item", included);
        Assert.Contains("repository", included);
        Assert.Contains("command_comment", included);
    }

    // ---- coder-Task context ----

    [Fact]
    public async Task CoderTask_CarriesApprovedPlanArtifactAndAgentBranchFacts()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, version, _) = await SeedAsync(db);
        var priorTask = await SeedTaskAsync(db, workItem, version, type: OrchestrationTaskTypes.Plan);
        priorTask.Status = OrchestrationTaskStatuses.Succeeded; // the prior task finished (one-unfinished invariant)
        priorTask.CompletedAt = DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        var priorWorkflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == workItem.Id);
        var planArtifact = new Artifact
        {
            Id = Guid.NewGuid(), TaskId = priorTask.Id, WorkflowId = priorWorkflow.Id,
            Type = ArtifactTypes.Plan, Format = ArtifactFormats.Markdown, Content = "# The approved plan", CreatedAt = DateTime.UtcNow,
        };
        var oldSummary = new Artifact
        {
            Id = Guid.NewGuid(), TaskId = priorTask.Id, WorkflowId = priorWorkflow.Id,
            Type = ArtifactTypes.ImplementationSummary, Format = ArtifactFormats.Text, Content = "old summary", CreatedAt = DateTime.UtcNow,
        };
        var logReference = new Artifact
        {
            Id = Guid.NewGuid(), TaskId = priorTask.Id, WorkflowId = priorWorkflow.Id,
            Type = ArtifactTypes.LogReference, Format = ArtifactFormats.Text, Content = "/var/lib/pi-agent/workflows/wf/runs/r1/logs", CreatedAt = DateTime.UtcNow,
        };
        var task = await SeedTaskAsync(db, workItem, version, type: OrchestrationTaskTypes.Implement);
        var workspace = new Workspace
        {
            Id = Guid.NewGuid(), TaskId = task.Id, WorkItemId = workItem.Id, RepositoryId = repo.Id,
            BranchName = "agent/issue-" + workItem.Number, BaseSha = "base-abc", CurrentSha = "head-xyz",
            Status = WorkspaceStatuses.Ready, CreatedAt = DateTime.UtcNow,
        };
        db.Artifacts.AddRange(planArtifact, oldSummary, logReference);
        db.Workspaces.Add(workspace);
        db.TaskArtifactInputs.Add(new TaskArtifactInput
        {
            Id = Guid.NewGuid(), TaskId = task.Id, ArtifactId = planArtifact.Id,
            Purpose = ArtifactInputPurposes.ApprovedPlan, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await AssembleAsync(db, task, version, workItem, repo, command: null);
        Assert.False(result.TooLarge, result.TooLargeDetail);
        var content = Content(result.Assembled!);

        // the approved plan is mandatory: its content is embedded with the recorded purpose
        var artifacts = content.GetProperty("artifacts");
        var planEntry = artifacts.EnumerateArray().Single(a => a.GetProperty("id").GetString() == planArtifact.Id.ToString());
        Assert.Equal("approved_plan", planEntry.GetProperty("purpose").GetString());
        Assert.Equal("# The approved plan", planEntry.GetProperty("content").GetString());
        // prior Workflow artifacts are context; a log_reference travels by reference only
        Assert.Equal("old summary",
            artifacts.EnumerateArray().Single(a => a.GetProperty("id").GetString() == oldSummary.Id.ToString()).GetProperty("content").GetString());
        var logEntry = artifacts.EnumerateArray().Single(a => a.GetProperty("id").GetString() == logReference.Id.ToString());
        Assert.Equal(ArtifactTypes.LogReference, logEntry.GetProperty("type").GetString());
        Assert.Equal(logReference.Content, logEntry.GetProperty("reference").GetString());
        Assert.True(logEntry.GetProperty("content").ValueKind is JsonValueKind.Null);
        // coder git facts come from the Workspace row
        var git = content.GetProperty("git");
        Assert.Equal("agent/issue-" + workItem.Number, git.GetProperty("branch").GetString());
        Assert.Equal("base-abc", git.GetProperty("baseSha").GetString());
        Assert.Equal("head-xyz", git.GetProperty("currentSha").GetString());
        // no command: the verb falls back to the task type and no commandComment section exists
        Assert.Equal("implement", content.GetProperty("command").GetString());
        Assert.False(content.TryGetProperty("commandComment", out _));
        Assert.Equal(ContextInstructions.For(OrchestrationTaskTypes.Implement), content.GetProperty("instructions").GetString());
    }

    // ---- reviewer-Task context ----

    [Fact]
    public async Task ReviewTask_PinsHeadBaseRevisions_GroupsThreads_NewestFirst()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, pullRequest, version, seededComment) = await SeedAsync(db, pullRequest: true);
        var baseTime = DateTime.UtcNow.AddHours(-1);
        db.GitHubReviews.AddRange(
            new GitHubReview
            {
                Id = Guid.NewGuid(), WorkItemId = pullRequest.Id, GithubReviewId = 11, AuthorLogin = "dave",
                State = GitHubReviewStates.Approved, Body = "approve", CommitSha = "head-aaa",
                SubmittedAtGithub = baseTime, LastSyncedAt = DateTime.UtcNow,
            },
            new GitHubReview
            {
                Id = Guid.NewGuid(), WorkItemId = pullRequest.Id, GithubReviewId = 22, AuthorLogin = "erin",
                State = GitHubReviewStates.ChangesRequested, Body = "please fix the loop", CommitSha = "head-bbb",
                SubmittedAtGithub = baseTime.AddMinutes(30), LastSyncedAt = DateTime.UtcNow,
            });
        // thread A (older): root + two replies (one chained to a reply)
        SeedComment(db, pullRequest, 201, "erin", "loop is wrong", kind: GitHubCommentKinds.ReviewComment,
            createdAt: baseTime, inReplyTo: null, reviewId: 22, path: "src/loop.cs", diffPosition: 12);
        SeedComment(db, pullRequest, 202, "alice", "re: loop", kind: GitHubCommentKinds.ReviewComment,
            createdAt: baseTime.AddMinutes(1), inReplyTo: 201, reviewId: 22);
        SeedComment(db, pullRequest, 203, "erin", "re2: loop", kind: GitHubCommentKinds.ReviewComment,
            createdAt: baseTime.AddMinutes(2), inReplyTo: 202, reviewId: 22);
        // thread B (newer): standalone root
        SeedComment(db, pullRequest, 301, "dave", "naming nit", kind: GitHubCommentKinds.ReviewComment,
            createdAt: baseTime.AddMinutes(40), inReplyTo: null, reviewId: 11, path: "src/naming.cs", diffPosition: 5);
        // PR conversation comment (issue_comment kind) stays in the conversation, not a thread
        SeedComment(db, pullRequest, 401, "frank", "conversation comment", kind: GitHubCommentKinds.IssueComment,
            createdAt: baseTime.AddMinutes(45));
        await db.SaveChangesAsync();
        var task = await SeedTaskAsync(db, pullRequest, version,
            type: OrchestrationTaskTypes.Review, targetHeadSha: "head-pinned", targetBaseSha: "base-pinned");

        var result = await AssembleAsync(db, task, version, pullRequest, repo, null);
        Assert.False(result.TooLarge, result.TooLargeDetail);
        var content = Content(result.Assembled!);

        // pinned revisions for the runtime diff
        Assert.Equal("head-pinned", content.GetProperty("task").GetProperty("targetHeadSha").GetString());
        Assert.Equal("base-pinned", content.GetProperty("task").GetProperty("targetBaseSha").GetString());
        Assert.Equal("review", content.GetProperty("command").GetString());
        Assert.Equal(ContextInstructions.For(OrchestrationTaskTypes.Review), content.GetProperty("instructions").GetString());

        var reviewList = content.GetProperty("reviews").EnumerateArray().ToList();
        Assert.Equal([22, 11], reviewList.Select(review => review.GetProperty("githubReviewId").GetInt64())); // newest first
        Assert.Equal("changes_requested", reviewList[0].GetProperty("state").GetString());
        Assert.Equal("please fix the loop", reviewList[0].GetProperty("body").GetString());

        var threads = content.GetProperty("threads").EnumerateArray().ToList();
        Assert.Equal(2, threads.Count);
        // newest thread first; unknown resolution (`resolved: null`) is treated as unresolved
        Assert.Equal(301, threads[0].GetProperty("githubCommentId").GetInt64());
        Assert.Equal(201, threads[1].GetProperty("githubCommentId").GetInt64());
        Assert.True(threads[0].GetProperty("resolved").ValueKind is JsonValueKind.Null);
        // the thread carries state, file/line bindings, and its replies (including the chained one)
        Assert.Equal("changes_requested", threads[1].GetProperty("state").GetString());
        Assert.Equal("src/loop.cs", threads[1].GetProperty("path").GetString());
        Assert.Equal(12, threads[1].GetProperty("line").GetInt32());
        var threadComments = threads[1].GetProperty("comments").EnumerateArray()
            .Select(comment => comment.GetProperty("githubCommentId").GetInt64()).ToList();
        Assert.Equal([201, 202, 203], threadComments); // root first, then the reply chain

        // the review conversation comments are conversation elements, not thread members
        var conversationIds = content.GetProperty("conversation").EnumerateArray()
            .Select(comment => comment.GetProperty("githubCommentId").GetInt64()).ToList();
        Assert.Equal([401, seededComment.GithubCommentId], conversationIds);
    }

    // ---- budget pruning + context_too_large ----

    [Fact]
    public async Task BudgetPrunesOlderElementsNewestFirst_CommandAndPlanSurvive()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, version, seededComment) = await SeedAsync(db);
        version.MaxContextTokens = 900; // 3.6 KiB serialized-input budget: mandatory core + plan + recent comments fit
        await db.SaveChangesAsync();
        var planTask = await SeedTaskAsync(db, workItem, version);
        var workflow = await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == workItem.Id);
        var planArtifact = new Artifact
        {
            Id = Guid.NewGuid(), TaskId = planTask.Id, WorkflowId = workflow.Id,
            Type = ArtifactTypes.Plan, Format = ArtifactFormats.Markdown, Content = "PLAN", CreatedAt = DateTime.UtcNow,
        };
        db.Artifacts.Add(planArtifact);
        SeedComment(db, workItem, 501, "carol", new string('c', 1500), createdAt: DateTime.UtcNow.AddMinutes(-10));
        SeedComment(db, workItem, 502, "alice", new string('a', 1500), createdAt: DateTime.UtcNow.AddMinutes(-1));
        await db.SaveChangesAsync();
        var command = new AgentCommand
        {
            Id = Guid.NewGuid(), WorkItemId = workItem.Id, GithubCommentId = 502, AuthorLogin = "alice",
            CommandName = AgentCommandNames.Plan, ParserVersion = "1", Status = AgentCommandStatuses.Queued,
            SourceBodySnapshot = "@bot /plan", CreatedAt = DateTime.UtcNow, ParsedAt = DateTime.UtcNow,
        };
        db.AgentCommands.Add(command);
        var taskEntity = await db.OrchestrationTasks.SingleAsync(t => t.Id == planTask.Id);
        db.TaskArtifactInputs.Add(new TaskArtifactInput
        {
            Id = Guid.NewGuid(), TaskId = taskEntity.Id, ArtifactId = planArtifact.Id,
            Purpose = ArtifactInputPurposes.ApprovedPlan, CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await AssembleAsync(db, planTask, version, workItem, repo, command);
        Assert.False(result.TooLarge, result.TooLargeDetail);
        var assembly = result.Assembled!;
        var content = Content(assembly);

        // mandatory context is intact: command comment, work item, and the approved plan
        Assert.Equal("@bot /plan", content.GetProperty("commandComment").GetProperty("text").GetString());
        Assert.Equal("PLAN", content.GetProperty("artifacts").EnumerateArray()
            .Single(entry => entry.GetProperty("id").GetString() == planArtifact.Id.ToString()).GetProperty("content").GetString());
        // newest-first kept: the NEWER comment survives the squeeze, the older one is excluded
        var conversation = content.GetProperty("conversation").EnumerateArray()
            .Select(comment => comment.GetProperty("githubCommentId").GetInt64()).ToList();
        // chronological in the document: the seeded comment (-5min) precedes 502 (-1min); 501 (-10min) is pruned
        Assert.Equal([seededComment.GithubCommentId, 502], conversation);

        var excluded = Excluded(assembly).EnumerateArray()
            .Where(entry => Reason(entry) == "pruned_by_limit").ToList();
        Assert.Contains(excluded, entry => entry.GetProperty("kind").GetString() == "comment"
            && entry.GetProperty("githubCommentId").GetInt64() == 501);

        // the serialized input stays within both the token budget and the 512 KiB hard byte cap
        var bytes = Encoding.UTF8.GetByteCount(assembly.Content);
        Assert.True(bytes <= ContextBudgets.MainBudgetBytes(version.MaxContextTokens), $"{bytes} bytes exceed the budget");
        Assert.True(ContextTokenEstimator.EstimateTokens(bytes) <= version.MaxContextTokens);
        Assert.Equal(64, assembly.ContentHash().Length);
    }

    [Fact]
    public async Task MandatoryContextBeyondBudget_AssemblyFailsWithContextTooLarge()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, version, _) = await SeedAsync(db, body: new string('x', 50_000));
        version.MaxContextTokens = 1_000; // 4 KiB budget: the huge body alone cannot fit
        await db.SaveChangesAsync();
        var task = await SeedTaskAsync(db, workItem, version);

        var result = await AssembleAsync(db, task, version, workItem, repo, null);

        Assert.True(result.TooLarge);
        Assert.Null(result.Assembled);
        Assert.Equal("context_too_large", ContextAssemblyResult.FailureCode);
        Assert.Contains("workItem", result.TooLargeDetail);
    }

    [Fact]
    public async Task ArtifactBudgets_ExclusionsAreRecorded_PerElement()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, workItem, version, _) = await SeedAsync(db);
        var baseTime = DateTime.UtcNow;
        var task = await SeedTaskAsync(db, workItem, version);
        var workflowId = (await db.Workflows.AsNoTracking().SingleAsync(w => w.WorkItemId == workItem.Id)).Id;
        var oversized = new Artifact
        {
            Id = Guid.NewGuid(), TaskId = task.Id, WorkflowId = workflowId,
            Type = ArtifactTypes.Custom, Format = ArtifactFormats.Text,
            // corrupt row (artifacts are validated ≤256 KiB at creation): the assembler must record, not crash
            Content = new string('o', 300 * 1024), CreatedAt = baseTime,
        };
        var oldArtifact = new Artifact
        {
            Id = Guid.NewGuid(), TaskId = task.Id, WorkflowId = workflowId,
            Type = ArtifactTypes.ImplementationSummary, Format = ArtifactFormats.Text,
            Content = new string('s', 1500), CreatedAt = baseTime.AddMinutes(-1),
        };
        db.Artifacts.AddRange(oversized, oldArtifact);
        await db.SaveChangesAsync();

        var result = await AssembleAsync(db, task, version, workItem, repo, null);
        Assert.False(result.TooLarge, result.TooLargeDetail);
        var assembly = result.Assembled!;
        var artifacts = Content(assembly).GetProperty("artifacts").EnumerateArray()
            .Select(entry => entry.GetProperty("id").GetString()).ToList();

        Assert.True(artifacts.Contains(oldArtifact.Id.ToString()), "the small artifact fits and is kept");
        Assert.DoesNotContain(oversized.Id.ToString(), artifacts);

        var excluded = Excluded(assembly).EnumerateArray()
            .Where(entry => entry.GetProperty("kind").GetString() == "artifact").ToList();
        Assert.Contains(excluded, entry => Reason(entry) == "artifact_too_large");
    }

    // ---- related WorkItems ----

    [Fact]
    public async Task RelatedWorkItems_CarryTheLinks()
    {
        await postgres.ResetAsync();
        await using var db = postgres.NewDbContext();
        var (repo, issue, version, _) = await SeedAsync(db, pullRequest: false);
        var pullRequest = new WorkItem
        {
            Id = Guid.NewGuid(), RepositoryId = repo.Id, GithubNodeId = "node-pr", Number = 900,
            Type = WorkItemTypes.PullRequest, Title = "The PR", Body = "pr body", State = "open",
            AuthorLogin = "someone", Assignees = "[]", Labels = "[]",
            CreatedAtGithub = DateTime.UtcNow, UpdatedAtGithub = DateTime.UtcNow, LastSyncedAt = DateTime.UtcNow,
        };
        db.WorkItems.Add(pullRequest);
        var task = await SeedTaskAsync(db, issue, version);
        task.Status = OrchestrationTaskStatuses.Succeeded;
        task.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        db.WorkItemLinks.Add(new WorkItemLink
        {
            Id = Guid.NewGuid(), IssueWorkItemId = issue.Id, PrWorkItemId = pullRequest.Id,
            RelationType = WorkItemLinkRelations.Closes, Source = WorkItemLinkSources.GithubClosingReference,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();

        var result = await AssembleAsync(db, task, version, issue, repo, null);
        Assert.False(result.TooLarge, result.TooLargeDetail);
        var link = Content(result.Assembled!).GetProperty("relatedWorkItems").EnumerateArray().Single();
        Assert.Equal("closes", link.GetProperty("relation").GetString());
        Assert.Equal("pull_request", link.GetProperty("otherType").GetString());
        Assert.Equal(900, link.GetProperty("number").GetInt64());
        Assert.Equal("octo-org/example", link.GetProperty("repository").GetString());
        Assert.Contains(Included(result.Assembled!).EnumerateArray(),
            source => source.GetProperty("kind").GetString() == "related_work_item"
                && source.GetProperty("reference").GetString() == "octo-org/example#900");
    }

    // ---- claim integration: no Run rows ever exist when the context does not fit ----

    [Fact]
    public async Task Claim_ContextTooLarge_FailsTheTaskTerminal_NeverPreparesARun()
    {
        await postgres.ResetAsync();
        var seedDb = postgres.NewDbContext();
        var (repo, workItem, _) = await CommandTestSupport.SeedAsync(seedDb);
        workItem.Body = new string('x', 50_000); // the huge body the budgets cannot accommodate
        var (_, version, _) = await CommandTestSupport.SeedProfileAsync(
            seedDb, OrchestrationTaskTypes.Plan, ProfileVersionStates.Ready);
        version.MaxContextTokens = 1_000; // the huge body cannot fit
        await OrchestrationTestSupport.SeedQueuedAsync(seedDb, workItem, version, withCommand: true, withRun: false);
        await seedDb.SaveChangesAsync();
        seedDb.ChangeTracker.Clear();
        await seedDb.DisposeAsync();

        var harness = OrchestrationTestSupport.NewWorker(postgres);
        harness.Handler.Fallback = _ => new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new System.Net.Http.StringContent(
                """{"permission": "admin", "role_name": "admin"}""", Encoding.UTF8, "application/json"),
        };
        await harness.Worker.RunCycleAsync(TestToken);

        var db = postgres.NewDbContext();
        var task = await db.OrchestrationTasks.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationTaskStatuses.Failed, task.Status);
        Assert.Equal("context_too_large", task.FailureReason);
        Assert.NotNull(task.CompletedAt);
        var job = await db.OrchestrationJobs.AsNoTracking().SingleAsync();
        Assert.Equal(OrchestrationJobStatuses.Failed, job.Status);
        var command = await db.AgentCommands.AsNoTracking().SingleAsync();
        Assert.Equal(AgentCommandStatuses.Failed, command.Status);
        Assert.Equal("context_too_large", command.RejectionReason);
        var workflow = await db.Workflows.AsNoTracking().SingleAsync();
        Assert.Equal(WorkflowStatuses.WaitingForHuman, workflow.Status);
        Assert.Null(workflow.CurrentTaskId);
        Assert.False(await db.Runs.AsNoTracking().AnyAsync());
        Assert.False(await db.RunContextSnapshots.AsNoTracking().AnyAsync());
        Assert.False(await db.Workspaces.AsNoTracking().AnyAsync());
        Assert.Equal(1, harness.Metrics.ClaimContextTooLargeTotal);
    }
}
