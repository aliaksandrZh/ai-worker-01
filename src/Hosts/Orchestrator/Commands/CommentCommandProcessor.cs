using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Config;
using PiAgent.Orchestrator.Orchestration;
using PiAgent.Platform.Workers;

namespace PiAgent.Orchestrator.Commands;

/// <summary>
/// Consumes `issue_comment_created` GitHubEvent rows (Poller-written, `pending`) and turns synchronized
/// comments into persisted `AgentCommand` rows: the shared acceptance stage (CommandIngestion, also
/// used by the task-08 `POST /internal/commands` hook) runs the grammar and policy checks from the
/// synced `Repository.Configuration` (repository pause, permitted commands/types, command-target types),
/// then this file runs the allowlist + collaborator-permission check (decision 66).
/// A comment produces at most one AgentCommand ever (unique GitHub comment ID =
/// idempotency key, 02 §11); event replays after a crash are no-ops. Rejections persist their reason
/// and enqueue exactly one Outbox reply (ticket 03 delivers). After authorization: task-producing
/// commands are admitted (task 07); control commands /status and /help are answered by
/// ControlCommandHandler (task 08) with exactly one Outbox reply and no Workflow/Task/Run writes
/// (decision 29); /stop (14) and /retry (15) remain `authorized` for later tickets. Transient
/// permission-lookup failures
/// leave the command `received` with the event released back to `pending` — retried on a later tick
/// (decision 66); a 401/non-rate-limit 403 is a system configuration error (log + metric, never a
/// per-user rejection, no PAT fallback — decisions 65/66). The loop only claims
/// `issue_comment_created`: edits never retrigger (decision 39) and review comments are context only
/// (06 §1). Cold-start gate: nothing is claimed or persisted until configuration synchronization
/// completes. Single-instance V1: claim + orphan recovery rely on the Orchestrator being one process;
/// the durable-job leases + queue consumer arrived with task 07 and serialize work the same way.
/// </summary>
public sealed class CommentCommandProcessor(
    IServiceScopeFactory scopeFactory,
    ConfigurationState configurationState,
    CollaboratorPermissionGate permissionGate,
    CommandMetrics metrics,
    ILogger<CommentCommandProcessor> logger) : IWorkerCycle
{
    private const int BatchSize = 32;
    private static readonly TimeSpan MaxPause = TimeSpan.FromHours(1);
    /// <summary>In-memory pause after a rate-limited permission check (Retry-After honored, capped 1 h).</summary>
    private DateTime _pauseUntilUtc = DateTime.MinValue;

    public async Task RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!configurationState.Synchronized || string.IsNullOrEmpty(configurationState.BotLogin))
            return; // cold-start gate (01 §13): defer — claim nothing, persist nothing
        if (DateTime.UtcNow < _pauseUntilUtc)
            return; // the permission lookup is honoring rate-limit backoff — this tick defers

        List<(Guid EventId, string Payload)> batch;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            // Crash recovery: a `processing` comment event cannot survive a tick (single-instance V1),
            // so leftovers at tick start are released for the claim below — saved FIRST so the claim
            // query (which runs against the committed DB state) sees them.
            var orphans = await db.GitHubEvents
                .Where(e => e.EventType == GitHubEventTypes.IssueCommentCreated
                    && e.ProcessingStatus == GitHubEventStatuses.Processing)
                .ToListAsync(cancellationToken);
            if (orphans.Count > 0)
            {
                foreach (var orphan in orphans)
                    orphan.ProcessingStatus = GitHubEventStatuses.Pending;
                await db.SaveChangesAsync(cancellationToken);
            }

            var claimed = await db.GitHubEvents
                .Where(e => e.EventType == GitHubEventTypes.IssueCommentCreated
                    && e.ProcessingStatus == GitHubEventStatuses.Pending)
                .OrderBy(e => e.ReceivedAt).ThenBy(e => e.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            foreach (var githubEvent in claimed)
                githubEvent.ProcessingStatus = GitHubEventStatuses.Processing;
            await db.SaveChangesAsync(cancellationToken);
            batch = [.. claimed.Select(e => (e.Id, e.Payload))];
        }

        foreach (var (eventId, payload) in batch)
        {
            try
            {
                await ProcessEventAsync(eventId, payload, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "processing comment event {eventId} failed — releasing for retry", eventId);
                await ReleaseEventForRetryAsync(eventId, exception.Message, cancellationToken);
            }
        }
    }

    private async Task ProcessEventAsync(Guid eventId, string payload, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var githubEvent = await db.GitHubEvents.SingleOrDefaultAsync(e => e.Id == eventId, cancellationToken);
            if (githubEvent is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return; // vanished between claim and processing
            }

            if (!TryReadCommentId(payload, out var githubCommentId))
            {
                githubEvent.ProcessingStatus = GitHubEventStatuses.Failed;
                githubEvent.ProcessedAt = DateTime.UtcNow;
                githubEvent.ProcessingError = "event payload carries no usable comment_id";
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var comment = await db.GitHubComments.AsNoTracking()
                .SingleOrDefaultAsync(c => c.GithubCommentId == githubCommentId && c.Kind == GitHubCommentKinds.IssueComment, cancellationToken);
            var workItem = comment is null
                ? null
                : await db.WorkItems.AsNoTracking().SingleOrDefaultAsync(w => w.Id == comment.WorkItemId, cancellationToken);
            var repository = workItem is null
                ? null
                : await db.Repositories.AsNoTracking().SingleOrDefaultAsync(r => r.Id == workItem.RepositoryId, cancellationToken);
            if (comment is null || workItem is null || repository is null)
            {
                githubEvent.ProcessingStatus = GitHubEventStatuses.Failed;
                githubEvent.ProcessedAt = DateTime.UtcNow;
                githubEvent.ProcessingError = $"comment/work item/repository row missing for comment {githubCommentId} (referential integrity bug)";
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            if (string.Equals(comment.AuthorLogin, configurationState.BotLogin, StringComparison.Ordinal))
            {
                // bot-authored comments never trigger (06 §1) — no row, no reply
                MarkProcessed(githubEvent);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var command = await db.AgentCommands
                .SingleOrDefaultAsync(c => c.GithubCommentId == githubCommentId, cancellationToken);

            if (command is null)
            {
                var accepted = await CommandIngestion.TryAcceptAsync(
                    db, metrics, configurationState.BotLogin!,
                    comment.GithubCommentId, comment.AuthorLogin, comment.Body,
                    comment.UpdatedAtGithub != comment.CreatedAtGithub,
                    workItem, repository, cancellationToken);
                if (accepted is null || accepted.Status != AgentCommandStatuses.Received)
                {
                    // mentioned but context-only (decision 45), or fresh and already rejected
                    MarkProcessed(githubEvent);
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return;
                }
                command = accepted;
            }
            else if (command.Status != AgentCommandStatuses.Received)
            {
                MarkProcessed(githubEvent);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return; // idempotent replay: the command was already handled (04 §2)
            }

            // Authorization-only retry for a `received` command: no reparse, no policy recheck —
            // exactly the deferred check (decision 66; claim-time rechecks are ticket 07's contract).
            var deferred = await AuthorizeCommandAsync(db, command, comment, workItem, repository, cancellationToken);
            if (deferred is { } pause)
            {
                // command stays `received`; the event is released to be retried after the pause
                githubEvent.ProcessingStatus = GitHubEventStatuses.Pending;
                githubEvent.ProcessingError = "authorization check deferred (transient GitHub error)";
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                if (pause > DateTime.UtcNow)
                    _pauseUntilUtc = pause;
                return;
            }

            // Admission (task 07): gate + Workflow/Task/Job creation for a command we just authorized,
            // in this same transaction; non-null outcome → existing rejection path (reason + Outbox reply).
            // Commands rejected by the authorization gate keep their reason — never re-admitted here.
            var outcome = command.Status == AgentCommandStatuses.Authorized
                ? await CommandAdmission.AdmitAsync(db, command, workItem, repository,
                    configurationState.BotLogin, cancellationToken)
                : null;
            if (outcome is { } rejection)
                await RejectAsync(db, command, workItem, repository, rejection.Reason, rejection.Detail, cancellationToken);
            else if (command.Status == AgentCommandStatuses.Authorized && command.CommandName is AgentCommandNames.Status or AgentCommandNames.Help or AgentCommandNames.Stop or AgentCommandNames.Retry)
                // Control commands: /status//help answered in place (task 08, decisions 29/71/75); /stop is
                // a state-changing control path handled by StopCommandHandler (ticket 14, decisions 38/61)
                // and /retry by RetryCommandHandler (ticket 15, decisions 27/65/70). The runtime-signal
                // client rides the scope (same transaction precedent as the collaborator permission lookup HTTP).
                await ControlCommandHandler.HandleAsync(db, command, workItem, repository,
                    scope.ServiceProvider.GetService<RuntimeApiClient>(), metrics, cancellationToken);

            MarkProcessed(githubEvent);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private void MarkProcessed(GitHubEvent githubEvent)
    {
        githubEvent.ProcessingStatus = GitHubEventStatuses.Processed;
        githubEvent.ProcessedAt = DateTime.UtcNow;
        githubEvent.ProcessingError = null;
    }

    /// <summary>Allowlist + permission check for a `received` command. Returns null to continue
    /// (command reached `authorized`, `rejected`, or stays `received` only never) — or the pause
    /// instant when the check must be deferred (transient/system configuration error).</summary>
    private async Task<DateTime?> AuthorizeCommandAsync(
        AppDbContext db,
        AgentCommand command,
        GitHubComment comment,
        WorkItem workItem,
        Repository repository,
        CancellationToken cancellationToken)
    {
        var allowlist = CollaboratorPermissionGate.EvaluateAllowlist(repository.Configuration, comment.AuthorLogin ?? "");
        if (allowlist == AllowlistVerdict.Denied)
        {
            await RejectAsync(db, command, workItem, repository,
                AgentCommandRejectionReasons.NotInAllowlist, comment.AuthorLogin ?? "", cancellationToken);
            return null;
        }

        var config = RepoCommandConfigParser.Parse(repository);
        var outcome = await permissionGate.CheckAsync(
            repository.Owner, repository.Name, comment.AuthorLogin ?? "", config.MinimumPermission, cancellationToken);

        switch (outcome.Kind)
        {
            case PermissionKind.Allowed:
                command.Status = AgentCommandStatuses.Authorized;
                metrics.RecordAuthorized();
                return null;

            case PermissionKind.UserNotFound:
                await RejectAsync(db, command, workItem, repository,
                    AgentCommandRejectionReasons.UserNotCollaborator, comment.AuthorLogin ?? "", cancellationToken);
                return null;

            case PermissionKind.InsufficientPermission:
                await RejectAsync(db, command, workItem, repository,
                    AgentCommandRejectionReasons.InsufficientPermission, comment.AuthorLogin ?? "", cancellationToken);
                return null;

            default:
                // Transient → stays `received`, event released, check deferred (decision 66).
                // SystemConfigError → the PAT problem: same deferral + surfaced system error;
                // never a per-user rejection and never a different PAT (decisions 65/66).
                if (outcome.Kind == PermissionKind.SystemConfigError)
                    metrics.RecordAuthorizationSystemError();
                return outcome.RetryAfter is { } retryAfter
                    ? DateTime.UtcNow + (retryAfter > MaxPause ? MaxPause : retryAfter)
                    : DateTime.UtcNow;
        }
    }

    /// <summary>Rejects an already-persisted command row (authorization path) in the caller's transaction.</summary>
    private async Task RejectAsync(
        AppDbContext db, AgentCommand command, WorkItem workItem, Repository repository,
        string reason, string detail, CancellationToken cancellationToken)
    {
        command.Status = AgentCommandStatuses.Rejected;
        command.RejectionReason = reason;
        command.ProcessedAt = DateTime.UtcNow;
        metrics.RecordRejected(reason);
        await EnqueueReplyAsync(db, repository, workItem, reason, detail, permittedCommands: null, cancellationToken);
    }

    /// <summary>Random idempotency key is safe: the reply is enqueued inside the command row's own
    /// transaction, and the unique agent_command row makes every replay a no-op.</summary>
    private async Task EnqueueReplyAsync(
        AppDbContext db, Repository repository, WorkItem workItem, string reason, string detail,
        IReadOnlyList<string>? permittedCommands, CancellationToken cancellationToken) =>
        await new OutboxActionStore(db).EnqueueAsync(
            new OutboxEnqueueRequest(repository.Id, workItem.Id, null, workItem.Number,
                AgentCommandRejectionReplies.Text(reason, detail, permittedCommands), Guid.NewGuid()), cancellationToken);

    private async Task ReleaseEventForRetryAsync(Guid eventId, string error, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.GitHubEvents
            .Where(e => e.Id == eventId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(e => e.ProcessingStatus, GitHubEventStatuses.Pending)
                .SetProperty(e => e.ProcessingError, error), cancellationToken);
    }

    private static bool TryReadCommentId(string payload, out long githubCommentId)
    {
        githubCommentId = 0;
        try
        {
            using var json = JsonDocument.Parse(payload);
            if (json.RootElement.TryGetProperty("comment_id", out var commentId)
                && commentId.ValueKind == JsonValueKind.Number
                && commentId.TryGetInt64(out var value))
            {
                githubCommentId = value;
                return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
