using System.Net;
using Microsoft.EntityFrameworkCore;
using PiAgent.Hosts.Tests.Config;
using PiAgent.Hosts.Tests.Poller;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator;
using PiAgent.Orchestrator.Commands;
using PiAgent.Orchestrator.Config;
using Xunit;

namespace PiAgent.Hosts.Tests.Commands;

/// <summary>
/// The comment-command pipeline over real postgres with a stubbed GitHub (task 06): grammar outcomes
/// → AgentCommand rows (received/authorized/rejected + persisted reason), decision-45 context
/// carve-out, idempotent replay per comment ID, policy checks (pause, permitted commands/types,
/// target types), authorization (allowlist, permission API, transient deferral, revoked-PAT config
/// error), cold-start deferral, and Outbox rejection replies wired to ticket 03's dispatcher.
/// </summary>
public class CommentCommandProcessorTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private const string Bot = "jsxhuz";

    // ---- happy path ----

    [Fact]
    public async Task ValidCommand_Authorized_WithSnapshotAndArguments()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync("@jsxhuz /plan extra context");
        AuthStub200(harness.Handler);
        // task 07: routing must exist for /plan to pass admission
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        // task 07: the accepted command continues past authorization into admission → queued (02 §11)
        Assert.Equal(AgentCommandStatuses.Queued, command.Status);
        Assert.Equal(AgentCommandNames.Plan, command.CommandName);
        Assert.Equal("@jsxhuz /plan extra context", command.SourceBodySnapshot);
        Assert.Equal("{}", command.Arguments);
        Assert.Equal("1", command.ParserVersion);
        Assert.Equal("alice", command.AuthorLogin);
        Assert.Equal(workItem.Id, command.WorkItemId);
        Assert.Null(command.RejectionReason);
        var githubEvent = await EventAsync(db, comment);
        Assert.Equal(GitHubEventStatuses.Processed, githubEvent.ProcessingStatus);
        Assert.NotNull(githubEvent.ProcessedAt);
        Assert.Equal(1, harness.Metrics.CommandsAuthorizedTotal);
    }

    // ---- decision 45 + bot + review comments ----

    [Fact]
    public async Task MentionOnlyComment_NoRow_NoOutbox()
    {
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz can you look at this when you get a chance");
        await RunAsync(db, harness);

        Assert.Empty(await db.AgentCommands.AsNoTracking().ToListAsync());
        Assert.Empty(await db.OutboxActions.AsNoTracking().ToListAsync());
        Assert.Equal(GitHubEventStatuses.Processed, (await EventAsync(db, comment)).ProcessingStatus);
    }

    [Fact]
    public async Task BotAuthoredDirective_Ignored_NoRow()
    {
        var (db, harness, comment, _) = await NewCaseAsync(body: "@jsxhuz /plan", authorLogin: Bot);
        await RunAsync(db, harness);

        Assert.Empty(await db.AgentCommands.AsNoTracking().ToListAsync());
        Assert.Equal(GitHubEventStatuses.Processed, (await EventAsync(db, comment)).ProcessingStatus);
    }

    [Fact]
    public async Task ReviewCommentEventsNeverClaimed()
    {
        var (db, harness, comment, workItem) = await NewCaseAsync();
        AuthStub200(harness.Handler);
        var reviewEvent = new GitHubEvent
        {
            Id = Guid.NewGuid(),
            RepositoryId = workItem.RepositoryId,
            WorkItemId = workItem.Id,
            Source = GitHubEventSources.RestResourceSync,
            DedupeKey = "rc:999:created",
            EventType = GitHubEventTypes.ReviewCommentCreated,
            Payload = """{"comment_id": 999, "issue_number": 1, "author": "alice"}""",
            OccurredAtGithub = DateTime.UtcNow,
            ReceivedAt = DateTime.UtcNow,
            ProcessingStatus = GitHubEventStatuses.Pending,
        };
        db.GitHubEvents.Add(reviewEvent);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RunAsync(db, harness);

        Assert.Equal(GitHubEventStatuses.Pending,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == reviewEvent.Id)).ProcessingStatus);
        Assert.Equal(GitHubEventStatuses.Processed, (await EventAsync(db, comment)).ProcessingStatus);
    }

    // ---- rejections with persisted reasons + Outbox reply ----

    [Fact]
    public async Task UnknownCommand_Rejected_WithReplyEnqueued()
    {
        var (db, harness, comment, _) = await NewCaseAsync("  @jsxhuz /frobnicate");
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.UnknownCommand, command.RejectionReason);
        Assert.Null(command.CommandName);

        var reply = await db.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Contains("not a supported command", reply.Payload);
        // the supported-commands line is DERIVED (owner decision B): the default repo config permits
        // all eight commands, and the canonical test_cases renders as the /test-cases token
        Assert.Contains("Supported commands: `/plan`, `/code`, `/review`, `/test-cases`, `/status`, `/stop`, `/help`, `/retry`",
            reply.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_ReplyListsRepositoryPermittedCommands()
    {
        const string config = """{"allowlist": {"enabled": false, "logins": []}, "permitted_commands": ["plan", "status"], "permitted_work_item_types": ["issue", "pull_request"], "authorization": {"minimum_permission": "write"}}""";
        var (db, harness, comment, _) = await NewCaseAsync(body: "  @jsxhuz /frobnicate", configuration: config);
        await RunAsync(db, harness);

        var reply = await db.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Contains("Supported commands: `/plan`, `/status`", reply.Payload, StringComparison.Ordinal);
        Assert.DoesNotContain("/help", reply.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TwoDirectives_Rejected_Ambiguous()
    {
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz /plan\n@jsxhuz /stop");
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.AmbiguousCommand, command.RejectionReason);
    }

    [Fact]
    public async Task QuotedDirective_Rejected()
    {
        var (db, harness, comment, _) = await NewCaseAsync("> @jsxhuz /plan");
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.CommandInQuoteOrCodeBlock, command.RejectionReason);
    }

    [Fact]
    public async Task AlreadyEditedWhenFirstSeen_Rejected_CommandEdited()
    {
        var (db, harness, comment, _) = await NewCaseAsync(body: "@jsxhuz /plan", editedAlready: true);
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.CommandEdited, command.RejectionReason);
        Assert.Null(command.CommandName);
    }

    [Fact]
    public async Task ReplaySameEvent_DoesNotDuplicateRowOrReply()
    {
        var (db, harness, comment, _) = await NewCaseAsync("> @jsxhuz /plan");
        await RunAsync(db, harness);
        await RunAsync(db, harness);

        Assert.Equal(1, await db.AgentCommands.AsNoTracking().CountAsync(c => c.GithubCommentId == comment.GithubCommentId));
        Assert.Equal(1, await db.OutboxActions.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task EditAfterCommandReceived_NeverReprocessedOrReinterpreted()
    {
        var (db, harness, comment, _) = await NewCaseAsync("@jsxhuz /plan extra context");
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        // first cycle: transient auth — command stays received
        harness.Handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });
        await RunAsync(db, harness);
        Assert.Equal(AgentCommandStatuses.Received,
            (await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId)).Status);

        // the user edits the comment (row updated like the sync would)
        var storedComment = await db.GitHubComments.SingleAsync(c => c.Id == comment.Id);
        storedComment.Body = "@jsxhuz /stop";
        storedComment.UpdatedAtGithub = storedComment.CreatedAtGithub.AddMinutes(1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // second cycle: auth succeeds — the original parse is preserved, no command_edited reinterpretation
        harness.Handler.EnqueueResponse(PermissionResponse("write", "write"));
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        // task 07: admitted after successful authorization → queued; original parse still the frozen one
        Assert.Equal(AgentCommandStatuses.Queued, command.Status);
        Assert.Equal(AgentCommandNames.Plan, command.CommandName);
        Assert.Equal("@jsxhuz /plan extra context", command.SourceBodySnapshot);
    }

    // ---- repository policy ----

    [Fact]
    public async Task PausedRepository_PlanRejected_RepositoryPaused()
    {
        var (db, harness, comment, _) = await NewCaseAsync(enabled: false);
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.RepositoryPaused, command.RejectionReason);
    }

    [Fact]
    public async Task PausedRepository_StatusStillProcessed()
    {
        // decision 75 + task 08: control commands are unaffected by a repository pause — /status works
        var (db, harness, comment, _) = await NewCaseAsync(body: "@jsxhuz /status", enabled: false);
        AuthStub200(harness.Handler);
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Processed, command.Status);
        Assert.Contains("No agent workflow started.", (await db.OutboxActions.AsNoTracking().SingleAsync()).Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotPermittedCommand_Rejected()
    {
        const string config = """{"allowlist": {"enabled": false, "logins": []}, "permitted_commands": ["status"], "permitted_work_item_types": ["issue", "pull_request"], "authorization": {"minimum_permission": "write"}}""";
        var (db, harness, comment, _) = await NewCaseAsync(configuration: config);
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.NotPermittedCommand, command.RejectionReason);
        // owner decision B: the reply derives the permitted list from the repository config
        var reply = await db.OutboxActions.AsNoTracking().SingleAsync();
        Assert.Contains("(permitted: `/status`)", reply.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOnPullRequest_Rejected_WrongTargetType()
    {
        var (db, harness, comment, _) = await NewCaseAsync(pullRequest: true);
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.WrongTargetType, command.RejectionReason);
    }

    [Fact]
    public async Task ReviewOnIssue_Rejected_WrongTargetType()
    {
        var (db, harness, comment, _) = await NewCaseAsync(body: "@jsxhuz /review");
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.WrongTargetType, command.RejectionReason);
    }

    [Fact]
    public async Task WorkItemTypeNotPermitted_Rejected()
    {
        const string config = """{"allowlist": {"enabled": false, "logins": []}, "permitted_commands": ["plan", "code", "review", "test_cases", "status", "stop", "help", "retry"], "permitted_work_item_types": ["issue"], "authorization": {"minimum_permission": "write"}}""";
        var (db, harness, comment, _) = await NewCaseAsync(body: "@jsxhuz /status", pullRequest: true, configuration: config);
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.WorkItemTypeNotPermitted, command.RejectionReason);
    }

    // ---- authorization ----

    [Fact]
    public async Task Allowlist_NonMemberRejected()
    {
        const string config = """{"allowlist": {"enabled": true, "logins": ["bob"]}, "permitted_commands": ["plan", "code", "review", "test_cases", "status", "stop", "help", "retry"], "permitted_work_item_types": ["issue", "pull_request"], "authorization": {"minimum_permission": "write"}}""";
        var (db, harness, comment, _) = await NewCaseAsync(configuration: config, authorLogin: "Alice");
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.NotInAllowlist, command.RejectionReason);
    }

    [Fact]
    public async Task PermissionLookup404_Rejected_UserNotCollaborator()
    {
        var (db, harness, comment, _) = await NewCaseAsync();
        harness.Handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") });
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.UserNotCollaborator, command.RejectionReason);
    }

    [Fact]
    public async Task PermissionLookupTriage_Rejected_InsufficientPermission()
    {
        var (db, harness, comment, _) = await NewCaseAsync();
        harness.Handler.EnqueueResponse(PermissionResponse("read", "triage"));
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Rejected, command.Status);
        Assert.Equal(AgentCommandRejectionReasons.InsufficientPermission, command.RejectionReason);
    }

    [Fact]
    public async Task TransientError_CommandStaysReceived_EventRetries()
    {
        var (db, harness, comment, _) = await NewCaseAsync();
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        harness.Handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") });
        await RunAsync(db, harness);
        harness.Handler.EnqueueResponse(PermissionResponse("write", "write"));
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        // task 07: the retried authorization succeeds and admission admits → queued
        Assert.Equal(AgentCommandStatuses.Queued, command.Status);
        Assert.Equal(GitHubEventStatuses.Processed, (await EventAsync(db, comment)).ProcessingStatus);
    }

    [Fact]
    public async Task RateLimited429_CommandStaysReceived()
    {
        var (db, harness, comment, _) = await NewCaseAsync();
        var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{}") };
        response.Headers.Add("Retry-After", "12");
        harness.Handler.EnqueueResponse(response);
        await RunAsync(db, harness);
        // the cycle honors the guidance pause — an immediate repeat must not touch anything
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Received, command.Status);
        var githubEvent = await EventAsync(db, comment);
        Assert.Equal(GitHubEventStatuses.Pending, githubEvent.ProcessingStatus);
        Assert.NotNull(githubEvent.ProcessingError);
        Assert.Empty(await db.OutboxActions.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RevokedPat401_SystemConfigError_StaysReceived()
    {
        var (db, harness, comment, _) = await NewCaseAsync();
        harness.Handler.EnqueueResponse(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("{}") });
        await RunAsync(db, harness);

        var command = await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId);
        Assert.Equal(AgentCommandStatuses.Received, command.Status);
        Assert.Empty(await db.OutboxActions.AsNoTracking().ToListAsync());
        Assert.Equal(1, harness.Metrics.AuthorizationSystemErrorsTotal);
    }

    // ---- gate / loop mechanics ----

    [Fact]
    public async Task ColdStartGate_DefersProcessing()
    {
        var (db, harness, comment, _) = await NewCaseAsync();
        var deferredState = new ConfigurationState(); // not synchronized
        var deferred = CommandTestSupport.NewProcessor(postgres.ConnectionString, deferredState);
        try
        {
            await deferred.Processor.RunCycleAsync(default);
        }
        finally
        {
            await deferred.Services.DisposeAsync();
        }
        db.ChangeTracker.Clear();

        Assert.Empty(await db.AgentCommands.AsNoTracking().ToListAsync());
        Assert.Equal(GitHubEventStatuses.Pending, (await EventAsync(db, comment)).ProcessingStatus);
    }

    [Fact]
    public async Task OrphanProcessingRowsRecovered()
    {
        var (db, harness, comment, _) = await NewCaseAsync();
        await CommandTestSupport.SeedProfileAsync(db, OrchestrationTaskTypes.Plan);
        var githubEvent = await EventAsync(db, comment);
        var rawEvent = await db.GitHubEvents.SingleAsync(e => e.Id == githubEvent.Id);
        rawEvent.ProcessingStatus = GitHubEventStatuses.Processing; // crashed mid-batch leftover
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        harness.Handler.EnqueueResponse(PermissionResponse("admin", "admin"));

        await RunAsync(db, harness);

        Assert.Equal(GitHubEventStatuses.Processed,
            (await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == rawEvent.Id)).ProcessingStatus);
        Assert.Equal(AgentCommandStatuses.Queued,
            (await db.AgentCommands.AsNoTracking().SingleAsync(c => c.GithubCommentId == comment.GithubCommentId)).Status);
    }

    [Fact]
    public async Task MalformedEventPayload_MarkedFailed()
    {
        var (db, harness, comment, _) = await NewCaseAsync();
        var githubEvent = await EventAsync(db, comment);
        var rawEvent = await db.GitHubEvents.SingleAsync(e => e.Id == githubEvent.Id);
        rawEvent.Payload = """{"issue_number": 1}"""; // no comment_id
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await RunAsync(db, harness); // must not throw

        var after = await db.GitHubEvents.AsNoTracking().SingleAsync(e => e.Id == rawEvent.Id);
        Assert.Equal(GitHubEventStatuses.Failed, after.ProcessingStatus);
        Assert.NotNull(after.ProcessingError);
    }

    [Fact]
    public async Task PermissionCacheHitsWithinTtl()
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(db, body: "@jsxhuz /plan extra");
        await CommandTestSupport.SeedCommandEventAsync(db, comment, workItem);
        // a second command from the same user on another comment of the same repository
        var (_, workItem2, comment2) = await CommandTestSupport.SeedAsync(db, body: "@jsxhuz /status please", existingRepository: repo);
        await CommandTestSupport.SeedCommandEventAsync(db, comment2, workItem2);
        db.ChangeTracker.Clear();
        var harness = CommandTestSupport.NewProcessor(postgres.ConnectionString);
        harness.Handler.EnqueueResponse(PermissionResponse("admin", "admin"));

        await RunAsync(db, harness);

        // one HTTP permission request for both commands (60 s cache), both authorized
        Assert.Single(harness.Handler.Requests);
        Assert.Equal(2, harness.Metrics.CommandsAuthorizedTotal);
        await harness.Services.DisposeAsync();
    }

    // ---- helpers ----

    /// <summary>Db tracked fresh against the fixture so no stale rows linger between harness runs.</summary>
    private async Task RunAsync(AppDbContext db, ProcessorHarness harness)
    {
        db.ChangeTracker.Clear();
        await harness.Processor.RunCycleAsync(default);
    }

    private async Task<(AppDbContext db, ProcessorHarness harness, GitHubComment comment, WorkItem workItem)> NewCaseAsync(
        string? body = null,
        bool pullRequest = false,
        string? configuration = null,
        string? authorLogin = "alice",
        bool enabled = true,
        bool editedAlready = false)
    {
        await postgres.ResetAsync();
        var db = postgres.NewDbContext();
        var (repo, workItem, comment) = await CommandTestSupport.SeedAsync(
            db, pullRequest: pullRequest, configuration: configuration,
            authorLogin: authorLogin, enabled: enabled, body: body, editedAlready: editedAlready);
        await CommandTestSupport.SeedCommandEventAsync(db, comment, workItem);
        db.ChangeTracker.Clear();
        var harness = CommandTestSupport.NewProcessor(postgres.ConnectionString);
        return (db, harness, comment, workItem);
    }

    private static HttpResponseMessage PermissionResponse(string permission, string roleName) =>
        new(HttpStatusCode.OK) { Content = new StringContent($$"""{"permission": "{{permission}}", "role_name": "{{roleName}}"}""") };

    private static void AuthStub200(PollerTestSupport.StubHandler handler) =>
        handler.EnqueueResponse(PermissionResponse("admin", "admin"));

    private static async Task<GitHubEvent> EventAsync(AppDbContext db, GitHubComment comment) =>
        await db.GitHubEvents.AsNoTracking().SingleAsync(e =>
            e.EventType == GitHubEventTypes.IssueCommentCreated
            && e.DedupeKey == $"ic:{comment.GithubCommentId}:created");
}
