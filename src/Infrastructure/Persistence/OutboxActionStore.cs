using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace PiAgent.Infrastructure.Persistence;

/// <summary>One orchestration-owned comment the Outbox should publish to a GitHub work item.</summary>
public sealed record OutboxEnqueueRequest(Guid RepositoryId, Guid WorkItemId, Guid? TaskId, long IssueNumber, string Text, Guid? IdempotencyKey);

/// <summary>
/// Transactional operations over outbox_action. Claim is atomic via
/// SELECT … FOR UPDATE SKIP LOCKED inside one transaction — a leaked row (crashed dispatcher)
/// becomes claimable again when its lease expires; all post-claim transitions are owner-guarded
/// so a stale dispatcher can never touch a row another dispatcher now owns.
/// </summary>
public sealed class OutboxActionStore(AppDbContext db)
{
    public async Task<OutboxAction> EnqueueAsync(OutboxEnqueueRequest request, CancellationToken cancellationToken)
    {
        if (request.IssueNumber <= 0)
            throw new ArgumentException($"issue_number must be positive, got {request.IssueNumber}", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("text must be non-empty", nameof(request));
        if (!await db.Repositories.AnyAsync(r => r.Id == request.RepositoryId, cancellationToken))
            throw new ArgumentException($"repository {request.RepositoryId} does not exist", nameof(request));

        var payload = JsonSerializer.Serialize(new { issue_number = request.IssueNumber, text = request.Text });
        var now = DateTime.UtcNow;

        if (request.IdempotencyKey is { } suppliedKey)
        {
            var existing = await db.OutboxActions.AsNoTracking()
                .FirstOrDefaultAsync(a => a.IdempotencyKey == suppliedKey.ToString(), cancellationToken);
            if (existing is not null)
                return existing;
        }

        var action = new OutboxAction
        {
            Id = Guid.NewGuid(),
            RepositoryId = request.RepositoryId,
            WorkItemId = request.WorkItemId,
            TaskId = request.TaskId,
            ActionType = OutboxActionTypes.Comment,
            Payload = payload,
            Status = OutboxStatuses.Pending,
            AvailableAt = now,
            IdempotencyKey = request.IdempotencyKey?.ToString() ?? Guid.NewGuid().ToString(),
            CreatedAt = now,
        };
        db.OutboxActions.Add(action);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return action;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is NpgsqlException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // a concurrent enqueue with the same idempotency key won the insert race
            db.Entry(action).State = EntityState.Detached;
            var winner = await db.OutboxActions.AsNoTracking()
                .FirstAsync(a => a.IdempotencyKey == action.IdempotencyKey, cancellationToken);
            return winner;
        }
    }

    /// <summary>The AI-label replace action: `label = null` clears all four (cancellation/closure).
    /// Idempotent at GitHub by construction (deletes of the other three, 404 = fine), so a fresh
    /// random idempotency key per transition is safe — replays after a crash re-apply the same state.</summary>
    public async Task<OutboxAction> EnqueueAiLabelAsync(AiLabelEnqueueRequest request, CancellationToken cancellationToken)
    {
        if (request.IssueNumber <= 0)
            throw new ArgumentException($"issue_number must be positive, got {request.IssueNumber}", nameof(request));
        if (request.Label is { } label && !AiStateLabels.All.Contains(label))
            throw new ArgumentException($"label '{label}' is not one of the AI state labels", nameof(request));
        if (!await db.Repositories.AnyAsync(r => r.Id == request.RepositoryId, cancellationToken))
            throw new ArgumentException($"repository {request.RepositoryId} does not exist", nameof(request));

        var payload = JsonSerializer.Serialize(new { issue_number = request.IssueNumber, label = request.Label });
        var now = DateTime.UtcNow;
        var action = new OutboxAction
        {
            Id = Guid.NewGuid(),
            RepositoryId = request.RepositoryId,
            WorkItemId = request.WorkItemId,
            TaskId = request.TaskId,
            ActionType = OutboxActionTypes.AiLabel,
            Payload = payload,
            Status = OutboxStatuses.Pending,
            AvailableAt = now,
            IdempotencyKey = Guid.NewGuid().ToString(),
            CreatedAt = now,
        };
        db.OutboxActions.Add(action);
        await db.SaveChangesAsync(cancellationToken);
        return action;
    }

