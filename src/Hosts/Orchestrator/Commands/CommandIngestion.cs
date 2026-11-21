using Microsoft.EntityFrameworkCore;
using PiAgent.Infrastructure.Persistence;
using PiAgent.Orchestrator.Orchestration;

namespace PiAgent.Orchestrator.Commands;

/// <summary>
/// The command-acceptance stage shared by both ingestion paths (task 06 poller comments and task 08's
/// `POST /internal/commands` test hook): the already-edited check (real comments only), grammar parse,
/// and policy checks from the synced repository configuration (pause, permitted commands/types, command-
/// target types). A comment produces at most one AgentCommand ever (unique GitHub comment ID = idempotency
/// key, 02 §11); rejections persist their reason and enqueue exactly one Outbox reply (ticket 03 delivers).
/// Only the task-producing subset and /retry are rejected while the repository is paused (decision 75) —
/// control commands pass and are handled by ControlCommandHandler after authorization.
/// </summary>
public static class CommandIngestion
{
    /// <summary>Valid WorkItem types per command (06 §2 table); public because /help filters on it.</summary>
    public static readonly Dictionary<string, string[]> ValidTargetTypes = new()
    {
        [AgentCommandNames.Plan] = [WorkItemTypes.Issue],
        [AgentCommandNames.Code] = [WorkItemTypes.Issue],
        [AgentCommandNames.TestCases] = [WorkItemTypes.Issue],
        [AgentCommandNames.Review] = [WorkItemTypes.PullRequest],
        [AgentCommandNames.Status] = [WorkItemTypes.Issue, WorkItemTypes.PullRequest],
        [AgentCommandNames.Stop] = [WorkItemTypes.Issue, WorkItemTypes.PullRequest],
        [AgentCommandNames.Help] = [WorkItemTypes.Issue, WorkItemTypes.PullRequest],
        [AgentCommandNames.Retry] = [WorkItemTypes.Issue, WorkItemTypes.PullRequest],
    };

    /// <summary>First-sight handling: the already-edited check (real comments only — the hook passes
    /// false) → grammar → policy checks. Returns the persisted AgentCommand (received, or rejected for
    /// a grammar/policy reason), or null when the text is context only (no usable directive). A
    /// rejection reply goes through the Outbox inside the caller's transaction.</summary>
    public static async Task<AgentCommand?> TryAcceptAsync(
        AppDbContext db,
        CommandMetrics metrics,
        string botLogin,
        long commentId,
        string? authorLogin,
        string body,
        bool editedAlready,
        WorkItem workItem,
        Repository repository,
        CancellationToken cancellationToken)
    {
        if (editedAlready)
            return await PersistRejectedAsync(db, metrics, commentId, authorLogin, body, workItem, repository,
                AgentCommandRejectionReasons.CommandEdited, "already edited when first seen",
                commandName: null, cancellationToken);

        var config = RepoCommandConfigParser.Parse(repository);
        var parsed = CommandParser.Parse(body, botLogin);
        switch (parsed)
        {
            case NoCommandDetected:
                return null;

            case CommandRejectedParse rejected:
                var (reason, detail) = rejected.Reason switch
                {
                    CommandRejection.UnknownCommand => (AgentCommandRejectionReasons.UnknownCommand, rejected.Detail),
                    CommandRejection.AmbiguousCommand => (AgentCommandRejectionReasons.AmbiguousCommand, rejected.Detail),
                    CommandRejection.MalformedCommand => (AgentCommandRejectionReasons.MalformedCommand, rejected.Detail),
                    CommandRejection.CommandInQuoteOrCodeBlock => (AgentCommandRejectionReasons.CommandInQuoteOrCodeBlock, rejected.Detail),
                    _ => (AgentCommandRejectionReasons.MalformedCommand, rejected.Detail),
                };
                // owner decision B: the supported-commands line in the reply is derived from the
                // repository's permitted set (unknown commands), not hand-maintained
                var permittedForReply = reason == AgentCommandRejectionReasons.UnknownCommand
                    ? config.PermittedCommands
                    : null;
                return await PersistRejectedAsync(db, metrics, commentId, authorLogin, body, workItem, repository,
                    reason, detail, commandName: null,
                    permittedCommands: permittedForReply, cancellationToken: cancellationToken);

            case CommandParsed commandParsed:
                var name = commandParsed.Name;

                if (!repository.IsEnabled && AgentCommandNames.TaskProducingOrExecution.Contains(name))
                    return await PersistRejectedAsync(db, metrics, commentId, authorLogin, body, workItem, repository,
                        AgentCommandRejectionReasons.RepositoryPaused, name, name, cancellationToken); // decision 75

                if (!config.PermittedCommands.Contains(name))
                    return await PersistRejectedAsync(db, metrics, commentId, authorLogin, body, workItem, repository,
                        AgentCommandRejectionReasons.NotPermittedCommand, name, name,
                        permittedCommands: config.PermittedCommands, cancellationToken: cancellationToken);

                if (!ValidTargetTypes[name].Contains(workItem.Type))
                    return await PersistRejectedAsync(db, metrics, commentId, authorLogin, body, workItem, repository,
                        AgentCommandRejectionReasons.WrongTargetType, name, name, cancellationToken);

                if (!config.PermittedWorkItemTypes.Contains(workItem.Type))
                    return await PersistRejectedAsync(db, metrics, commentId, authorLogin, body, workItem, repository,
                        AgentCommandRejectionReasons.WorkItemTypeNotPermitted, workItem.Type, name, cancellationToken);

                metrics.RecordReceived();
                var received = new AgentCommand
                {
                    Id = Guid.NewGuid(),
                    WorkItemId = workItem.Id,
                    GithubCommentId = commentId,
                    AuthorLogin = authorLogin,
                    CommandName = name,
                    Arguments = "{}",
                    ParserVersion = "1",
                    Status = AgentCommandStatuses.Received,
                    SourceBodySnapshot = body,
                    CreatedAt = DateTime.UtcNow,
                    ParsedAt = DateTime.UtcNow,
                };
                db.AgentCommands.Add(received);
                return received;
        }

        return null; // unreachable
    }

    /// <summary>Persists a fresh rejected row + exactly one Outbox reply in the caller's transaction.</summary>
    private static async Task<AgentCommand> PersistRejectedAsync(
        AppDbContext db, CommandMetrics metrics, long commentId, string? authorLogin, string body,
        WorkItem workItem, Repository repository,
        string reason, string detail, string? commandName, CancellationToken cancellationToken,
        IReadOnlyList<string>? permittedCommands = null)
    {
        var command = new AgentCommand
        {
            Id = Guid.NewGuid(),
            WorkItemId = workItem.Id,
            GithubCommentId = commentId,
            AuthorLogin = authorLogin,
            CommandName = commandName,
            Arguments = "{}",
            ParserVersion = "1",
            Status = AgentCommandStatuses.Rejected,
            SourceBodySnapshot = body,
            CreatedAt = DateTime.UtcNow,
            ParsedAt = DateTime.UtcNow,
            ProcessedAt = DateTime.UtcNow,
            RejectionReason = reason,
        };
        db.AgentCommands.Add(command);
        metrics.RecordRejected(reason);
        await new OutboxActionStore(db).EnqueueAsync(
            new OutboxEnqueueRequest(repository.Id, workItem.Id, null, workItem.Number,
                AgentCommandRejectionReplies.Text(reason, detail, permittedCommands), Guid.NewGuid()), cancellationToken);
        return command;
    }
}