    public async Task<OutboxAction?> ClaimNextAsync(
        Guid dispatcherId, TimeSpan leaseDuration, TimeSpan retryWindow, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var now = DateTime.UtcNow;

        // Per-work-item ordering: an earlier uncompleted action for the same work item always
        // delivers first — the retry backoff must never let a later transition overtake an earlier
        // one (e.g. a delayed Scheduled applying over an already-delivered Finished). Blocked
        // actions release at dead_letter/cancel, so a stuck write cannot wedge the work item forever
        // (bounded by the retry window).
        var claimed = (await db.OutboxActions.FromSql($"""
            SELECT * FROM outbox_action
            WHERE status IN ({OutboxStatuses.Pending}, {OutboxStatuses.Processing})
              AND available_at <= {now}
              AND (locked_until IS NULL OR locked_until < {now})
              AND NOT EXISTS (
                  SELECT 1 FROM outbox_action earlier
                  WHERE earlier.work_item_id = outbox_action.work_item_id
                    AND earlier.status IN ({OutboxStatuses.Pending}, {OutboxStatuses.Processing})
                    AND (earlier.created_at < outbox_action.created_at
                        OR (earlier.created_at = outbox_action.created_at AND earlier.id < outbox_action.id)))
            ORDER BY available_at, id
            LIMIT 1
            FOR UPDATE SKIP LOCKED
            """).ToListAsync(cancellationToken)).FirstOrDefault();

        if (claimed is null)
            return null;

        if (claimed.Attempts > 0 && now - claimed.CreatedAt >= retryWindow)
        {
            claimed.Status = OutboxStatuses.DeadLetter;
            claimed.LastError = $"retry window exhausted after {claimed.Attempts} attempts";
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        claimed.Attempts++;
        claimed.Status = OutboxStatuses.Processing;
        claimed.LockedBy = dispatcherId;
        claimed.LockedUntil = now + leaseDuration;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<bool> MarkCompletedAsync(Guid actionId, Guid dispatcherId, CancellationToken cancellationToken)
    {
        var rows = await db.OutboxActions
            .Where(a => a.Id == actionId
                && a.LockedBy == dispatcherId
                && a.Status == OutboxStatuses.Processing)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(a => a.Status, OutboxStatuses.Completed)
                .SetProperty(a => a.CompletedAt, DateTime.UtcNow)
                .SetProperty(a => a.LockedBy, (Guid?)null)
                .SetProperty(a => a.LockedUntil, (DateTime?)null), cancellationToken);
        return rows == 1;
    }

    public async Task MarkTransientFailureAsync(
        Guid actionId, Guid dispatcherId, string error, TimeSpan delay, CancellationToken cancellationToken)
    {
        await db.OutboxActions
            .Where(a => a.Id == actionId && a.LockedBy == dispatcherId)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(a => a.Status, OutboxStatuses.Pending)
                .SetProperty(a => a.AvailableAt, DateTime.UtcNow + delay)
                .SetProperty(a => a.LastError, error)
                .SetProperty(a => a.LockedBy, (Guid?)null)
                .SetProperty(a => a.LockedUntil, (DateTime?)null), cancellationToken);
    }

    public async Task MarkDeadLetterAsync(Guid actionId, Guid dispatcherId, string error, CancellationToken cancellationToken)
    {
        await db.OutboxActions
            .Where(a => a.Id == actionId && a.LockedBy == dispatcherId)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(a => a.Status, OutboxStatuses.DeadLetter)
                .SetProperty(a => a.LastError, error)
                .SetProperty(a => a.LockedBy, (Guid?)null)
                .SetProperty(a => a.LockedUntil, (DateTime?)null), cancellationToken);
    }

    public async Task<int> CancelPendingForWorkItemAsync(Guid workItemId, CancellationToken cancellationToken)
    {
        return await db.OutboxActions
            .Where(a => a.WorkItemId == workItemId && a.Status == OutboxStatuses.Pending)
            .ExecuteUpdateAsync(updates => updates
                .SetProperty(a => a.Status, OutboxStatuses.Cancelled), cancellationToken);
    }
}
